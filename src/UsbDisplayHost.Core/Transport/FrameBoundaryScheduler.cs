using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Core.Transport;

public interface IFrameBoundaryWork
{
    // 构造／排队不执行 I/O；只有调度器授予帧边界后才能开始。
    Task<OperationResult<Unit>> ExecuteAsync(OperationContext context, CancellationToken cancellationToken);
    Task<OperationResult<Unit>> DrainAsync();
    // 仅在成功收据及观察状态已发布后允许继续推流；切模式／熄屏／暂停保持 false。
    bool AllowsFrameStreaming => false;
}

public sealed record FrameSchedulerStatus(bool IsStopping, bool HasInFlightFrame, bool HasPendingFrame,
    bool HasProtectedPendingFrame, bool FramesPaused, int PendingControls, long ReplacedFrames, long RejectedFrames,
    bool IoUnconfirmed, AppError? Error);

// 一个在途工作、一个待发帧、最多八个帧边界控制；原生传输只经本调度器组合。
public sealed class FrameBoundaryScheduler : IAsyncDisposable
{
    private sealed record FrameWork(EncodedFrame Frame, bool Protected, CancellationToken Cancellation,
        TaskCompletionSource<OperationResult<FrameTransferReceipt>> Completion);
    private sealed record ControlWork(OperationContext Context, IFrameBoundaryWork Work, CancellationToken Cancellation,
        TaskCompletionSource<OperationResult<Unit>> Completion);
    private readonly object syncRoot = new();
    private readonly SemaphoreSlim available = new(0, 1);
    private readonly Queue<ControlWork> controls = new();
    private readonly FrameTransferExecutor executor;
    private readonly Func<OperationContext, bool> isCurrentControl;
    private readonly IErrorReporter errorReporter;
    private readonly FrameRateController pacing;
    private readonly Task worker;
    private FrameWork? pending;
    private IFrameBoundaryWork? unconfirmedControlOwner;
    private bool stopping;
    private bool inFlightFrame;
    private bool ioUnconfirmed;
    private bool framesPaused;
    private CompletedHostFrame? completedHostFrame;
    public CompletedHostFrame? LatestCompletedHostFrame => Volatile.Read(ref completedHostFrame);
    private bool resourcesDisposed;
    private AppError? error;
    private long replacedFrames;
    private long rejectedFrames;
    private long highestRevision = -1;
    private long highestFrameNumber = -1;
    private long allocatedFrameNumber = -1;

    public FrameBoundaryScheduler(FrameTransferExecutor executor, Func<OperationContext, bool> isCurrentControl,
        IErrorReporter errorReporter, FrameRateController pacing)
    {
        this.executor = executor;
        this.isCurrentControl = isCurrentControl;
        this.errorReporter = errorReporter;
        this.pacing = pacing;
        if (!ReferenceEquals(executor.Clock, pacing.Clock)) { throw new ArgumentException("Frame transfer and pacing require the same TimeProvider.", nameof(pacing)); }
        worker = Task.Run(RunAsync);
    }
    public FrameRateSnapshot FrameRate => pacing.Current;
    internal FrameRateController Pacing => pacing;
    internal FrameTransferExecutor Executor => executor;

    public bool CanAcceptFrames
    {
        get
        {
            lock (syncRoot)
            {
                return !stopping && !ioUnconfirmed && !framesPaused
                    && executor.Current.State == FrameTransferState.Ready && !pacing.Current.RequiresRecovery;
            }
        }
    }

    public OperationResult<long> AllocateFrameNumber()
    {
        lock (syncRoot)
        {
            if (stopping || ioUnconfirmed || allocatedFrameNumber == long.MaxValue)
            {
                return Failed<long>("FrameNumberUnavailable", "会话停止或帧号已达到限额");
            }
            return new OperationResult<long>.Success(++allocatedFrameNumber);
        }
    }

