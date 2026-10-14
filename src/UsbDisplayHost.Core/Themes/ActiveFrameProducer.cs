using System.Collections.Immutable;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Core.Telemetry;
using UsbDisplayHost.Core.Transport;

namespace UsbDisplayHost.Core.Themes;

public sealed record ActiveFrameProducerStatus(bool IsStopping, long RenderedFrames, int PendingReceipts,
    AppError? Error, long? ErrorRevision, bool RequiresReceiptRecovery);

// 每个物理会话一个产帧循环；静态主题由应用首帧更新，动态主题每秒读取最新活动配置。
public sealed class ActiveFrameProducer : IAsyncDisposable
{
    private readonly DeviceCoordinator coordinator;
    private readonly IFrameRenderer renderer;
    private readonly FrameBoundaryScheduler scheduler;
    private readonly Func<ImmutableArray<MetricSnapshot>> getMetrics;
    private readonly TimeProvider clock;
    private readonly IErrorReporter reporter;
    private readonly DeviceKey deviceKey;
    private readonly long generation;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object syncRoot = new();
    private readonly Task loop;
    private sealed record PendingReceipt(FrameContext Context, long FrameNumber, int JpegBytes,
        Task<OperationResult<FrameTransferReceipt>> Completion);
    private readonly List<PendingReceipt> receipts = new(3);
    private long rendered;
    private long? failedRevision;
    private AppError? error;
    private long? errorRevision;
    private bool receiptRecoveryRequired;
    private bool stopping;
    private bool disposed;
    private int pendingReceipts;
    private Task? stopTask;
    internal DeviceCoordinator Coordinator => coordinator;
    internal FrameBoundaryScheduler Scheduler => scheduler;

    public ActiveFrameProducer(DeviceCoordinator coordinator, IFrameRenderer renderer, FrameBoundaryScheduler scheduler,
        Func<ImmutableArray<MetricSnapshot>> getMetrics, TimeProvider clock, IErrorReporter reporter)
    {
        this.coordinator = coordinator;
        this.renderer = renderer;
        this.scheduler = scheduler;
        this.getMetrics = getMetrics;
        this.clock = clock;
        this.reporter = reporter;
        deviceKey = scheduler.Executor.DeviceKey;
        generation = scheduler.Executor.SessionGeneration;
        if (!ReferenceEquals(scheduler.Pacing.Clock, clock)) { throw new ArgumentException("Active rendering and transmission require the same clock.", nameof(clock)); }
        loop = Task.Run(RunAsync);
    }

    public ActiveFrameProducerStatus Current
    {
        get { lock (syncRoot) { return new(stopping, rendered, pendingReceipts, error, errorRevision, receiptRecoveryRequired); } }
    }

