using System.Collections.Immutable;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Telemetry;
using UsbDisplayHost.Core.Themes;
using UsbDisplayHost.Core.Transport;

namespace UsbDisplayHost.Core.Sessions;

public sealed record ThemeSessionApplicationCompletion(OperationResult<DeviceSnapshot> Session,
    ThemeApplicationAttempt? Attempt);

// 同一物理会话的组合所有者；共享协调器／渲染器／存储由应用生命周期持有。
public sealed class ThemeSessionPipeline
{
    private readonly object syncRoot = new();
    private readonly DeviceKey key;
    private readonly long generation;
    private readonly DeviceCoordinator coordinator;
    private readonly ThemeApplicationService application;
    private readonly ActiveFrameProducer producer;
    private readonly FrameBoundaryScheduler scheduler;
    private readonly FrameTransferExecutor executor;
    private readonly IDisplayTransport transport;
    private readonly IApplicationSessionState session;
    private readonly IErrorReporter reporter;
    private Task? operation;
    private Task<OperationResult<Unit>>? closeTask;
    private Task<OperationResult<Unit>>? finalizeTask;
    private bool closing;

    public ThemeSessionPipeline(DeviceKey key, long generation, DeviceCoordinator coordinator,
        ThemeApplicationService application, ActiveFrameProducer producer, FrameBoundaryScheduler scheduler,
        FrameTransferExecutor executor, IDisplayTransport transport, IApplicationSessionState session, IErrorReporter reporter)
    {
        if (generation <= 0 || executor.DeviceKey != key || executor.SessionGeneration != generation || application.DeviceKey != key
            || !ReferenceEquals(application.Coordinator, coordinator) || !ReferenceEquals(application.Scheduler, scheduler)
            || !ReferenceEquals(producer.Coordinator, coordinator) || !ReferenceEquals(producer.Scheduler, scheduler)
            || !ReferenceEquals(scheduler.Executor, executor) || !ReferenceEquals(executor.Transport, transport))
        {
            throw new ArgumentException("Theme pipeline components must belong to the same physical session.", nameof(executor));
        }
        this.key = key; this.generation = generation; this.coordinator = coordinator;
        this.application = application; this.producer = producer; this.scheduler = scheduler;
        this.executor = executor; this.transport = transport; this.session = session; this.reporter = reporter;
    }

    public DeviceCoordinator Coordinator => coordinator;
    public DeviceKey DeviceKey => key;
    public long SessionGeneration => generation;
    public FrameTransferStatus TransferStatus => executor.Current;
    public ActiveFrameProducerStatus ProducerStatus => producer.Current;
    public FrameRateSnapshot FrameRate => scheduler.FrameRate;
    public CompletedHostFrame? CaptureActiveHostFrame()
    {
        var active = coordinator.CurrentActiveProfile;
        if (active is null || active.Context.DeviceKey != key || active.Context.SessionGeneration != generation) { return null; }
        var completed = scheduler.LatestCompletedHostFrame;
        if (completed is null || completed.Frame.Context.DeviceKey != key || completed.Frame.Context.SessionGeneration != generation
            || completed.Frame.Context.OperationId != active.Context.OperationId
            || completed.Frame.Context.ContentRevision != active.PipelineRevision
            || completed.Frame.Purpose != FramePurpose.Active || coordinator.CurrentActiveProfile != active)
        { return null; }
        return completed;
    }
    internal IDisplayTransport Transport => transport;
    internal IApplicationSessionState Observation => session;
    internal IFrameParserRecovery? Parser => executor.Recovery;
    internal DeviceCapabilities Capabilities => executor.Capabilities;
    public bool IsNativeClosed
    {
        get { lock (syncRoot) { return closeTask is { IsCompletedSuccessfully: true, Result: OperationResult<Unit>.Success }; } }
    }
    public OperationResult<Unit>? CloseOutcome
    {
        get { lock (syncRoot) { return finalizeTask is { IsCompletedSuccessfully: true } completed ? completed.Result : null; } }
    }

    public bool CanReleaseOwnership => CloseOutcome is OperationResult<Unit>.Success;
    public PendingThemeSave? PendingSave => application.PendingSave;
    public bool IsBusy { get { lock (syncRoot) { return operation is { IsCompleted: false }; } } }

