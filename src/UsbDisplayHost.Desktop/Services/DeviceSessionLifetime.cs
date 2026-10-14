using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Core.Devices;
using System.Threading.Channels;
using System.Collections.Immutable;
using UsbDisplayHost.Core.Telemetry;
using UsbDisplayHost.Core.Transport;
using UsbDisplayHost.Core.Themes;
using UsbDisplayHost.Windows.Usb;

namespace UsbDisplayHost.Desktop.Services;

public enum SessionLifecycleStage { None, Active, Closing, Finalizing, Released, RetainedLocalResult, Unknown }
public sealed record SessionLifecycleSnapshot(long Version, DeviceKey? DeviceKey, long Generation,
    SessionLifecycleStage Stage, bool NativeClosed, AppError? Error);
public sealed record SessionTransportSnapshot(DeviceKey DeviceKey, long Generation, SessionLifecycleStage Stage,
    bool NativeClosed, FrameTransferStatus Transfer, ActiveFrameProducerStatus Producer, FrameRateSnapshot FrameRate);

// 应用持有唯一会话；关闭未知时仍保留整个管线，不能用新的会话覆盖它。
public sealed class DeviceSessionLifetime(DeviceCoordinator coordinator, IErrorReporter reporter)
{
    private readonly object syncRoot = new();
    private ThemeSessionPipeline? owner;
    private IControlChannel? controlOwner;
    private DeviceKey? controlKey;
    private long controlGeneration;
    private bool stopping;
    private bool buildingPipeline;
    private Task pipelineHandoff = Task.CompletedTask;
    private Task<OperationResult<Unit>>? closeTask;
    private SessionLifecycleSnapshot current = new(0, null, 0, SessionLifecycleStage.None, false, null);
    private readonly Channel<SessionLifecycleSnapshot> changes = Channel.CreateBounded<SessionLifecycleSnapshot>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, AllowSynchronousContinuations = false });
    public SessionLifecycleSnapshot Current => Volatile.Read(ref current);
    public Task<OperationResult<DeviceSnapshot>> ExecuteProtocolAsync(DeviceAction action,
        Func<OperationContext, CancellationToken, Task<OperationResult<DeviceObservation>>> execute,
        bool resumeFrames, bool recoverFrames, CancellationToken token, int? brightness = null)
    {
        lock (syncRoot)
        {
            if (owner is null || stopping || closeTask is not null)
            { return Task.FromResult<OperationResult<DeviceSnapshot>>(new OperationResult<DeviceSnapshot>.Failure(
                AppError.Create("ProtocolSessionUnavailable", "主题会话不可用", "重新连接所选设备"))); }
            return owner.ExecuteProtocolAsync(action, execute, resumeFrames, recoverFrames, token, brightness);
        }
    }
    public ChannelReader<SessionLifecycleSnapshot> Reader => changes.Reader;

    public async Task<OperationResult<Unit>> CreateAndAttachAsync(ThemeConnectionSessionWork work, ThemePipelineDependencies dependencies)
    {
        TaskCompletionSource<Unit> finished;
        lock (syncRoot)
        {
            if (work is null || dependencies is null || stopping || owner is not null || buildingPipeline
                || current.Version == long.MaxValue)
            {
                return new OperationResult<Unit>.Failure(AppError.Create("DevicePipelineHandoffUnavailable",
                    "管线正在创建、原会话仍被保留或应用正在退出", "请等待当前操作结束，不覆盖会话所有者"));
            }
            buildingPipeline = true;
            finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pipelineHandoff = finished.Task;
        }
        ThemeSessionPipeline? built = null;
        try
        {
            var result = await work.CreatePipelineAsync(dependencies).ConfigureAwait(false);
            if (result is not OperationResult<ThemeSessionPipeline>.Success success)
            {
                return result switch
                {
                    OperationResult<ThemeSessionPipeline>.Failure failure => new OperationResult<Unit>.Failure(failure.Error),
                    OperationResult<ThemeSessionPipeline>.Unknown unknown => new OperationResult<Unit>.Unknown(unknown.Error),
                    _ => new OperationResult<Unit>.Cancelled()
                };
            }
            built = success.Value;
            var attached = Attach(built);
            if (attached is not OperationResult<Unit>.Success)
            {
                var error = attached is OperationResult<Unit>.Failure failure ? failure.Error
                    : AppError.Create("DevicePipelineHandoffUnconfirmed", "管线接管未确认", "请等待原连接关闭并检查诊断");
                // 接管拒绝后由原连接工作收敛；这里不重复释放其传输或控制句柄。
                built.Coordinator.NotifyDisconnected(built.DeviceKey, built.SessionGeneration, error);
            }
            return attached;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DevicePipelineHandoff");
            var error = new AppError(report.ErrorId, "DevicePipelineHandoffFailed",
                "管线创建或接管未完成，原资源所有者已保留", "请检查诊断并等待原会话关闭，不创建替代连接");
            if (built is not null) { built.Coordinator.NotifyDisconnected(built.DeviceKey, built.SessionGeneration, error); }
            return new OperationResult<Unit>.Failure(error);
        }
        finally
        {
            lock (syncRoot) { buildingPipeline = false; }
            finished.TrySetResult(new());
        }
    }

    public OperationResult<Unit> Attach(ThemeSessionPipeline pipeline)
    {
        lock (syncRoot)
        {
            if (pipeline is null || !ReferenceEquals(pipeline.Coordinator, coordinator))
            {
                return new OperationResult<Unit>.Failure(AppError.Create("DeviceCoordinatorMismatch",
                    "会话不属于当前工作区协调器，未接管会话", "请使用工作区唯一协调器组合会话并处理原所有者"));
            }
            if (stopping || owner is not null || current.Version == long.MaxValue
                || controlOwner is not null && (controlKey != pipeline.DeviceKey || controlGeneration != pipeline.SessionGeneration))
            {
                return new OperationResult<Unit>.Failure(AppError.Create("DeviceSessionOwnerOccupied",
                    "原会话尚未释放或应用正在退出，不能替换会话所有者", "请先确认原会话关闭；未知结果不能直接重开"));
            }
            var transferred = coordinator.TransferConnectionTo(pipeline);
            if (transferred is not OperationResult<Unit>.Success) { return transferred; }
            owner = pipeline;
            closeTask = null;
            PublishLocked(pipeline, SessionLifecycleStage.Active, null);
            return new OperationResult<Unit>.Success(new Unit());
        }
    }

    public OperationResult<Unit> AttachControl(Ad102V2ModeController controller)
    {
        lock (syncRoot)
        {
            try
            {
                if (controller is null || stopping || buildingPipeline || controlOwner is not null
                    || current.Version >= long.MaxValue - 1 || controller.Input.TerminalError is not null
                    || controller.Input.Completion.IsCompleted)
                { return ControlRejected("ControlOwnerUnavailable", "控制会话不可接管或原所有者尚未释放"); }
                var key = controller.Input.DeviceKey;
                var generation = controller.Input.SessionGeneration;
                var snapshot = coordinator.Current;
                if (!coordinator.IsLiveSession(key, generation) || snapshot.Connection != ConnectionState.Ready
                    || snapshot.DeviceKey != key || snapshot.SessionGeneration != generation
                    || snapshot.Capabilities is not { IsProtocolMatched: true } capabilities
                    || !capabilities.Commands.Contains(DeviceCommand.SwitchMode) || snapshot.PendingOperationId is not null
                    || owner is not null && (owner.DeviceKey != key || owner.SessionGeneration != generation))
                { return ControlRejected("ControlSessionMismatch", "控制会话与当前已核验的设备会话不一致"); }
                var created = Ad102V2ControlChannel.Create(controller, coordinator.IsCurrent,
                    coordinator.CaptureControlPowerIntent, reporter);
                if (created is not OperationResult<Ad102V2ControlChannel>.Success accepted)
                {
                    return created is OperationResult<Ad102V2ControlChannel>.Unknown unknown
                        ? new OperationResult<Unit>.Unknown(unknown.Error)
                        : ControlRejected("ControlChannelHandoffRejected", "原控制所有者拒绝通道交接");
                }
                // 在创建队列前保存原通道；后续组合异常不能丢弃已交接的所有者。
                controlOwner = accepted.Value;
                controlKey = key;
                controlGeneration = generation;
                var queued = SerializedControlChannel.Create(accepted.Value, coordinator.IsCurrent, reporter);
                if (queued is not OperationResult<SerializedControlChannel>.Success queue)
                { return new OperationResult<Unit>.Unknown(AppError.Create("ControlQueueHandoffUnconfirmed",
                    "串行队列接管未确认，原通道仍由工作区保留", "正常关闭原会话，不创建替代通道")); }
                controlOwner = queue.Value;
                closeTask = null;
                if (!coordinator.IsLiveSession(key, generation) || coordinator.Current.Version != snapshot.Version)
                {
                    var expired = AppError.Create("ControlHandoffExpired", "交接期间设备状态已变化，原通道保留待关闭",
                        "正常关闭原所有者，不执行控制命令");
                    PublishControlLocked(SessionLifecycleStage.Unknown, false, expired);
                    return new OperationResult<Unit>.Unknown(expired);
                }
                if (owner is not null) { PublishLocked(owner, SessionLifecycleStage.Active, null); }
                else { PublishControlLocked(SessionLifecycleStage.Active, false, null); }
                return new OperationResult<Unit>.Success(new());
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = reporter.Report(exception, "DesktopControlHandoff");
                var error = new AppError(report.ErrorId, "DesktopControlHandoffUnconfirmed", "控制会话交接异常",
                    "保留原所有者与诊断，正常关闭后再处理");
                if (controlOwner is null) { return new OperationResult<Unit>.Failure(error); }
                PublishControlLocked(SessionLifecycleStage.Unknown, false, error);
                return new OperationResult<Unit>.Unknown(error);
            }
        }
    }

    public Task<OperationResult<ControlObservation>> ExecuteControlAsync(ControlRequest request, CancellationToken token)
    {
        lock (syncRoot)
        {
            if (request is null || stopping || closeTask is not null || controlOwner is not SerializedControlChannel
                || current.Stage != SessionLifecycleStage.Active
                || request.Context.DeviceKey != controlKey || request.Context.SessionGeneration != controlGeneration)
            { return Task.FromResult<OperationResult<ControlObservation>>(new OperationResult<ControlObservation>.Failure(
                AppError.Create("DesktopControlUnavailable", "原控制会话不可用，未受理命令", "核实当前会话与操作"))); }
            var permission = coordinator.ValidateControlRequest(request);
            if (permission is OperationResult<Unit>.Failure rejected)
            { return Task.FromResult<OperationResult<ControlObservation>>(new OperationResult<ControlObservation>.Failure(rejected.Error)); }
            return controlOwner.ExecuteAsync(request, token);
        }
    }

    public OperationResult<ModeTransitionLease> PrepareModeTransition(ControlRequest request)
    {
        lock (syncRoot)
        {
            if (request is null || stopping || closeTask is not null || controlOwner is not SerializedControlChannel
                || current.Stage != SessionLifecycleStage.Active || request.Context.DeviceKey != controlKey
                || request.Context.SessionGeneration != controlGeneration)
            { return new OperationResult<ModeTransitionLease>.Failure(AppError.Create("DesktopModeTransitionUnavailable",
                "原模式控制所有者不可用", "保留原操作，不开始目标交接")); }
            return coordinator.ArmModeTransition(request, controlOwner);
        }
    }

    public IAsyncEnumerable<ControlObservation>? ObserveControl(CancellationToken token)
    {
        lock (syncRoot) { return stopping ? null : controlOwner?.ObserveAsync(token); }
    }

    private static OperationResult<Unit> ControlRejected(string code, string reason) =>
        new OperationResult<Unit>.Failure(AppError.Create(code, reason, "保留原控制所有者，不覆盖或重开会话"));

    public bool HasRetainedSession { get { lock (syncRoot) { return owner is not null || controlOwner is not null; } } }
    public PendingThemeSave? PendingSave { get { lock (syncRoot) { return owner?.PendingSave; } } }
    public bool IsBusy { get { lock (syncRoot) { return buildingPipeline || owner?.IsBusy == true
        || controlOwner is SerializedControlChannel { IsBusy: true } || closeTask is { IsCompleted: false }; } } }
    public SessionTransportSnapshot? CaptureTransport()
    {
        lock (syncRoot)
        {
            return owner is null ? null : new(owner.DeviceKey, owner.SessionGeneration, current.Stage,
                owner.IsNativeClosed, owner.TransferStatus, owner.ProducerStatus, owner.FrameRate);
        }
    }
    public CompletedHostFrame? CaptureActiveHostFrame()
    {
        lock (syncRoot) { return owner?.CaptureActiveHostFrame(); }
    }

    public Task<ThemeSessionApplicationCompletion> ApplyThemeAsync(DeviceKey key, long generation, ThemeDraft draft,
        ImmutableArray<MetricSnapshot> metrics, CancellationToken token)
    {
        if (draft is null || draft.DeviceKey != key)
        { return ApplicationRejected("ThemeDraftOwnerMismatch", "主题内容与目标设备不匹配，未受理应用"); }
        return StartApplication(key, generation, pipeline => pipeline.ApplyThemeAsync(draft, metrics, token));
    }

    public Task<ThemeSessionApplicationCompletion> ApplySettingsAsync(DeviceKey key, long generation, DeviceSettingsDraft draft,
        ImmutableArray<MetricSnapshot> metrics, CancellationToken token)
    {
        if (draft is null || draft.DeviceKey != key)
        { return ApplicationRejected("SettingsDraftOwnerMismatch", "设置与目标设备不匹配，未受理应用"); }
        return StartApplication(key, generation, pipeline => pipeline.ApplySettingsAsync(draft, metrics, token));
    }

    private Task<ThemeSessionApplicationCompletion> StartApplication(DeviceKey key, long generation,
        Func<ThemeSessionPipeline, Task<ThemeSessionApplicationCompletion>> start)
    {
        lock (syncRoot)
        {
            if (stopping || buildingPipeline || owner is null || closeTask is not null || owner.IsBusy
                || generation <= 0 || owner.DeviceKey != key || owner.SessionGeneration != generation)
            {
                return ApplicationRejected("DeviceApplicationOwnerUnavailable",
                    "当前设备会话已变化、正在操作或关闭，未受理应用");
            }
            try
            {
                // 关闭和应用共用接管门禁；管线受理后，关闭链会等待该实际操作结算。
                return ObserveApplicationAsync(start(owner));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = reporter.Report(exception, "DeviceApplicationAcceptance");
                return Task.FromResult(new ThemeSessionApplicationCompletion(new OperationResult<DeviceSnapshot>.Failure(
                    new(report.ErrorId, "DeviceApplicationAcceptanceFailed", "应用未能受理，编辑已保留",
                        "请检查输入与诊断，不重复发送未确认操作")), null));
            }
        }
    }

    private async Task<ThemeSessionApplicationCompletion> ObserveApplicationAsync(Task<ThemeSessionApplicationCompletion> operation)
    {
        try { return await operation.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DeviceApplicationCompletion");
            return new(new OperationResult<DeviceSnapshot>.Unknown(new(report.ErrorId, "DeviceApplicationCompletionUnconfirmed",
                "应用结果未确认，编辑已保留", "请核实发送及保存结果，不自动重发")), null);
        }
    }

    private static Task<ThemeSessionApplicationCompletion> ApplicationRejected(string code, string reason) =>
        Task.FromResult(new ThemeSessionApplicationCompletion(new OperationResult<DeviceSnapshot>.Failure(
            AppError.Create(code, reason, "请读取当前设备与会话后重新确认应用")), null));

    public Task<OperationResult<Unit>> CloseDisconnectedAsync(DeviceKey key, long generation)
    {
        lock (syncRoot)
        {
            if (!(owner is not null && owner.DeviceKey == key && owner.SessionGeneration == generation)
                && !(controlOwner is not null && controlKey == key && controlGeneration == generation))
            { return Task.FromResult<OperationResult<Unit>>(new OperationResult<Unit>.Success(new())); }
            // 不将物理断开变成应用退出，也不因再次收到通知重放未知关闭。
            return closeTask ??= Task.Run(() => CloseCoreAsync(null));
        }
    }

    public Task<OperationResult<Unit>> DisconnectAsync(DeviceKey key, long generation)
    {
        lock (syncRoot)
        {
            if (stopping || buildingPipeline || IsBusy
                || !(owner is not null && owner.DeviceKey == key && owner.SessionGeneration == generation
                    || controlOwner is not null && controlKey == key && controlGeneration == generation))
            {
                return Task.FromResult<OperationResult<Unit>>(new OperationResult<Unit>.Failure(
                    AppError.Create("DeviceDisconnectUnavailable", "设备会话已变化或正在处理操作，未受理断开",
                        "请等待当前操作结束并读取设备状态")));
            }
            // 已确认原生关闭后只完成本地交接；未知关闭始终返回原结果。
            if (closeTask is { IsCompletedSuccessfully: true, Result: OperationResult<Unit>.Failure }
                && owner is { IsNativeClosed: true })
            { closeTask = null; }
            return closeTask ??= Task.Run(() => CloseCoreAsync(null));
        }
    }

    public Task<OperationResult<PendingThemeSave>> RetrySaveAsync(Guid retryId)
    {
        lock (syncRoot)
        {
            return owner is not null ? ObserveLocalSaveAsync(owner, owner.RetrySaveAsync(retryId, CancellationToken.None))
                : Task.FromResult<OperationResult<PendingThemeSave>>(new OperationResult<PendingThemeSave>.Failure(
                    AppError.Create("PendingSaveOwnerMissing", "待保存会话已变化", "请读取当前应用结果")));
        }
    }

    public Task<OperationResult<DeviceSnapshot>> SetStreamingAsync(DeviceKey key, long generation, bool resume, CancellationToken token)
    {
        lock (syncRoot)
        {
            if (stopping || buildingPipeline || owner is null || closeTask is not null || owner.IsBusy
                || owner.DeviceKey != key || owner.SessionGeneration != generation || current.Stage != SessionLifecycleStage.Active)
            {
                return Task.FromResult<OperationResult<DeviceSnapshot>>(new OperationResult<DeviceSnapshot>.Failure(
                    AppError.Create("StreamingOwnerUnavailable", "原设备会话已变化或正在操作，未受理推流变更", "请等待当前操作结束并读取设备状态")));
            }
            return ObserveStreamingAsync(owner, owner.SetStreamingAsync(resume, token));
        }
    }

    private async Task<OperationResult<DeviceSnapshot>> ObserveStreamingAsync(ThemeSessionPipeline selected,
        Task<OperationResult<DeviceSnapshot>> operation)
    {
        try { return await operation.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DesktopStreamingCompletion");
            return new OperationResult<DeviceSnapshot>.Unknown(new(report.ErrorId, "DesktopStreamingUnconfirmed",
                "推流变更结果未确认，原会话已保留", "请核实当前状态，不重复操作"));
        }
        finally
        {
            lock (syncRoot)
            {
                if (ReferenceEquals(owner, selected)) { PublishLocked(selected, current.Stage, current.Error); }
            }
        }
    }

    private async Task<OperationResult<PendingThemeSave>> ObserveLocalSaveAsync(ThemeSessionPipeline selected,
        Task<OperationResult<PendingThemeSave>> operation)
    {
        try { return await operation.ConfigureAwait(false); }
        finally
        {
            lock (syncRoot)
            {
                if (ReferenceEquals(owner, selected)) { PublishLocked(selected, current.Stage, current.Error); }
            }
        }
    }

    public Task<OperationResult<Unit>> CloseAsync(Guid? discardSaveId = null)
    {
        lock (syncRoot)
        {
            stopping = true;
            if (closeTask is null || closeTask is { IsCompletedSuccessfully: true, Result: OperationResult<Unit>.Failure })
            {
                closeTask = Task.Run(() => CloseCoreAsync(discardSaveId));
            }
            return closeTask;
        }
    }

    private async Task<OperationResult<Unit>> CloseCoreAsync(Guid? discardSaveId)
    {
        Task handoff;
        lock (syncRoot) { handoff = pipelineHandoff; }
        await handoff.ConfigureAwait(false);
        ThemeSessionPipeline? selected;
        lock (syncRoot)
        {
            selected = owner;
            if (selected is not null) { PublishLocked(selected, selected.IsNativeClosed && controlOwner is null
                ? SessionLifecycleStage.Finalizing : SessionLifecycleStage.Closing, null); }
        }
        if (selected is null) { return await CloseControlAsync().ConfigureAwait(false); }
        try
        {
            var result = await selected.CloseAsync(discardSaveId).ConfigureAwait(false);
            if (selected.IsNativeClosed)
            {
                var controls = await CloseControlAsync().ConfigureAwait(false);
                if (controls is not OperationResult<Unit>.Success)
                {
                    var controlError = controls is OperationResult<Unit>.Unknown unknown ? unknown.Error
                        : controls is OperationResult<Unit>.Failure failure ? failure.Error
                        : AppError.Create("ControlCloseUnconfirmed", "控制会话关闭未确认", "保留原所有者并检查诊断");
                    lock (syncRoot) { PublishLocked(selected, SessionLifecycleStage.Unknown, controlError); }
                    return controls;
                }
            }
            if (result is OperationResult<Unit>.Success && selected.CanReleaseOwnership)
            {
                lock (syncRoot)
                {
                    if (ReferenceEquals(owner, selected)) { PublishLocked(selected, SessionLifecycleStage.Released, null); owner = null; }
                }
                return result;
            }
            if (result is OperationResult<Unit>.Failure failureResult)
            {
                lock (syncRoot) { PublishLocked(selected, selected.IsNativeClosed ? SessionLifecycleStage.RetainedLocalResult : SessionLifecycleStage.Unknown, failureResult.Error); }
                return result;
            }
            var error = result switch
            {
                OperationResult<Unit>.Failure failure => failure.Error,
                OperationResult<Unit>.Unknown unknown => unknown.Error,
                _ => AppError.Create("DeviceSessionOwnershipUnconfirmed", "设备会话释放尚未确认", "请保留窗口与会话所有者并检查诊断")
            };
            lock (syncRoot) { PublishLocked(selected, SessionLifecycleStage.Unknown, error); }
            return new OperationResult<Unit>.Unknown(error);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DeviceSessionLifetimeClose");
            var error = new AppError(report.ErrorId, "DeviceSessionLifetimeCloseUnconfirmed",
                "设备会话关闭未确认，窗口已保留", "请检查诊断，不要直接创建替代会话");
            lock (syncRoot) { PublishLocked(selected, SessionLifecycleStage.Unknown, error); }
            return new OperationResult<Unit>.Unknown(error);
        }
    }
    private async Task<OperationResult<Unit>> CloseControlAsync()
    {
        IControlChannel? selected;
        lock (syncRoot)
        {
            selected = controlOwner;
            if (selected is not null && owner is null) { PublishControlLocked(SessionLifecycleStage.Closing, false, null); }
        }
        if (selected is null) { return new OperationResult<Unit>.Success(new()); }
        OperationResult<Unit> result;
        try { result = await selected.CloseAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DesktopControlClose");
            result = new OperationResult<Unit>.Unknown(new(report.ErrorId, "DesktopControlCloseUnconfirmed",
                "控制会话关闭未确认，原通道已保留", "保留窗口与诊断，不创建替代通道"));
        }
        lock (syncRoot)
        {
            if (ReferenceEquals(controlOwner, selected))
            {
                if (result is OperationResult<Unit>.Success)
                {
                    if (owner is null) { PublishControlLocked(SessionLifecycleStage.Released, true, null); }
                    controlOwner = null;
                    controlKey = null;
                    controlGeneration = 0;
                }
                else if (owner is null)
                {
                    var error = result is OperationResult<Unit>.Unknown unknown ? unknown.Error
                        : result is OperationResult<Unit>.Failure failure ? failure.Error
                        : AppError.Create("ControlCloseUnconfirmed", "控制会话关闭未确认", "保留原所有者与诊断");
                    PublishControlLocked(SessionLifecycleStage.Unknown, false, error);
                }
            }
        }
        return result;
    }

    private void PublishControlLocked(SessionLifecycleStage stage, bool nativeClosed, AppError? error)
    {
        if (current.Version >= long.MaxValue - 1) { return; }
        Volatile.Write(ref current, new(current.Version + 1, controlKey, controlGeneration, stage, nativeClosed, error));
        changes.Writer.TryWrite(current);
    }

    private void PublishLocked(ThemeSessionPipeline pipeline, SessionLifecycleStage stage, AppError? error)
    {
        if (current.Version == long.MaxValue) { return; }
        if (current.Version == long.MaxValue - 1)
        {
            stage = SessionLifecycleStage.Unknown;
            error = AppError.Create("SessionLifecycleVersionExhausted", "会话生命周期版本已耗尽", "请确认会话关闭后重新启动应用");
        }
        Volatile.Write(ref current, new(current.Version + 1, pipeline.DeviceKey, pipeline.SessionGeneration, stage,
            pipeline.IsNativeClosed && controlOwner is null, error));
        changes.Writer.TryWrite(current);
    }
}