    public Task<OperationResult<Unit>> PrepareCandidateAsync(EncodedFrame frame, CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            if (stopping || ioUnconfirmed || framesPaused)
            {
                return Task.FromResult(Failed<Unit>("CandidateChannelUnavailable", "帧通道停止、暂停或 I/O 未收敛，未准备候选"));
            }
        }
        return executor.ValidateCandidateAsync(frame, cancellationToken);
    }

    public FrameSchedulerStatus Current
    {
        get
        {
            lock (syncRoot)
            {
                return new(stopping, inFlightFrame, pending is not null, pending?.Protected == true,
                    framesPaused, controls.Count, replacedFrames, rejectedFrames, ioUnconfirmed, error);
            }
        }
    }

    public Task<OperationResult<FrameTransferReceipt>> SubmitFrameAsync(EncodedFrame frame,
        bool protectedFirstFrame, CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult<OperationResult<FrameTransferReceipt>>(new OperationResult<FrameTransferReceipt>.Cancelled());
            }
            if (stopping || ioUnconfirmed) { return RejectFrame("FrameSchedulerStopped", "帧调度已停止或 I/O 未收敛"); }
            if (framesPaused) { return RejectFrame("FramesPausedAtBoundary", "控制操作尚未确认允许继续推流"); }
            if (frame.Purpose != FramePurpose.Active || frame.Context.DeviceKey != executor.DeviceKey
                || frame.Context.SessionGeneration != executor.SessionGeneration || executor.SessionGeneration <= 0
                || frame.Context.OperationId == Guid.Empty || frame.Context.ContentRevision < 0 || frame.FrameNumber < 0
                || frame.Width != 1920 || frame.Height != 1080 || frame.Jpeg.IsDefaultOrEmpty
                || frame.Jpeg.Length > FrameFragmentPlan.MaximumFrameBytes)
            {
                return RejectFrame("InvalidQueuedFrame", "帧内容或会话字段无效");
            }
            if (executor.Current.State != FrameTransferState.Ready)
            {
                return RejectFrame("FrameChannelNotReady", "帧通道尚未确认恢复，未排队该帧");
            }
            if (frame.Context.ContentRevision < highestRevision
                || (frame.Context.ContentRevision == highestRevision && frame.FrameNumber <= highestFrameNumber))
            {
                return RejectFrame("OutdatedQueuedFrame", "帧修订或帧号迟到，未替换当前内容");
            }
            if (pending?.Protected == true)
            {
                return RejectFrame("ProtectedFramePending", "主题应用首帧等待发送，不能替换");
            }
            var completion = new TaskCompletionSource<OperationResult<FrameTransferReceipt>>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (pending is not null)
            {
                replacedFrames++;
                pending.Completion.TrySetResult(new OperationResult<FrameTransferReceipt>.Cancelled());
            }
            pending = new(frame, protectedFirstFrame, cancellationToken, completion);
            highestRevision = frame.Context.ContentRevision;
            highestFrameNumber = frame.FrameNumber;
            Signal();
            return completion.Task;
        }
    }

    public Task<OperationResult<Unit>> ScheduleControlAsync(OperationContext context, IFrameBoundaryWork work,
        CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            if (cancellationToken.IsCancellationRequested) { return Task.FromResult<OperationResult<Unit>>(new OperationResult<Unit>.Cancelled()); }
            if (stopping || ioUnconfirmed || controls.Count >= 8)
            {
                return Task.FromResult(Failed<Unit>("ControlQueueUnavailable", "帧边界控制未受理，通道停止或队列已满"));
            }
            if (context.DeviceKey != executor.DeviceKey || context.SessionGeneration != executor.SessionGeneration
                || context.OperationId == Guid.Empty || context.ContentRevision < 0)
            {
                return Task.FromResult(Failed<Unit>("ControlSessionMismatch", "控制操作与当前物理会话不匹配"));
            }
            var completion = new TaskCompletionSource<OperationResult<Unit>>(TaskCreationOptions.RunContinuationsAsynchronously);
            controls.Enqueue(new(context, work, cancellationToken, completion));
            Signal();
            return completion.Task;
        }
    }

    public Task<OperationResult<Unit>> RecoverAsync(OperationContext context, CancellationToken cancellationToken) =>
        ScheduleControlAsync(context, new RecoveryWork(executor), cancellationToken);

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await available.WaitAsync().ConfigureAwait(false);
                while (true)
                {
                    FrameWork? frame;
                    ControlWork? control;
                    lock (syncRoot)
                    {
                        if (stopping || ioUnconfirmed) { return; }
                        control = controls.TryDequeue(out var next) ? next : null;
                        frame = control is null && !framesPaused ? pending : null;
                    }
                    if (control is not null) { await RunControlAsync(control).ConfigureAwait(false); }
                    else if (frame is not null)
                    {
                        var delayResult = pacing.GetNextDelay();
                        if (delayResult is OperationResult<TimeSpan>.Failure pacingFailure)
                        {
                            PauseFrames(pacingFailure.Error);
                            continue;
                        }
                        var delay = ((OperationResult<TimeSpan>.Success)delayResult).Value;
                        if (delay > TimeSpan.Zero)
                        {
                            try { await WaitForRateOrWorkAsync(delay).ConfigureAwait(false); }
                            catch (Exception exception) when (exception is not OutOfMemoryException)
                            {
                                var report = errorReporter.Report(exception, "FramePacingWait");
                                var fault = new AppError(report.ErrorId, "FramePacingWaitFailed", "发送节奏等待失败，已暂停新帧", "请核实会话时钟后恢复");
                                pacing.StopAfterFailure(fault);
                                PauseFrames(fault);
                            }
                            continue;
                        }
                        lock (syncRoot)
                        {
                            if (stopping || ioUnconfirmed) { return; }
                            if (controls.Count > 0 || framesPaused || !ReferenceEquals(pending, frame)) { continue; }
                            pending = null;
                            inFlightFrame = true;
                        }
                        try { await RunFrameAsync(frame).ConfigureAwait(false); }
                        finally { lock (syncRoot) { inFlightFrame = false; } }
                    }
                    else { break; }
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "FrameScheduler");
            StopOnUnknown(new(report.ErrorId, "FrameSchedulerFailed", "设备调度异常，已停止新操作",
                "请退出应用后重新连接所选设备"));
        }
        finally
        {
            lock (syncRoot)
            {
                stopping = true;
                CompleteQueued();
            }
        }
    }

    private async Task RunFrameAsync(FrameWork frame)
    {
        var timing = pacing.BeginFrame();
        if (timing is OperationResult<long>.Failure clockFailure)
        {
            PauseFrames(clockFailure.Error);
            frame.Completion.TrySetResult(new OperationResult<FrameTransferReceipt>.Failure(clockFailure.Error));
            return;
        }
        long started = ((OperationResult<long>.Success)timing).Value;
        OperationResult<FrameTransferReceipt> result;
        try { result = await executor.SendAsync(frame.Frame, frame.Cancellation).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "ScheduledFrame");
            result = new OperationResult<FrameTransferReceipt>.Unknown(new(report.ErrorId, "ScheduledFrameFailed",
                "设备帧结果未知", "请停止发送并重新连接所选设备"));
        }
        var drained = await DrainFrameAsync().ConfigureAwait(false);
        if (drained is not OperationResult<Unit>.Success)
        {
            var fault = ResultError(drained);
            StopOnUnknown(fault);
            frame.Completion.TrySetResult(new OperationResult<FrameTransferReceipt>.Unknown(fault));
            return;
        }
        if (result is OperationResult<FrameTransferReceipt>.Success sent)
        {
            if (sent.Value.Context == frame.Frame.Context && sent.Value.FrameNumber == frame.Frame.FrameNumber
                && sent.Value.JpegBytes == frame.Frame.Jpeg.Length && sent.Value.PacketCount > 0
                && sent.Value.PacketBytes >= sent.Value.JpegBytes + 11L)
            {
                // 单槽引用实际完整发送画面；不复制 JPEG，不将其单独当作活动提交或设备呈现。
                Volatile.Write(ref completedHostFrame, new(frame.Frame, sent.Value));
            }
            if (pacing.ObserveSuccess(started, sent.Value) is OperationResult<Unit>.Failure clockError)
            {
                // USB 收据仍然成功，计时失败只停止后续帧，不篡改已发生的发送。
                PauseFrames(clockError.Error);
            }
        }
        else
        {
            pacing.CompleteUnsuccessfulAttempt();
            if (result is OperationResult<FrameTransferReceipt>.Unknown unknownResult) { pacing.StopAfterFailure(unknownResult.Error); }
            else if (result is OperationResult<FrameTransferReceipt>.Failure failedResult
                && executor.Current.State != FrameTransferState.Ready) { pacing.StopAfterFailure(failedResult.Error); }
        }
        lock (syncRoot)
        {
            if (result is OperationResult<FrameTransferReceipt>.Unknown) { framesPaused = true; }
            if ((framesPaused || executor.Current.State != FrameTransferState.Ready) && pending is not null)
            {
                pending.Completion.TrySetResult(Failed<FrameTransferReceipt>("FrameRecoveryRequired", "帧通道需要恢复，已清除待发帧"));
                pending = null;
            }
            if (result is OperationResult<FrameTransferReceipt>.Failure failure) { error = failure.Error; }
            else if (result is OperationResult<FrameTransferReceipt>.Unknown unknown) { error = unknown.Error; }
            else if (result is OperationResult<FrameTransferReceipt>.Success && !framesPaused) { error = null; }
        }
        frame.Completion.TrySetResult(result);
    }

    private async Task RunControlAsync(ControlWork control)
    {
        OperationResult<Unit> result;
        bool attempted = false;
        try
        {
            if (control.Cancellation.IsCancellationRequested) { result = new OperationResult<Unit>.Cancelled(); }
            else if (!isCurrentControl(control.Context)) { result = Failed<Unit>("StaleBoundaryControl", "控制操作已过期，未执行"); }
            else
            {
                lock (syncRoot) { framesPaused = true; }
                attempted = true;
                result = await control.Work.ExecuteAsync(control.Context, control.Cancellation).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "FrameBoundaryControl");
            result = attempted
                ? new OperationResult<Unit>.Unknown(new(report.ErrorId, "BoundaryControlUnknown", "控制结果未知", "请核实设备状态后重试"))
                : new OperationResult<Unit>.Failure(new(report.ErrorId, "BoundaryControlRejected", "控制条件无法确认", "请重新连接所选设备"));
        }
        if (attempted)
        {
            OperationResult<Unit> drained;
            try { drained = await control.Work.DrainAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = errorReporter.Report(exception, "BoundaryControlDrain");
                drained = new OperationResult<Unit>.Unknown(new(report.ErrorId, "BoundaryControlDrainFailed",
                    "控制 I/O 未收敛", "请退出应用后重新连接所选设备"));
            }
            if (drained is not OperationResult<Unit>.Success)
            {
                unconfirmedControlOwner = control.Work;
                var fault = ResultError(drained);
                StopOnUnknown(fault);
                result = new OperationResult<Unit>.Unknown(fault);
            }
            bool mayResume = false;
            if (result is OperationResult<Unit>.Success)
            {
                try { mayResume = control.Work.AllowsFrameStreaming && isCurrentControl(control.Context); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    var report = errorReporter.Report(exception, "BoundaryControlResumeGuard");
                    result = new OperationResult<Unit>.Unknown(new(report.ErrorId, "BoundaryResumeUnconfirmed",
                        "控制完成后的推流条件无法确认", "请核实设备状态后恢复"));
                }
            }
            if (mayResume) { pacing.ResumeAfterRecovery(); }
            lock (syncRoot)
            {
                framesPaused = !mayResume;
                if (framesPaused && pending is not null)
                {
                    pending.Completion.TrySetResult(Failed<FrameTransferReceipt>("FramesPausedByControl",
                        "控制未确认继续推流，已清除未发送的帧"));
                    pending = null;
                }
                if (result is OperationResult<Unit>.Failure failure) { error = failure.Error; }
                else if (result is OperationResult<Unit>.Unknown unknown) { error = unknown.Error; }
                else if (result is OperationResult<Unit>.Success) { error = null; }
            }
        }
        // 已执行控制的成功收据保留其冻结上下文；会话协调器仍须拒绝迟到的旧代次完成。
        control.Completion.TrySetResult(result);
    }

    private async Task<OperationResult<Unit>> DrainFrameAsync()
    {
        try { return await executor.DrainAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "ScheduledFrameDrain");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "ScheduledFrameDrainFailed",
                "设备帧 I/O 未收敛", "请退出应用后重新连接所选设备"));
        }
    }

    private async Task WaitForRateOrWorkAsync(TimeSpan delay)
    {
        using var cancellation = new CancellationTokenSource();
        Task timer = Task.Delay(delay, pacing.Clock, cancellation.Token);
        Task incoming = available.WaitAsync(cancellation.Token);
        await Task.WhenAny(timer, incoming).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(timer, incoming).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private void PauseFrames(AppError failure)
    {
        lock (syncRoot)
        {
            framesPaused = true;
            error = failure;
            pending?.Completion.TrySetResult(new OperationResult<FrameTransferReceipt>.Failure(failure));
            pending = null;
        }
    }

    private void StopOnUnknown(AppError fault)
    {
        lock (syncRoot)
        {
            ioUnconfirmed = true;
            error = fault;
            CompleteQueued();
        }
    }

    private void CompleteQueued()
    {
        if (pending is not null)
        {
            pending.Completion.TrySetResult(ioUnconfirmed
                ? Failed<FrameTransferReceipt>("FrameChannelStopped", "前一操作未收敛，该帧未发送")
                : new OperationResult<FrameTransferReceipt>.Cancelled());
            pending = null;
        }
        while (controls.TryDequeue(out var control))
        {
            // 此控制未开始 I/O，其结果不会因其他工作的未知结果而被伪装为已执行。
            control.Completion.TrySetResult(stopping ? new OperationResult<Unit>.Cancelled()
                : Failed<Unit>("FrameChannelStopped", "前一操作未收敛，控制未执行"));
        }
    }

    private Task<OperationResult<FrameTransferReceipt>> RejectFrame(string code, string reason)
    {
        rejectedFrames++;
        return Task.FromResult(Failed<FrameTransferReceipt>(code, reason));
    }

    private void Signal()
    {
        if (!resourcesDisposed && available.CurrentCount == 0) { available.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (syncRoot)
        {
            stopping = true;
            CompleteQueued();
            Signal();
        }
        // 不为正常退出截断在途帧，等待发送和取消回调的真实收敛。
        await worker.ConfigureAwait(false);
        lock (syncRoot)
        {
            if (!resourcesDisposed) { available.Dispose(); resourcesDisposed = true; }
        }
        if (ioUnconfirmed)
        {
            GC.KeepAlive(unconfirmedControlOwner);
            throw new InvalidOperationException("Frame scheduler I/O remains unconfirmed; retain its session owner.");
        }
    }

    private static AppError ResultError(OperationResult<Unit> result) => result switch
    {
        OperationResult<Unit>.Failure failure => failure.Error,
        OperationResult<Unit>.Unknown unknown => unknown.Error,
        _ => AppError.Create("FrameIoDrainUnconfirmed", "设备 I/O 收敛未确认", "请退出应用后重新连接所选设备")
    };

    private static OperationResult<T> Failed<T>(string code, string reason) =>
        new OperationResult<T>.Failure(AppError.Create(code, reason, "请等待当前操作结束或重新连接所选设备"));

    private sealed class RecoveryWork(FrameTransferExecutor executor) : IFrameBoundaryWork
    {
        public bool AllowsFrameStreaming => true;
        public Task<OperationResult<Unit>> ExecuteAsync(OperationContext context, CancellationToken cancellationToken) =>
            executor.RecoverAsync(context, cancellationToken);
        public Task<OperationResult<Unit>> DrainAsync() => executor.DrainAsync();
    }
}