    public Task<OperationResult<DeviceSnapshot>> ExecuteProtocolAsync(DeviceAction action,
        Func<OperationContext, CancellationToken, Task<OperationResult<DeviceObservation>>> execute,
        bool resumeFrames, bool recoverFrames, CancellationToken token, int? brightness = null)
    {
        lock (syncRoot)
        {
            if (closing || operation is { IsCompleted: false })
            { return Task.FromResult<OperationResult<DeviceSnapshot>>(new OperationResult<DeviceSnapshot>.Failure(
                AppError.Create("ProtocolSessionBusy", "会话正在执行或关闭", "等待当前操作结束"))); }
            var work = new ProtocolControlWork(execute, session.DrainAsync, scheduler, resumeFrames, recoverFrames ? executor : null);
            var task = coordinator.ExecuteAsync(key, generation, action, coordinator.CurrentActiveProfile?.PipelineRevision ?? 0,
                work, token, brightness);
            operation = task;
            return task;
        }
    }

    public Task<ThemeSessionApplicationCompletion> ApplyThemeAsync(ThemeDraft frozenDraft,
        ImmutableArray<MetricSnapshot> metrics, CancellationToken cancellationToken) =>
        StartApplication(ThemeApplicationSessionWork.ForTheme(application, frozenDraft, metrics, session,
            coordinator.IsCurrent, reporter), frozenDraft.Revision, cancellationToken);

    public Task<ThemeSessionApplicationCompletion> ApplySettingsAsync(DeviceSettingsDraft frozenDraft,
        ImmutableArray<MetricSnapshot> metrics, CancellationToken cancellationToken) =>
        StartApplication(ThemeApplicationSessionWork.ForSettings(application, frozenDraft, metrics, session,
            coordinator.IsCurrent, reporter), frozenDraft.Revision, cancellationToken);