    private async Task RunAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                await CollectReceiptsAsync(false).ConfigureAwait(false);
                var active = coordinator.CurrentActiveProfile;
                if (active is null || active.Context.DeviceKey != deviceKey || active.Context.SessionGeneration != generation
                    || active.Content is ThemeContent.Image) { continue; }
                if (!scheduler.CanAcceptFrames) { continue; }
                lock (syncRoot) { if (receiptRecoveryRequired || failedRevision == active.PipelineRevision) { continue; } }
                var frameContext = new FrameContext(active.Context.DeviceKey, active.Context.SessionGeneration,
                    active.Context.OperationId, active.PipelineRevision);
                if (coordinator.ResolveFrameOperation(frameContext) is not OperationResult<OperationContext>.Success) { continue; }
                var state = coordinator.Current;
                if (state.DeviceKey != deviceKey || state.SessionGeneration != generation
                    || state.Capabilities is not { IsProtocolMatched: true } capabilities) { continue; }
                var numbered = scheduler.AllocateFrameNumber();
                if (numbered is OperationResult<long>.Failure numberingFailure) { Fail(active.PipelineRevision, numberingFailure.Error); continue; }
                var metrics = getMetrics();
                if (!metrics.IsDefault && metrics.Length > 64)
                {
                    Fail(active.PipelineRevision, AppError.Create("ActiveMetricsLimit", "活动指标快照超过受限数量", "请检查指标来源后重新应用"));
                    continue;
                }
                var request = new RenderRequest(active.Content, active.Settings, capabilities.Width, capabilities.Height,
                    capabilities.MaximumFrameBytes, frameContext, ((OperationResult<long>.Success)numbered).Value,
                    FramePurpose.Active, clock.GetLocalNow(), metrics.IsDefault ? [] : metrics);
                var result = await renderer.RenderAsync(request, lifetime.Token).ConfigureAwait(false);
                if (result is not OperationResult<EncodedFrame>.Success frame)
                {
                    if (result is OperationResult<EncodedFrame>.Failure renderFailure) { Fail(active.PipelineRevision, renderFailure.Error); }
                    else if (result is OperationResult<EncodedFrame>.Unknown renderUnknown) { Fail(active.PipelineRevision, renderUnknown.Error); }
                    continue;
                }
                if (lifetime.IsCancellationRequested || coordinator.CurrentActiveProfile != active
                    || coordinator.ResolveFrameOperation(frameContext) is not OperationResult<OperationContext>.Success) { continue; }
                if (frame.Value.Context != frameContext || frame.Value.FrameNumber != request.FrameNumber
                    || frame.Value.Purpose != FramePurpose.Active || frame.Value.Width != request.Width || frame.Value.Height != request.Height)
                {
                    Fail(active.PipelineRevision, AppError.Create("ActiveRenderMismatch", "活动渲染收据与冻结请求不一致", "请检查渲染诊断后重新应用"));
                    continue;
                }
                receipts.Add(new(frameContext, frame.Value.FrameNumber, frame.Value.Jpeg.Length,
                    scheduler.SubmitFrameAsync(frame.Value, false, lifetime.Token)));
                lock (syncRoot) { rendered++; }
                // 替换待发帧会立即完成旧回执；只保留在途和最新待发的两个句柄。
                await CollectReceiptsAsync(false).ConfigureAwait(false);
                if (receipts.Count > 2)
                {
                    Fail(active.PipelineRevision, AppError.Create("ActiveReceiptLimit", "发送回执未符合单在途／单待发约束", "请停止会话并检查诊断"));
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "ActiveFrameProducer");
            Fail(-1, new(report.ErrorId,
                "ActiveFrameProducerStopped", "活动产帧已停止", "请检查诊断后重新建立会话"));
        }
        finally
        {
            await CollectReceiptsAsync(true).ConfigureAwait(false);
            lock (syncRoot) { stopping = true; }
        }
    }

    private async Task CollectReceiptsAsync(bool all)
    {
        for (int index = 0; index < receipts.Count;)
        {
            var receipt = receipts[index];
            if (!all && !receipt.Completion.IsCompleted) { index++; continue; }
            try
            {
                var result = await receipt.Completion.ConfigureAwait(false);
                if (result is OperationResult<FrameTransferReceipt>.Unknown unknown)
                {
                    FailReceipt(receipt.Context, unknown.Error);
                }
                else if (result is OperationResult<FrameTransferReceipt>.Failure failure)
                {
                    if (IsCurrentActive(receipt.Context))
                    {
                        lock (syncRoot)
                        {
                            if (!receiptRecoveryRequired) { error = failure.Error; errorRevision = receipt.Context.ContentRevision; }
                        }
                    }
                }
                else if (result is OperationResult<FrameTransferReceipt>.Success sent)
                {
                    if (sent.Value.Context != receipt.Context || sent.Value.FrameNumber != receipt.FrameNumber
                        || sent.Value.JpegBytes != receipt.JpegBytes || sent.Value.PacketCount <= 0
                        || sent.Value.PacketBytes < receipt.JpegBytes + 11L)
                    {
                        FailReceipt(receipt.Context, AppError.Create("ActiveReceiptMismatch",
                            "发送收据与原活动帧不一致", "请停止会话并核实发送结果"));
                    }
                    else if (IsCurrentActive(receipt.Context))
                    {
                        lock (syncRoot)
                        {
                            if (!receiptRecoveryRequired && failedRevision != receipt.Context.ContentRevision)
                            {
                                error = null; errorRevision = null;
                            }
                        }
                    }
                }
                else if (result is not OperationResult<FrameTransferReceipt>.Cancelled)
                {
                    FailReceipt(receipt.Context, AppError.Create("MissingActiveReceipt",
                        "活动帧未返回有效发送结果", "请停止会话并核实发送结果"));
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = reporter.Report(exception, "ActiveFrameReceipt");
                FailReceipt(receipt.Context, new(report.ErrorId,
                    "ActiveFrameReceiptUnknown", "活动帧发送结果未知", "请停止会话并检查诊断"));
            }
            receipts.RemoveAt(index);
        }
        lock (syncRoot) { pendingReceipts = receipts.Count; }
    }

    private void Fail(long revision, AppError failure)
    {
        lock (syncRoot)
        {
            failedRevision = revision;
            if (!receiptRecoveryRequired) { error = failure; errorRevision = revision; }
        }
    }

    private bool IsCurrentActive(FrameContext context) => coordinator.CurrentActiveProfile is { } active
        && active.Context.DeviceKey == deviceKey && active.Context.SessionGeneration == generation
        && active.Context.OperationId == context.OperationId && active.PipelineRevision == context.ContentRevision;

    private void FailReceipt(FrameContext context, AppError failure)
    {
        lock (syncRoot)
        {
            receiptRecoveryRequired = true;
            error = failure;
            errorRevision = context.ContentRevision;
        }
    }

    public OperationResult<Unit> ResumeAfterRecovery()
    {
        lock (syncRoot)
        {
            if (stopping || !scheduler.CanAcceptFrames || coordinator.CurrentActiveProfile is not { } active
                || active.Context.DeviceKey != deviceKey || active.Context.SessionGeneration != generation
                || coordinator.ResolveFrameOperation(new(deviceKey, generation, active.Context.OperationId, active.PipelineRevision))
                    is not OperationResult<OperationContext>.Success)
            {
                return new OperationResult<Unit>.Failure(AppError.Create("ActiveProducerRecoveryUnavailable",
                    "活动产帧或发送通道尚未确认可以恢复", "请先完成实际会话恢复与活动配置确认"));
            }
            failedRevision = null;
            error = null;
            errorRevision = null;
            receiptRecoveryRequired = false;
            return new OperationResult<Unit>.Success(new Unit());
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (syncRoot)
        {
            stopping = true;
            return new ValueTask(stopTask ??= Task.Run(StopAsync));
        }
    }

    private async Task StopAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await loop.ConfigureAwait(false);
        lock (syncRoot)
        {
            if (!disposed) { lifetime.Dispose(); disposed = true; }
        }
    }
}
