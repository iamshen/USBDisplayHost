using System.Threading.Channels;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Themes;
using UsbDisplayHost.Core.Transport;

namespace UsbDisplayHost.Core.Sessions;

public sealed partial class DeviceCoordinator : IAsyncDisposable
{
    private const int MaximumRequests = 16;
    private const int MaximumSubscribers = 8;
    private readonly object syncRoot = new();
    private readonly Queue<Request> requests = new(MaximumRequests);
    private readonly Dictionary<Guid, Channel<DeviceSnapshot>> subscribers = new(MaximumSubscribers);
    private readonly Channel<byte> signal = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false
    });
    private readonly IErrorReporter errorReporter;
    private readonly TimeProvider timeProvider;
    private readonly Task loop;
    private DeviceSnapshot snapshot = DeviceSnapshot.Disconnected;
    private Request? running;
    private CancellationTokenSource? operationCancellation;
    private Task cancellationCallbacks = Task.CompletedTask;
    private Completion? completion;
    private Invalidation? invalidation;
    private Guid? cancelOperation;
    private bool closing;
    private bool finished;
    private bool drainUnconfirmed;
    private CancellationTokenRegistration userCancellation;
    private Task workerTask = Task.CompletedTask;
    private IDeviceSessionWork? unconfirmedOwner;
    private ThemeConnectionSessionWork? unclaimedConnection;
    private ThemeSessionPipeline? transferredPipeline;
    private Task<OperationResult<Unit>>? unclaimedClose;
    private OperationResult<Unit>? unclaimedCloseResult;
    private long lastThemeRevision = -1;
    private long lastSettingsRevision = -1;
    private ActiveProfile? activeProfile;
    private ProfileCommit? profileCommit;
    private ThemeApplicationBase? themeApplicationBase;
    private ModeTransitionLease? modeTransition;
    private long lastPipelineRevision = -1;
    private sealed record ProfileCommit(ActiveProfile Profile, FrameTransferReceipt Receipt, FrameRateController Pacing,
        TaskCompletionSource<OperationResult<ActiveProfile>> Reply);

    public DeviceCoordinator(IErrorReporter errorReporter, TimeProvider timeProvider)
    {
        this.errorReporter = errorReporter;
        this.timeProvider = timeProvider;
        loop = Task.Run(RunLoopAsync);
    }

    public DeviceSnapshot Current => Volatile.Read(ref snapshot);
    public ActiveProfile? CurrentActiveProfile => Volatile.Read(ref activeProfile);
    public bool RequiresProcessRestart => Volatile.Read(ref unconfirmedOwner) is not null;

    public OperationResult<Unit> TransferConnectionTo(ThemeSessionPipeline pipeline)
    {
        lock (syncRoot)
        {
            if (pipeline is null || closing || finished || invalidation is not null || running is not null || transferredPipeline is not null
                || Current.Connection != ConnectionState.Ready || Current.DeviceKey != pipeline.DeviceKey
                || Current.SessionGeneration != pipeline.SessionGeneration || unclaimedConnection is null || unclaimedClose is not null)
            { return new OperationResult<Unit>.Failure(AppError.Create("ConnectionTransferUnavailable",
                "当前连接尚未就绪、正在关闭或没有待接管资源", "请核实当前会话，不替换已有资源所有者")); }
            try
            {
                var transferred = unclaimedConnection.TransferTo(pipeline);
                if (transferred is OperationResult<Unit>.Success)
                { transferredPipeline = pipeline; unclaimedConnection = null; }
                return transferred;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = errorReporter.Report(exception, "ThemeConnectionTransfer");
                return new OperationResult<Unit>.Failure(new(report.ErrorId, "ThemeConnectionTransferFailed",
                    "连接资源交接核验未完成，原所有者仍持有资源", "请检查诊断，不沿用过期选择或覆盖原所有者"));
            }
        }
    }

    internal OperationResult<Unit> ReleaseTransferredPipeline(ThemeSessionPipeline pipeline)
    {
        lock (syncRoot)
        {
            if (transferredPipeline is null) { return new OperationResult<Unit>.Success(new()); }
            if (!ReferenceEquals(transferredPipeline, pipeline))
            {
                return new OperationResult<Unit>.Failure(AppError.Create("TransferredPipelineOwnerMismatch",
                    "待释放管线不属于当前接管所有者", "请保留实际所有者并检查诊断"));
            }
            // 仅由管线在原生关闭及本地引用交接均成功之后调用。
            transferredPipeline = null;
            return new OperationResult<Unit>.Success(new());
        }
    }

    public bool IsCurrent(OperationContext context)
    {
        lock (syncRoot)
        {
            return !closing && !finished && invalidation is null && Current.DeviceKey == context.DeviceKey
                && Current.SessionGeneration == context.SessionGeneration && running?.Id == context.OperationId
                && running.Revision == context.ContentRevision;
        }
    }

    public bool IsLiveSession(DeviceKey key, long generation)
    {
        lock (syncRoot)
        {
            return !closing && !finished && !drainUnconfirmed && invalidation is null && Current.DeviceKey == key
                && Current.SessionGeneration == generation && Current.Connection != ConnectionState.Disconnected;
        }
    }

    public OperationResult<PowerState> CaptureControlPowerIntent(OperationContext context)
    {
        lock (syncRoot)
        {
            // 意图取自操作受理前的已核验快照，不能被随后到来的未知状态替代。
            if (!IsCurrent(context) || running?.Action != DeviceAction.SwitchMode
                || running.Before is not { Connection: ConnectionState.Ready, Capabilities.IsProtocolMatched: true } before
                || before.DeviceKey != context.DeviceKey || before.SessionGeneration != context.SessionGeneration
                || before.Power is not (PowerState.On or PowerState.Off))
            {
                return new OperationResult<PowerState>.Failure(AppError.Create("ControlPowerIntentUnavailable",
                    "当前切换操作没有已核验的原电源意图", "保留原状态，不发送模式切换"));
            }
            return new OperationResult<PowerState>.Success(before.Power);
        }
    }

    public OperationResult<Unit> ValidateControlRequest(ControlRequest request)
    {
        lock (syncRoot)
        {
            if (request is null || !IsCurrent(request.Context) || running is null
                || cancelOperation == request.Context.OperationId || running.CancellationToken.IsCancellationRequested
                || operationCancellation?.IsCancellationRequested != false
                || running.Before is not { Connection: ConnectionState.Ready, Capabilities.IsProtocolMatched: true } before
                || before.DeviceKey != request.Context.DeviceKey || before.SessionGeneration != request.Context.SessionGeneration)
            { return ControlRejected("ControlOperationExpired", "原控制操作已失效、取消或没有已核验的受理快照"); }
            bool matchesAction = request.Command switch
            {
                DeviceCommand.SwitchMode => running.Action == DeviceAction.SwitchMode,
                DeviceCommand.SetPower => running.Action == DeviceAction.SetPower,
                DeviceCommand.SetBrightness => running.Action == DeviceAction.SetBrightness,
                DeviceCommand.QueryState => running.Action is DeviceAction.SwitchMode or DeviceAction.SetPower or DeviceAction.SetBrightness or DeviceAction.QueryState,
                _ => false
            };
            if (!matchesAction)
            { return ControlRejected("ControlActionMismatch", "控制命令不属于协调器受理的原操作"); }
            if (!before.Capabilities!.Commands.Contains(request.Command))
            { return ControlRejected("ControlCommandUnverified", "原受理能力未声明该控制命令"); }
            bool validValue = request.Command switch
            {
                DeviceCommand.SwitchMode => request.Value is (int)DeviceMode.Theme or (int)DeviceMode.SecondaryDisplay
                    && request.Value != (int)before.Mode && before.Power is PowerState.On or PowerState.Off,
                DeviceCommand.SetPower => request.Value is (int)PowerState.On or (int)PowerState.Off,
                DeviceCommand.SetBrightness => request.Value is >= 0 and <= 100,
                DeviceCommand.QueryState => request.Value == 0,
                _ => false
            };
            return validValue ? new OperationResult<Unit>.Success(new())
                : ControlRejected("InvalidControlValue", "控制目标或原电源意图无效，未批准请求");
        }
    }

    private static OperationResult<Unit> ControlRejected(string code, string reason) =>
        new OperationResult<Unit>.Failure(AppError.Create(code, reason, "核实原操作与能力，不发送替代命令"));

    public OperationResult<ModeTransitionLease> ArmModeTransition(ControlRequest request, IControlChannel originalChannel)
    {
        lock (syncRoot)
        {
            var valid = ValidateControlRequest(request);
            if (valid is OperationResult<Unit>.Failure rejected)
            { return new OperationResult<ModeTransitionLease>.Failure(rejected.Error); }
            if (request.Command != DeviceCommand.SwitchMode || originalChannel is null || running?.Work is null
                || Current.SessionGeneration == long.MaxValue)
            { return new OperationResult<ModeTransitionLease>.Failure(AppError.Create("ModeTransitionUnavailable",
                "模式交接缺少原通道、工作所有者或可用代次", "保留原操作，不发切换")); }
            if (modeTransition is { } existing)
            {
                return existing.Original == request.Context && existing.Target == (DeviceMode)request.Value
                    && ReferenceEquals(existing.Channel, originalChannel) && ReferenceEquals(existing.Work, running.Work)
                    ? new OperationResult<ModeTransitionLease>.Success(existing)
                    : new OperationResult<ModeTransitionLease>.Failure(AppError.Create("ModeTransitionOccupied",
                        "原操作已有不同的模式交接", "保留唯一交接，不重复切换"));
            }
            modeTransition = new(request.Context, (DeviceMode)request.Value, running.Before!.Power,
                originalChannel, running.Work, errorReporter, timeProvider, IsCurrent);
            return new OperationResult<ModeTransitionLease>.Success(modeTransition);
        }
    }

    public OperationResult<OperationContext> AdmitModeTarget(ModeTransitionLease lease, DeviceSelectionAdmission admission)
    {
        lock (syncRoot)
        {
            if (lease is null || !ReferenceEquals(modeTransition, lease) || closing || finished || invalidation is not null
                || running?.Action != DeviceAction.SwitchMode || !ReferenceEquals(running.Work, lease.Work)
                || lease.TargetContext is not { } target || !IsCurrent(target) || Current.Connection != ConnectionState.Recovering
                || !lease.OriginalControlClosed || transferredPipeline is not null || unclaimedConnection is not null
                || lease.Admission is not null || cancelOperation == running.Id || running.CancellationToken.IsCancellationRequested
                || operationCancellation?.IsCancellationRequested != false)
            { return new OperationResult<OperationContext>.Failure(AppError.Create("ModeTargetAdmissionUnavailable",
                "原资源未收敛、模式交接已变化或目标已受理", "保留原操作，不覆盖目标会话")); }
            if (!ValidateAdmission(admission, out var error))
            { return new OperationResult<OperationContext>.Failure(error!); }
            if (admission.Selection.Key != lease.Original.DeviceKey)
            { return new OperationResult<OperationContext>.Failure(AppError.Create("ModeTargetDeviceMismatch",
                "重枚举目标不属于原物理设备", "重新核实身份，不接管其他设备")); }
            lease.Admission = admission;
            Publish(Current with { SelectedInterfaces = admission.Selection.Interfaces, Identity = admission.Selection.Interfaces[0] });
            if (!ValidateAdmission(admission, out error))
            {
                Invalidate(error);
                return new OperationResult<OperationContext>.Failure(error!);
            }
            return new OperationResult<OperationContext>.Success(target);
        }
    }

    public OperationResult<OperationContext> ResolveFrameOperation(FrameContext frame)
    {
        lock (syncRoot)
        {
            if (frame.DeviceKey is not null && IsLiveSession(frame.DeviceKey, frame.SessionGeneration)
                && Current.Connection == ConnectionState.Ready && Current.Mode == DeviceMode.Theme
                && Current.Power == PowerState.On && Current.Streaming is not (StreamingState.Paused or StreamingState.Faulted)
                && Current.Capabilities is { IsProtocolMatched: true })
            {
                if (themeApplicationBase is { } candidate && IsCurrent(candidate.Context)
                    && cancelOperation != candidate.Context.OperationId && !running!.CancellationToken.IsCancellationRequested
                    && operationCancellation?.IsCancellationRequested != true
                    && candidate.PipelineRevision == frame.ContentRevision && candidate.Context.OperationId == frame.OperationId)
                {
                    return new OperationResult<OperationContext>.Success(candidate.Context);
                }
                if (CurrentActiveProfile is { } active && active.Context.DeviceKey == frame.DeviceKey
                    && active.Context.SessionGeneration == frame.SessionGeneration && active.PipelineRevision == frame.ContentRevision
                    && active.Context.OperationId == frame.OperationId
                    && (running is null || (IsCurrent(active.Context) && cancelOperation != active.Context.OperationId
                        && !running.CancellationToken.IsCancellationRequested && operationCancellation?.IsCancellationRequested != true)))
                {
                    return new OperationResult<OperationContext>.Success(active.Context);
                }
            }
            return new OperationResult<OperationContext>.Failure(AppError.Create("FrameOperationExpired",
                "帧不属于当前候选或活动内容，不能发送或恢复", "请核实所选设备与当前活动配置"));
        }
    }

    public OperationResult<ThemeApplicationBase> CaptureThemeApplicationBase(OperationContext context, DeviceProfile? restore,
        ThemeContent? theme, DeviceSettings? settings)
    {
        lock (syncRoot)
        {
            if (!IsCurrent(context) || running?.Action is not (DeviceAction.ApplyContent or DeviceAction.ApplySettings)
                || cancelOperation == context.OperationId || running.CancellationToken.IsCancellationRequested
                || operationCancellation?.IsCancellationRequested == true
                || Current.Connection != ConnectionState.Ready || Current.Mode != DeviceMode.Theme
                || Current.Power != PowerState.On || Current.Capabilities is not { IsProtocolMatched: true } capabilities
                || (restore is not null && restore.DeviceKey != context.DeviceKey)
                || (running.Action == DeviceAction.ApplyContent ? theme is null || settings is not null
                    : theme is not null || settings is null) || lastPipelineRevision == long.MaxValue)
            {
                return new OperationResult<ThemeApplicationBase>.Failure(AppError.Create("ThemeApplicationUnavailable",
                    "当前操作或设备状态不允许应用主题", "请连接并唤醒所选主题设备"));
            }
            var active = CurrentActiveProfile;
            var candidateContent = theme ?? active?.Content ?? restore?.Content ?? ThemeContent.Default;
            if (ThemeValidation.Validate(candidateContent) is OperationResult<Unit>.Failure invalid)
            {
                return new OperationResult<ThemeApplicationBase>.Failure(invalid.Error);
            }
            themeApplicationBase = new(context, candidateContent,
                settings ?? active?.Settings ?? restore?.Settings ?? DeviceSettings.Default,
                Current.ObservedBrightness ?? restore?.ConfirmedBrightness, Current.Power,
                capabilities.Width, capabilities.Height, capabilities.MaximumFrameBytes, ++lastPipelineRevision);
            return new OperationResult<ThemeApplicationBase>.Success(themeApplicationBase);
        }
    }

    public Task<OperationResult<ActiveProfile>> CommitActiveProfileAsync(ActiveProfile profile, FrameTransferReceipt receipt, FrameRateController pacing)
    {
        lock (syncRoot)
        {
            if (closing || finished || profileCommit is not null || !IsCurrent(profile.Context))
            {
                return Task.FromResult<OperationResult<ActiveProfile>>(new OperationResult<ActiveProfile>.Failure(
                    AppError.Create("ActiveCommitUnavailable", "活动配置提交已过期或不可受理", "请重新核实所选设备状态")));
            }
            var reply = new TaskCompletionSource<OperationResult<ActiveProfile>>(TaskCreationOptions.RunContinuationsAsynchronously);
            profileCommit = new(profile, receipt, pacing, reply);
            signal.Writer.TryWrite(0);
            return reply.Task;
        }
    }

    private void ProcessProfileCommit()
    {
        if (profileCommit is not { } commit) { return; }
        profileCommit = null;
        try
        {
            var context = commit.Profile.Context;
            var received = commit.Receipt.Context;
            if (!IsCurrent(context) || running?.Action is not (DeviceAction.ApplyContent or DeviceAction.ApplySettings)
                || cancelOperation == context.OperationId || running.CancellationToken.IsCancellationRequested
                || operationCancellation?.IsCancellationRequested == true
                || themeApplicationBase?.Context != context || themeApplicationBase.Settings != commit.Profile.Settings
                || themeApplicationBase.Content != commit.Profile.Content || themeApplicationBase.PipelineRevision != commit.Profile.PipelineRevision
                || Current.Connection != ConnectionState.Ready || Current.Mode != DeviceMode.Theme || Current.Power != PowerState.On
                || received.DeviceKey != context.DeviceKey || received.SessionGeneration != context.SessionGeneration
                || received.OperationId != context.OperationId || received.ContentRevision != commit.Profile.PipelineRevision
                || commit.Receipt.FrameNumber < 0 || commit.Receipt.JpegBytes <= 0
                || commit.Receipt.JpegBytes > Math.Min(FrameFragmentPlan.MaximumFrameBytes, Current.Capabilities?.MaximumFrameBytes ?? 0)
                || commit.Receipt.PacketCount <= 0 || commit.Receipt.PacketBytes < commit.Receipt.JpegBytes + 11L
                || ThemeValidation.Validate(commit.Profile.Content) is not OperationResult<Unit>.Success)
            {
                commit.Reply.TrySetResult(new OperationResult<ActiveProfile>.Failure(AppError.Create("StaleActiveCommit",
                    "首帧收据与当前操作不匹配，未提交活动配置", "请核实设备状态后重新应用")));
                return;
            }
            var rate = commit.Pacing.SetUserLimit(commit.Profile.Settings.FramesPerSecond);
            if (rate is OperationResult<Unit>.Failure pacingFailure)
            {
                commit.Reply.TrySetResult(new OperationResult<ActiveProfile>.Failure(pacingFailure.Error));
                return;
            }
            Volatile.Write(ref activeProfile, commit.Profile);
            themeApplicationBase = null;
            commit.Reply.TrySetResult(new OperationResult<ActiveProfile>.Success(commit.Profile));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "ActiveProfileCommit");
            commit.Reply.TrySetResult(new OperationResult<ActiveProfile>.Failure(new(report.ErrorId, "ActiveProfileCommitFailed",
                "活动配置提交失败", "请核实设备状态后恢复")));
        }
    }

    public Task<OperationResult<DeviceSnapshot>> SelectAsync(DeviceSelectionAdmission admission, CancellationToken cancellationToken) =>
        admission is null || admission.Selection is null || admission.Validity is null
            ? Task.FromResult(Reject("SelectionAdmissionMissing", "设备选择缺少发现有效性凭据", "请从当前完整发现结果重新确认设备"))
            : Enqueue(new Request(admission, null, null, 0, 0, null, cancellationToken));

    public Task<OperationResult<DeviceSnapshot>> ExecuteAsync(DeviceKey deviceKey, long sessionGeneration,
        DeviceAction action, long contentRevision, IDeviceSessionWork work, CancellationToken cancellationToken,
        int? requestedBrightness = null) =>
        Enqueue(new Request(null, deviceKey, action, sessionGeneration, contentRevision, work, cancellationToken, requestedBrightness));

    private Task<OperationResult<DeviceSnapshot>> Enqueue(Request request)
    {
        lock (syncRoot)
        {
            if (closing || finished) { return Task.FromResult(Reject("ApplicationStopping", "设备服务正在退出", "请等待退出完成")); }
            if (requests.Count == MaximumRequests) { return Task.FromResult(Reject("DeviceQueueFull", "设备操作请求过多", "请等待当前操作完成后重试")); }
            requests.Enqueue(request);
            signal.Writer.TryWrite(0);
            return request.Reply.Task;
        }
    }

    public void NotifyDisconnected(DeviceKey key, long generation, AppError? reason = null)
    {
        lock (syncRoot)
        {
            if (finished || Current.DeviceKey != key || Current.SessionGeneration != generation) { return; }
            if (invalidation is { Reason: not null } existing && reason is null
                && existing.Key == key && existing.Generation == generation) { return; }
            invalidation = new Invalidation(key, generation, reason);
            signal.Writer.TryWrite(0);
        }
    }

    public bool NotifyInterfaceRemoved(DeviceKey key, long generation, string? path, AppError reason)
    {
        lock (syncRoot)
        {
            var current = Current;
            if (finished || current.Connection == ConnectionState.Disconnected || current.DeviceKey != key
                || current.SessionGeneration != generation || invalidation is not null) { return false; }
            if (path is not null && !current.SelectedInterfaces.Any(identity =>
                StringComparer.OrdinalIgnoreCase.Equals(identity.InterfacePath, path))) { return false; }
            bool expectedModeRemoval = path is not null && modeTransition is { } transition
                && transition.Admission is null && transition.TargetContext is null
                && transition.Original.DeviceKey == key && transition.Original.SessionGeneration == generation
                && running?.Id == transition.Original.OperationId && running.Action == DeviceAction.SwitchMode;
            invalidation = new Invalidation(key, generation, reason, expectedModeRemoval);
            signal.Writer.TryWrite(0);
            return true;
        }
    }

    public OperationResult<DeviceSnapshotSubscription> Subscribe()
    {
        lock (syncRoot)
        {
            if (finished || closing || subscribers.Count == MaximumSubscribers)
            {
                return new OperationResult<DeviceSnapshotSubscription>.Failure(AppError.Create("SnapshotSubscriptionUnavailable",
                    "设备状态订阅不可用", "请关闭无用页面订阅或等待退出完成"));
            }
            var channel = Channel.CreateBounded<DeviceSnapshot>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false
            });
            Guid id = Guid.NewGuid();
            subscribers.Add(id, channel);
            channel.Writer.TryWrite(Current);
            return new OperationResult<DeviceSnapshotSubscription>.Success(new DeviceSnapshotSubscription(channel.Reader, () =>
            {
                lock (syncRoot)
                {
                    if (subscribers.Remove(id, out var removed)) { removed.Writer.TryComplete(); }
                }
            }));
        }
    }

    private async Task RunLoopAsync()
    {
        try
        {
            await foreach (byte wake in signal.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                lock (syncRoot)
                {
                    ApplyInvalidation();
                    ApplyUnclaimedClose();
                    ProcessProfileCommit();
                    if (closing && Current.Connection != ConnectionState.Disconnected)
                    {
                        Invalidate(null);
                    }
                    if (cancelOperation == running?.Id && running is not null) { CancelRunning(); }
                    cancelOperation = null;
                    if (completion is not null)
                    {
                        Completion delivered = completion;
                        completion = null;
                        if (!cancellationCallbacks.IsCompletedSuccessfully)
                        {
                            Task callbacks = cancellationCallbacks;
                            cancellationCallbacks = Task.CompletedTask;
                            workerTask = DeliverAfterCancellationAsync(delivered, callbacks);
                        }
                        else { Finish(delivered); }
                    }
                    while (requests.TryDequeue(out var request))
                    {
                        if (closing || request.CancellationToken.IsCancellationRequested)
                        {
                            request.Reply.TrySetResult(new OperationResult<DeviceSnapshot>.Cancelled());
                        }
                        else { Accept(request); }
                    }
                    if (closing && running is null && (unclaimedConnection is null || RequiresProcessRestart))
                    {
                        finished = true;
                        foreach (var subscriber in subscribers.Values) { subscriber.Writer.TryComplete(); }
                        subscribers.Clear();
                        signal.Writer.TryComplete();
                        break;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 不丢弃已受理工作：记录调度故障并等待其完成／收敛后停止。
            var report = errorReporter.Report(exception, "DeviceCoordinatorLoop");
            lock (syncRoot)
            {
                closing = true;
                profileCommit?.Reply.TrySetResult(new OperationResult<ActiveProfile>.Failure(AppError.Create(
                    "ActiveCommitCoordinatorStopped", "设备调度已停止，未提交活动配置", "请重新连接所选设备")));
                profileCommit = null;
                CancelRunning();
                foreach (var request in requests) { request.Reply.TrySetResult(Reject("DeviceCoordinatorFailed", "设备调度已停止", $"请重启应用。错误编号：{report.ErrorId}")); }
                requests.Clear();
            }
            try { await workerTask.ConfigureAwait(false); }
            catch (Exception workException) when (workException is not OutOfMemoryException) { errorReporter.Report(workException, "DeviceCoordinatorFailureDrain"); }
            bool cancellationFailed = false;
            try { await cancellationCallbacks.ConfigureAwait(false); }
            catch (Exception cancelException) when (cancelException is not OutOfMemoryException)
            {
                cancellationFailed = true;
                errorReporter.Report(cancelException, "DeviceCoordinatorFailureCancel");
            }
            lock (syncRoot)
            {
                if (running is not null && (cancellationFailed || completion?.Drained is not OperationResult<Unit>.Success))
                {
                    drainUnconfirmed = true;
                    Volatile.Write(ref unconfirmedOwner, running.Work);
                }
                running?.Reply.TrySetResult(Reject("DeviceCoordinatorFailed", "设备调度已停止", $"请重启应用。错误编号：{report.ErrorId}"));
                running = null;
                userCancellation.Unregister();
                operationCancellation?.Dispose();
                operationCancellation = null;
                Publish(Current with { SessionGeneration = Current.SessionGeneration + 1,
                    Connection = ConnectionState.Faulted, Streaming = StreamingState.Faulted, PendingOperationId = null,
                    Error = new AppError(report.ErrorId, "DeviceCoordinatorFailed", "设备调度已停止", "请重启应用") });
                foreach (var subscriber in subscribers.Values) { subscriber.Writer.TryComplete(); }
                subscribers.Clear();
                finished = true;
                signal.Writer.TryComplete();
            }
        }
    }

    private void ApplyInvalidation()
    {
        if (invalidation is not { } lost) { return; }
        invalidation = null;
        if (Current.DeviceKey == lost.Key && Current.SessionGeneration == lost.Generation)
        {
            if (lost.ExpectedModeRemoval && !closing && modeTransition is { } transition
                && running?.Id == transition.Original.OperationId && running.Action == DeviceAction.SwitchMode
                && !running.CancellationToken.IsCancellationRequested && operationCancellation?.IsCancellationRequested == false
                && cancelOperation != running.Id && Current.SessionGeneration < long.MaxValue)
            {
                long generation = Current.SessionGeneration + 1;
                transition.SetTargetGeneration(generation);
                Volatile.Write(ref activeProfile, null);
                themeApplicationBase = null;
                Publish(Current with { SessionGeneration = generation, Connection = ConnectionState.Recovering,
                    SelectedInterfaces = [], Identity = null, Capabilities = null, Mode = DeviceMode.Unknown,
                    Power = PowerState.Unknown, Streaming = StreamingState.Stopped, ObservedBrightness = null,
                    RequestedBrightness = null, Error = null });
                return;
            }
            Invalidate(lost.Reason);
        }
    }

    private void Invalidate(AppError? reason)
    {
        modeTransition?.EndWaiting(reason);
        modeTransition = null;
        Volatile.Write(ref activeProfile, null);
        themeApplicationBase = null;
        Publish(Current with
        {
            SessionGeneration = Current.SessionGeneration + 1, Connection = ConnectionState.Disconnected,
            SelectedInterfaces = [],
            Capabilities = null, Mode = DeviceMode.Unknown, Power = PowerState.Unknown,
            Streaming = StreamingState.Stopped, ObservedBrightness = null, RequestedBrightness = null, Error = reason
        });
        CancelRunning();
        StartUnclaimedClose();
    }

    private void CancelRunning()
    {
        modeTransition?.EndWaiting();
        if (operationCancellation is { IsCancellationRequested: false }) { cancellationCallbacks = operationCancellation.CancelAsync(); }
    }

    private void Accept(Request request)
    {
        if (running is not null || drainUnconfirmed || unclaimedConnection is not null)
        {
            request.Reply.TrySetResult(Reject("DeviceBusy", "设备操作尚未收敛", "请等待当前操作完成；若持续故障请重新插拔并重启应用"));
            return;
        }
        if (request.Selection is { } admission)
        {
            if (Current.Connection != ConnectionState.Disconnected || transferredPipeline is not null)
            {
                request.Reply.TrySetResult(Reject("PreviousSessionNotReleased", "原设备会话尚未释放", "请先断开并收敛原会话后重新选择"));
                return;
            }
            if (!ValidateAdmission(admission, out var admissionError))
            {
                request.Reply.TrySetResult(new OperationResult<DeviceSnapshot>.Failure(admissionError!));
                return;
            }
            var selection = admission.Selection;
            Publish(DeviceSnapshot.Disconnected with
            {
                SessionGeneration = Current.SessionGeneration + 1, DeviceKey = selection.Key,
                SelectedInterfaces = selection.Interfaces,
                Identity = selection.Interfaces[0], Connection = ConnectionState.Discovered
            });
            lastThemeRevision = -1;
            lastSettingsRevision = -1;
            if (!ValidateAdmission(admission, out admissionError))
            {
                Invalidate(admissionError);
                request.Reply.TrySetResult(new OperationResult<DeviceSnapshot>.Failure(admissionError!));
                return;
            }
            request.Reply.TrySetResult(new OperationResult<DeviceSnapshot>.Success(Current));
            return;
        }
        if (transferredPipeline is not null && (request.Action == DeviceAction.Connect
            || request.Work is ThemeConnectionSessionWork))
        {
            request.Reply.TrySetResult(Reject("PreviousPipelineNotReleased", "原设备管线尚未完成关闭与本地交接",
                "请先处理原会话，不创建替代连接"));
            return;
        }
        if (request.DeviceKey != Current.DeviceKey || request.Generation != Current.SessionGeneration || request.Revision < 0)
        {
            request.Reply.TrySetResult(Reject("StaleDeviceContext", "设备或会话已发生变化", "请读取当前状态后重新操作"));
            return;
        }
        var availability = OperationAvailability.Evaluate(Current, request.Action!.Value);
        if (availability is OperationResult<Unit>.Failure failure)
        {
            request.Reply.TrySetResult(new OperationResult<DeviceSnapshot>.Failure(failure.Error));
            return;
        }
        if (request.Action == DeviceAction.SaveDraft)
        {
            request.Reply.TrySetResult(Reject("SeparateDraftOperation", "草稿保存不属于设备操作", "请通过本地草稿存储保存"));
            return;
        }
        if (request.Action == DeviceAction.SetBrightness && request.RequestedBrightness is not (>= 0 and <= 100)
            || request.Action != DeviceAction.SetBrightness && request.RequestedBrightness is not null)
        {
            request.Reply.TrySetResult(Reject("InvalidBrightnessRequest", "亮度请求值与操作不匹配", "请输入 0～100 的亮度"));
            return;
        }
        if (request.Action == DeviceAction.ApplyContent && request.Revision < lastThemeRevision
            || request.Action == DeviceAction.ApplySettings && request.Revision < lastSettingsRevision)
        {
            request.Reply.TrySetResult(Reject("StaleContentRevision", "该编辑修订已被较新操作取代", "请使用当前编辑重新提交"));
            return;
        }
        if (request.Action == DeviceAction.ApplyContent) { lastThemeRevision = request.Revision; }
        if (request.Action == DeviceAction.ApplySettings) { lastSettingsRevision = request.Revision; }
        running = request;
        if (request.Action is DeviceAction.Connect or DeviceAction.Recover && request.Work is ThemeConnectionSessionWork connection)
        { unclaimedConnection = connection; }
        request.Before = Current;
        request.ActiveBefore = CurrentActiveProfile;
        operationCancellation = new CancellationTokenSource();
        cancellationCallbacks = Task.CompletedTask;
        var context = new OperationContext(request.DeviceKey!, request.Generation, request.Id, request.Revision);
        Publish(Current with
        {
            PendingOperationId = request.Id,
            RequestedBrightness = request.Action == DeviceAction.SetBrightness ? request.RequestedBrightness : Current.RequestedBrightness,
            Connection = request.Action switch
            {
                DeviceAction.Connect => ConnectionState.Opening,
                DeviceAction.Recover => ConnectionState.Recovering,
                _ => Current.Connection
            }, Error = null
        });
        userCancellation = request.CancellationToken.Register(() =>
        {
            lock (syncRoot)
            {
                if (running?.Id != request.Id) { return; }
                cancelOperation = request.Id;
                signal.Writer.TryWrite(0);
            }
        });
        if (request.CancellationToken.IsCancellationRequested) { CancelRunning(); }
        CancellationToken token = operationCancellation.Token;
        workerTask = Task.Run(() => ExecuteWorkAsync(request, context, token));
    }

    private bool ValidateAdmission(DeviceSelectionAdmission admission, out AppError? error)
    {
        try
        {
            if (admission.Validity.IsCurrent(admission.Selection)) { error = null; return true; }
            error = AppError.Create("DiscoveryAdmissionStale", "设备选择对应的发现列表或通知代次已经变化",
                "请完成当前重扫并重新确认设备，不沿用旧选择");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "DeviceSelectionAdmission");
            error = new(report.ErrorId, "DiscoveryAdmissionFailed", "设备选择有效性核验未完成", "请检查诊断后重新检测设备");
        }
        return false;
    }

    private async Task ExecuteWorkAsync(Request request, OperationContext context, CancellationToken token)
    {
        long started = 0;
        bool executionInvoked = false;
        OperationResult<DeviceObservation> result;
        OperationResult<Unit> drained;
        try
        {
            started = timeProvider.GetTimestamp();
            request.Started = started;
            RecordOperationStart(request, context);
            executionInvoked = true;
            result = await request.Work!.ExecuteAsync(context, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (token.IsCancellationRequested)
        {
            if (request.Action is DeviceAction.Connect or DeviceAction.Recover)
            {
                result = new OperationResult<DeviceObservation>.Cancelled();
            }
            else
            {
                var report = errorReporter.Report(exception, "DeviceOperationCancellation", Association(context, started));
                result = new OperationResult<DeviceObservation>.Unknown(new AppError(report.ErrorId, "CancelledDeviceOutcomeUnconfirmed",
                    "已请求取消，设备执行结果尚未确认", "请核实设备实际状态后重新操作"));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "DeviceOperation", Association(context, started));
            result = new OperationResult<DeviceObservation>.Failure(new AppError(report.ErrorId, "DeviceOperationFailed",
                "设备操作未完成", "请检查连接与诊断后重试"));
        }
        if (!executionInvoked && request.Work is ThemeConnectionSessionWork skipped) { skipped.ConfirmExecutionSkipped(); }
        if (result is null)
        {
            result = new OperationResult<DeviceObservation>.Failure(AppError.Create("MissingDeviceCompletion",
                "设备操作未返回有效结果", "请检查设备诊断后重试"));
        }
        try { drained = await request.Work!.DrainAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "DeviceOperationDrain", Association(context, started));
            drained = new OperationResult<Unit>.Unknown(new AppError(report.ErrorId, "DeviceDrainUnconfirmed",
                "设备操作资源尚未确认收敛", "请停止使用该会话并检查诊断"));
        }
        DeviceRejectionProof? rejection = null;
        try { rejection = request.Work!.RejectionBeforeIo; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            errorReporter.Report(exception, "DeviceRejectionProof", Association(context, started));
        }
        Deliver(new Completion(request, context, result, drained, rejection));
    }

    private DiagnosticContext Association(OperationContext context, long started)
    {
        try
        {
            return new DiagnosticContext(DiagnosticContext.Anonymize(context.DeviceKey), context.SessionGeneration,
                context.OperationId, ElapsedMilliseconds: timeProvider.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new DiagnosticContext(SessionGeneration: context.SessionGeneration, OperationId: context.OperationId);
        }
    }

    private async Task DeliverAfterCancellationAsync(Completion delivered, Task callbacks)
    {
        try { await callbacks.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "DeviceCancellationCallbacks");
            delivered = delivered with { Drained = new OperationResult<Unit>.Unknown(new AppError(report.ErrorId,
                "CancellationDrainUnconfirmed", "设备取消回调未正常结束", "请停止使用该会话并检查诊断")) };
        }
        Deliver(delivered);
    }

    private void Deliver(Completion delivered)
    {
        lock (syncRoot)
        {
            if (finished) { delivered.Request.Reply.TrySetResult(new OperationResult<DeviceSnapshot>.Cancelled()); return; }
            completion = delivered;
            signal.Writer.TryWrite(0);
        }
    }

    private void Finish(Completion delivered)
    {
        if (running?.Id != delivered.Request.Id) { return; }
        var transition = modeTransition;
        bool transitioned = transition is not null && transition.Original == delivered.Context
            && ReferenceEquals(transition.Work, delivered.Request.Work) && transition.TargetContext is { } targetContext
            && IsCurrent(targetContext);
        bool current = Current.DeviceKey == delivered.Context.DeviceKey
            && (Current.SessionGeneration == delivered.Context.SessionGeneration || transitioned);
        bool succeeded = false;
        if (delivered.Drained is not OperationResult<Unit>.Success)
        {
            drainUnconfirmed = true;
            // 保留工作所有者，不把未知原生收敛当作释放缓冲的授权。
            Volatile.Write(ref unconfirmedOwner, delivered.Request.Work);
            AppError error = delivered.Drained switch
            {
                OperationResult<Unit>.Failure failure => failure.Error,
                OperationResult<Unit>.Unknown unknown => unknown.Error,
                _ => AppError.Create("DeviceDrainUnconfirmed", "设备操作资源尚未确认收敛", "请停止使用该会话并检查诊断")
            };
            if (current) { Publish(Current with { Connection = ConnectionState.Faulted, Streaming = StreamingState.Faulted, Error = error }); }
            delivered.Request.Reply.TrySetResult(new OperationResult<DeviceSnapshot>.Unknown(error));
        }
        else if (!current || closing)
        {
            delivered.Request.Reply.TrySetResult(new OperationResult<DeviceSnapshot>.Cancelled());
        }
        else if (CanPreserveRejectedApplication(delivered))
        {
            // 业务拒绝不伪造观察成功；原活动引用保留，独立错误仍交回调用方。
            if (delivered.Result is OperationResult<DeviceObservation>.Failure rejected)
            {
                Publish(Current with { Error = rejected.Error });
                delivered.Request.Reply.TrySetResult(new OperationResult<DeviceSnapshot>.Failure(rejected.Error));
            }
            else { delivered.Request.Reply.TrySetResult(new OperationResult<DeviceSnapshot>.Cancelled()); }
        }
        else if (delivered.Result is OperationResult<DeviceObservation>.Success success)
        {
            var valid = ValidateObservation(success.Value, delivered.Context.DeviceKey);
            if (transition is not null && (transition.Admission is not { } admitted || !transitioned
                || !transition.OriginalControlClosed || success.Value.Mode != transition.Target
                || success.Value.Power != transition.PowerIntent || !admitted.Selection.Interfaces.Contains(success.Value.Identity)
                || !ValidateAdmission(admitted, out _)))
            { valid = ControlRejected("ModeTargetCompletionUnverified", "模式目标的原资源、发现凭据或实际状态未通过交付核验"); }
            if (valid is OperationResult<Unit>.Failure failure) { Fault(delivered.Request, failure.Error, transition is not null); }
            else
            {
                var observed = success.Value;
                Publish(Current with
                {
                    Connection = ConnectionState.Ready, Identity = observed.Identity, Capabilities = observed.Capabilities, Mode = observed.Mode,
                    Power = observed.Power, Streaming = observed.Streaming, ObservedBrightness = observed.ObservedBrightness, Error = null
                });
                succeeded = true;
            }
        }
        else
        {
            AppError? error = delivered.Result switch
            {
                OperationResult<DeviceObservation>.Failure failure => failure.Error,
                OperationResult<DeviceObservation>.Unknown unknown => unknown.Error,
                _ => null
            };
            if (error is not null) { Fault(delivered.Request, error, delivered.Result is OperationResult<DeviceObservation>.Unknown); }
            else
            {
                if (delivered.Request.Action is DeviceAction.Connect or DeviceAction.Recover)
                {
                    Publish(Current with { Connection = ConnectionState.Discovered, Streaming = StreamingState.Stopped,
                        Capabilities = null, Mode = DeviceMode.Unknown, Power = PowerState.Unknown,
                        RequestedBrightness = null, ObservedBrightness = null });
                }
                delivered.Request.Reply.TrySetResult(new OperationResult<DeviceSnapshot>.Cancelled());
            }
        }
        themeApplicationBase = null;
        modeTransition?.EndWaiting(Current.Error);
        modeTransition = null;
        running = null;
        userCancellation.Unregister();
        operationCancellation?.Dispose();
        operationCancellation = null;
        Publish(Current with { PendingOperationId = null });
        if (!succeeded && ReferenceEquals(unclaimedConnection, delivered.Request.Work)) { StartUnclaimedClose(); }
        if (succeeded) { delivered.Request.Reply.TrySetResult(new OperationResult<DeviceSnapshot>.Success(Current)); }
        RecordOperationCompletion(delivered);
    }

    private void Fault(Request request, AppError error, bool unknown = false)
    {
        Publish(Current with { Connection = ConnectionState.Faulted, Streaming = StreamingState.Faulted, Error = error });
        request.Reply.TrySetResult(unknown ? new OperationResult<DeviceSnapshot>.Unknown(error) : new OperationResult<DeviceSnapshot>.Failure(error));
    }

    private bool CanPreserveRejectedApplication(Completion delivered)
    {
        var before = delivered.Request.Before;
        return delivered.Request.Action is DeviceAction.ApplyContent or DeviceAction.ApplySettings
            && delivered.Result is OperationResult<DeviceObservation>.Failure or OperationResult<DeviceObservation>.Cancelled
            && delivered.Rejection?.Context == delivered.Context && before is not null
            && before.Connection == ConnectionState.Ready && before.Mode == DeviceMode.Theme && before.Power == PowerState.On
            && before.Streaming is not (StreamingState.Faulted or StreamingState.Paused)
            && Current.Connection == before.Connection && Current.Mode == before.Mode && Current.Power == before.Power
            && Current.Streaming == before.Streaming && Current.Capabilities == before.Capabilities
            && Current.Identity == before.Identity && Current.ObservedBrightness == before.ObservedBrightness
            && Current.RequestedBrightness == before.RequestedBrightness && CurrentActiveProfile == delivered.Request.ActiveBefore;
    }

    private static OperationResult<Unit> ValidateObservation(DeviceObservation observation, DeviceKey expectedKey)
    {
        if (observation is null || observation.DeviceKey != expectedKey || observation.Identity is null
            || string.IsNullOrWhiteSpace(observation.Identity.InterfacePath)
            || DeviceKey.FromSerialNumber(observation.Identity.VendorId, observation.Identity.SerialNumber)
                is not OperationResult<DeviceKey>.Success identityKey || identityKey.Value != expectedKey
            || observation.Capabilities is not { IsProtocolMatched: true } || !observation.DriverVerified
            || !Enum.IsDefined(observation.Mode) || observation.Mode == DeviceMode.Unknown || !Enum.IsDefined(observation.Power)
            || !Enum.IsDefined(observation.Streaming) || observation.ObservedBrightness is < 0 or > 100
            || observation.Mode == DeviceMode.Theme && (observation.Capabilities.Width != 1920 || observation.Capabilities.Height != 1080)
            || observation.Mode == DeviceMode.SecondaryDisplay && (observation.Streaming != StreamingState.Stopped
                || !observation.SecondaryDisplayPathVerified && observation.Capabilities.Evidence != CapabilityEvidence.SourceMatched)
            || observation.Streaming == StreamingState.Running && observation.Power != PowerState.On)
        {
            return new OperationResult<Unit>.Failure(AppError.Create("InvalidDeviceObservation",
                "设备身份、能力、驱动或实际状态尚未通过核验", "请核对兼容记录和设备状态，不能继续推流"));
        }
        return new OperationResult<Unit>.Success(new Unit());
    }

    private void Publish(DeviceSnapshot value)
    {
        var previous = Current;
        value = value with { Version = Current.Version + 1 };
        Volatile.Write(ref snapshot, value);
        foreach (var subscriber in subscribers.Values) { subscriber.Writer.TryWrite(value); }
        RecordStateChange(previous, value);
    }

    private static OperationResult<DeviceSnapshot> Reject(string code, string reason, string action) =>
        new OperationResult<DeviceSnapshot>.Failure(AppError.Create(code, reason, action));

    public async ValueTask DisposeAsync()
    {
        lock (syncRoot)
        {
            if (transferredPipeline is not null)
            {
                throw new InvalidOperationException("接管管线尚未完成关闭与本地交接，不能关闭设备协调器");
            }
            closing = true; StartUnclaimedClose(); signal.Writer.TryWrite(0);
        }
        await loop.ConfigureAwait(false);
        Task<OperationResult<Unit>>? pendingClose;
        lock (syncRoot) { pendingClose = unclaimedClose; }
        if (pendingClose is not null) { await pendingClose.ConfigureAwait(false); lock (syncRoot) { ApplyUnclaimedClose(); } }
        if (RequiresProcessRestart)
        {
            throw new InvalidOperationException("设备 I/O 收敛尚未确认，不能将此清理报告为完成");
        }
    }

    private void StartUnclaimedClose()
    {
        if (unclaimedConnection is { } owner && unclaimedClose is null)
        { unclaimedClose = CloseUnclaimedCoreAsync(owner); }
    }
    private async Task<OperationResult<Unit>> CloseUnclaimedCoreAsync(ThemeConnectionSessionWork owner)
    {
        OperationResult<Unit> result;
        try { result = await owner.CloseUnattachedAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "UnclaimedConnectionClose");
            result = new OperationResult<Unit>.Unknown(new(report.ErrorId, "UnclaimedConnectionCloseUnconfirmed",
                "未接管连接的关闭未确认，所有者已保留", "请检查诊断，不创建替代会话"));
        }
        lock (syncRoot) { unclaimedCloseResult = result; signal.Writer.TryWrite(0); }
        return result;
    }
    private void ApplyUnclaimedClose()
    {
        if (unclaimedCloseResult is not { } result) { return; }
        unclaimedCloseResult = null;
        if (result is OperationResult<Unit>.Success) { unclaimedConnection = null; unclaimedClose = null; return; }
        unconfirmedOwner = unclaimedConnection;
        drainUnconfirmed = true;
        if (!finished)
        {
            var error = result switch
            {
                OperationResult<Unit>.Unknown unknown => unknown.Error,
                OperationResult<Unit>.Failure failure => failure.Error,
                _ => AppError.Create("ConnectionCloseUnconfirmed", "连接资源关闭尚未确认", "请保留原连接所有者")
            };
            Publish(Current with { Error = error });
        }
    }

    private sealed record Invalidation(DeviceKey Key, long Generation, AppError? Reason, bool ExpectedModeRemoval = false);
    private sealed record Completion(Request Request, OperationContext Context,
        OperationResult<DeviceObservation> Result, OperationResult<Unit> Drained, DeviceRejectionProof? Rejection);
    private sealed class Request(DeviceSelectionAdmission? selection, DeviceKey? deviceKey, DeviceAction? action,
        long generation, long revision, IDeviceSessionWork? work, CancellationToken cancellationToken, int? requestedBrightness = null)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public DeviceSelectionAdmission? Selection { get; } = selection;
        public DeviceKey? DeviceKey { get; } = deviceKey;
        public DeviceAction? Action { get; } = action;
        public int? RequestedBrightness { get; } = requestedBrightness;
        public long Generation { get; } = generation;
        public long Revision { get; } = revision;
        public IDeviceSessionWork? Work { get; } = work;
        public DeviceSnapshot? Before { get; set; }
        public ActiveProfile? ActiveBefore { get; set; }
        public long Started { get; set; }
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<OperationResult<DeviceSnapshot>> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