    private Task<ThemeSessionApplicationCompletion> StartApplication(ThemeApplicationSessionWork work, long revision,
        CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            if (closing || operation is { IsCompleted: false })
            {
                return Task.FromResult(new ThemeSessionApplicationCompletion(new OperationResult<DeviceSnapshot>.Failure(
                    AppError.Create("ThemeSessionBusy", "该会话正在操作或关闭，未受理应用", "请等待当前操作收敛")), null));
            }
            var task = Task.Run(async () =>
            {
                try
                {
                    var result = await coordinator.ExecuteAsync(key, generation, work.Action, revision, work, cancellationToken).ConfigureAwait(false);
                    return new ThemeSessionApplicationCompletion(result, work.Attempt);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    var report = reporter.Report(exception, "ThemeSessionApplication");
                    return new ThemeSessionApplicationCompletion(new OperationResult<DeviceSnapshot>.Unknown(new(report.ErrorId,
                        "ThemeSessionApplicationUnknown", "会话应用结果未确认", "请保留发送与保存结果并检查诊断")), work.Attempt);
                }
            });
            operation = task;
            return task;
        }
    }

    public Task<OperationResult<PendingThemeSave>> RetrySaveAsync(Guid retryId, CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            if ((closing && closeTask is not { IsCompletedSuccessfully: true, Result: OperationResult<Unit>.Success })
                || operation is { IsCompleted: false })
            {
                return Task.FromResult<OperationResult<PendingThemeSave>>(new OperationResult<PendingThemeSave>.Failure(
                    AppError.Create("ThemeSessionBusy", "该会话正在操作或关闭，未受理保存重试", "请等待当前操作收敛")));
            }
            var task = application.RetrySaveAsync(retryId, cancellationToken);
            operation = task;
            return task;
        }
    }

    public Task<OperationResult<DeviceSnapshot>> SetStreamingAsync(bool resume, CancellationToken token)
    {
        lock (syncRoot)
        {
            if (closing || operation is { IsCompleted: false })
            {
                return Task.FromResult<OperationResult<DeviceSnapshot>>(new OperationResult<DeviceSnapshot>.Failure(
                    AppError.Create("StreamingSessionBusy", "会话正在操作或关闭，未受理推流变更", "请等待当前操作结束")));
            }
            var active = coordinator.CurrentActiveProfile;
            if (resume && producer.Current.RequiresReceiptRecovery)
            {
                return Task.FromResult<OperationResult<DeviceSnapshot>>(new OperationResult<DeviceSnapshot>.Failure(
                    AppError.Create("StreamingReceiptRecoveryRequired", "活动发送收据尚未确认，不能恢复推流", "请完成实际会话恢复，不通过暂停解除未知状态")));
            }
            var revision = active?.Context.ContentRevision ?? 0;
            var work = new StreamingSessionWork(coordinator, scheduler, session, resume, reporter);
            var task = Task.Run(async () =>
            {
                try
                {
                    var result = await coordinator.ExecuteAsync(key, generation,
                        resume ? DeviceAction.Resume : DeviceAction.Pause, revision, work, token).ConfigureAwait(false);
                    if (resume && active is not null && result is OperationResult<DeviceSnapshot>.Success)
                    {
                        var resumed = producer.ResumeAfterRecovery();
                        if (resumed is not OperationResult<Unit>.Success)
                        {
                            var error = resumed is OperationResult<Unit>.Failure failure ? failure.Error
                                : AppError.Create("StreamingProducerUnconfirmed", "活动产帧恢复未确认", "请核实当前会话");
                            coordinator.NotifyDisconnected(key, generation, error);
                            return new OperationResult<DeviceSnapshot>.Unknown(error);
                        }
                    }
                    return result;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    var report = reporter.Report(exception, "StreamingSessionCompletion");
                    return new OperationResult<DeviceSnapshot>.Unknown(new(report.ErrorId, "StreamingCompletionUnconfirmed",
                        "已受理推流操作的结果未确认", "请保留原会话并检查诊断，不重复操作"));
                }
            });
            operation = task;
            return task;
        }
    }

    public Task<OperationResult<Unit>> CloseAsync(Guid? discardSaveId = null)
    {
        lock (syncRoot)
        {
            closing = true;
            closeTask ??= Task.Run(CloseCoreAsync);
            if (finalizeTask is null || finalizeTask is { IsCompletedSuccessfully: true, Result: OperationResult<Unit>.Failure })
            {
                finalizeTask = FinalizeCloseAsync(closeTask, discardSaveId);
            }
            return finalizeTask;
        }
    }

    private async Task<OperationResult<Unit>> FinalizeCloseAsync(Task<OperationResult<Unit>> nativeClose, Guid? discardSaveId)
    {
        var result = await nativeClose.ConfigureAwait(false);
        if (result is not OperationResult<Unit>.Success) { return result; }
        var released = application.ReleaseReferencesAfterClose(discardSaveId);
        if (released is not OperationResult<Unit>.Success) { return released; }
        return coordinator.ReleaseTransferredPipeline(this);
    }

    private async Task<OperationResult<Unit>> CloseCoreAsync()
    {
        try
        {
            // 先使旧代次帧和操作失效；断连快照不是已释放原生资源的证明。
            coordinator.NotifyDisconnected(key, generation);
            await producer.DisposeAsync().ConfigureAwait(false);
            Task? pending;
            lock (syncRoot) { pending = operation; }
            if (pending is not null) { await pending.ConfigureAwait(false); }
            await scheduler.DisposeAsync().ConfigureAwait(false);
            var observedDrain = await session.DrainAsync().ConfigureAwait(false);
            if (observedDrain is not OperationResult<Unit>.Success) { return Unconfirmed(observedDrain); }
            var frameDrain = await executor.DrainAsync().ConfigureAwait(false);
            if (frameDrain is not OperationResult<Unit>.Success) { return Unconfirmed(frameDrain); }
            var nativeDrain = await transport.CancelAndDrainAsync(CancellationToken.None).ConfigureAwait(false);
            if (nativeDrain is not OperationResult<Unit>.Success) { return Unconfirmed(nativeDrain); }
            await transport.DisposeAsync().ConfigureAwait(false);
            var controlClose = await session.CloseAsync().ConfigureAwait(false);
            if (controlClose is not OperationResult<Unit>.Success) { return Unconfirmed(controlClose); }
            return new OperationResult<Unit>.Success(new Unit());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "ThemeSessionClose");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "ThemeSessionCloseUnconfirmed",
                "会话关闭与资源释放尚未确认", "请保留会话所有者并检查诊断，不要打开替代会话"));
        }
    }

    private static OperationResult<Unit> Unconfirmed(OperationResult<Unit> result) =>
        new OperationResult<Unit>.Unknown(result switch
        {
            OperationResult<Unit>.Failure failure => failure.Error,
            OperationResult<Unit>.Unknown unknown => unknown.Error,
            _ => AppError.Create("ThemeSessionDrainUnconfirmed", "会话资源尚未确认收敛", "请保留会话所有者并检查诊断")
        });
}
