using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Sessions;

// 原操作关联保持不变；此对象持有并关闭实际原通道，不凭 ACK 声明收敛。
public sealed class ModeTransitionLease
{
    private readonly object sync = new();
    private readonly IErrorReporter reporter;
    private readonly TimeProvider clock;
    private readonly Func<OperationContext, bool> isCurrent;
    private readonly TaskCompletionSource<OperationResult<OperationContext>> targetArrived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<OperationResult<Unit>>? closing;
    private int originalClosed;
    private long targetGeneration;
    internal ModeTransitionLease(OperationContext original, DeviceMode target, PowerState power,
        IControlChannel channel, IDeviceSessionWork work, IErrorReporter reporter, TimeProvider clock,
        Func<OperationContext, bool> isCurrent)
    {
        Original = original; Target = target; PowerIntent = power; Channel = channel; Work = work;
        this.reporter = reporter; this.clock = clock; this.isCurrent = isCurrent;
    }
    public OperationContext Original { get; }
    public DeviceMode Target { get; }
    public PowerState PowerIntent { get; }
    public bool OriginalControlClosed => Volatile.Read(ref originalClosed) != 0;
    public OperationContext? TargetContext => Volatile.Read(ref targetGeneration) > Original.SessionGeneration
        ? Original with { SessionGeneration = Volatile.Read(ref targetGeneration) } : null;
    internal IControlChannel Channel { get; }
    internal IDeviceSessionWork Work { get; }
    internal DeviceSelectionAdmission? Admission { get; set; }
    internal void SetTargetGeneration(long generation)
    {
        Volatile.Write(ref targetGeneration, generation);
        targetArrived.TrySetResult(new OperationResult<OperationContext>.Success(Original with { SessionGeneration = generation }));
    }
    internal void EndWaiting(AppError? error = null) => targetArrived.TrySetResult(
        new OperationResult<OperationContext>.Unknown(error ?? AppError.Create("ModeTransitionEnded",
            "原模式操作已结束或失效，目标等待停止", "保留原资源所有者，不沿用旧上下文")));

    // 剩余预算来自原 ledger 的实际 BeginWrite 时刻；不为每个阶段重新给八秒。
    public async Task<OperationResult<OperationContext>> WaitForTargetContextAsync(TimeSpan remainingBudget,
        CancellationToken cancellationToken)
    {
        try
        {
            if (remainingBudget < TimeSpan.Zero || remainingBudget > Ad102V2ModeControlLedger.NormalWaitBudget)
            { return new OperationResult<OperationContext>.Failure(AppError.Create("InvalidModeWaitBudget",
                "目标等待预算无效", "读取原写入的剩余预算，不重新计时")); }
            if (cancellationToken.IsCancellationRequested)
            { return WaitingUnknown("ModeTargetWaitCancelled", "目标观察已取消，实际切换结果待确认"); }
            if (remainingBudget == TimeSpan.Zero)
            { return WaitingUnknown("ModeTargetWaitExpired", "原正常等待期限已到，需核实实际目标"); }
            var result = await targetArrived.Task.WaitAsync(remainingBudget, clock, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            { return WaitingUnknown("ModeTargetWaitCancelled", "目标交付前观察已取消，实际切换结果待确认"); }
            return result is OperationResult<OperationContext>.Success success && !isCurrent(success.Value)
                ? WaitingUnknown("ModeTargetContextExpired", "目标上下文交付前已失效") : result;
        }
        catch (TimeoutException) { return WaitingUnknown("ModeTargetWaitExpired", "目标重枚举观察超过原剩余预算"); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return WaitingUnknown("ModeTargetWaitCancelled", "目标观察已取消，实际切换结果待确认"); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "ModeTargetContextWait");
            return new OperationResult<OperationContext>.Unknown(new(report.ErrorId, "ModeTargetWaitUnconfirmed",
                "目标上下文观察未完成", "保留原请求与资源，不自动重发或关闭未知资源"));
        }
    }
    private static OperationResult<OperationContext> WaitingUnknown(string code, string reason) =>
        new OperationResult<OperationContext>.Unknown(AppError.Create(code, reason,
            "核实同一设备的实际状态；观察结束不证明原生请求已收敛"));
    public Task<OperationResult<Unit>> CloseOriginalControlAsync()
    { lock (sync) { return closing ??= CloseOwnedAsync(); } }
    private async Task<OperationResult<Unit>> CloseOwnedAsync()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            var result = await Channel.CloseAsync().ConfigureAwait(false);
            if (result is OperationResult<Unit>.Success) { Volatile.Write(ref originalClosed, 1); }
            return result ?? new OperationResult<Unit>.Unknown(AppError.Create("ModeOriginalCloseMissing",
                "原控制通道未返回关闭结果", "保留原所有者，不接管目标接口"));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "ModeOriginalControlClose");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "ModeOriginalCloseUnconfirmed",
                "原控制通道关闭未确认", "保留原所有者，不打开目标控制通道"));
        }
    }
}
