using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Transport;

public sealed record FrameRateSnapshot(int UserFramesPerSecond, int EffectiveFramesPerSecond,
    bool AdaptivePolicyConfigured, bool RequiresRecovery, int ConsecutiveSlowFrames, TimeSpan? LastFrameDuration, AppError? Error);

// 仅消费完整帧成功收据；异常不作为拥塞样本，稳定时间使用同一 TimeProvider 的单调时钟。
public sealed class FrameRateController
{
    private readonly object syncRoot = new();
    private readonly TimeProvider timeProvider;
    private readonly IErrorReporter reporter;
    private readonly FramePacingPolicy? policy;
    private int userLimit, effectiveRate, slowCount;
    private long? lastStarted, lastCompleted, healthySince;
    private TimeSpan? lastDuration;
    private AppError? error;
    private bool measurementPending;

    public FrameRateController(DeviceSettings settings, FramePacingPolicy? policy, TimeProvider timeProvider, IErrorReporter reporter)
    {
        userLimit = effectiveRate = settings.FramesPerSecond;
        this.policy = policy; this.timeProvider = timeProvider; this.reporter = reporter;
    }

    public FrameRateSnapshot Current
    {
        get { lock (syncRoot) { return new(userLimit, effectiveRate, policy is not null, error is not null, slowCount, lastDuration, error); } }
    }
    internal TimeProvider Clock => timeProvider;

    public OperationResult<Unit> SetUserLimit(int framesPerSecond)
    {
        lock (syncRoot)
        {
            if (framesPerSecond is < 1 or > 5) { return Failed<Unit>("InvalidFrameRate", "用户帧率必须为 1～5fps"); }
            userLimit = framesPerSecond;
            effectiveRate = policy is null ? userLimit : Math.Min(effectiveRate, userLimit);
            slowCount = 0; healthySince = null;
            return new OperationResult<Unit>.Success(new Unit());
        }
    }

    public OperationResult<TimeSpan> GetNextDelay()
    {
        lock (syncRoot)
        {
            if (error is not null) { return new OperationResult<TimeSpan>.Failure(error); }
            if (lastStarted is not { } previous) { return new OperationResult<TimeSpan>.Success(TimeSpan.Zero); }
            try
            {
                var elapsed = timeProvider.GetElapsedTime(previous);
                if (elapsed < TimeSpan.Zero) { return ClockFailure<TimeSpan>(); }
                var period = TimeSpan.FromTicks((TimeSpan.TicksPerSecond + effectiveRate - 1) / effectiveRate);
                return new OperationResult<TimeSpan>.Success(elapsed >= period ? TimeSpan.Zero : period - elapsed);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { return ReportFailure<TimeSpan>(exception); }
        }
    }

    public OperationResult<long> BeginFrame()
    {
        lock (syncRoot)
        {
            if (error is not null) { return new OperationResult<long>.Failure(error); }
            if (measurementPending) { return Failed<long>("PacingMeasurementBusy", "前一帧计时尚未结束"); }
            try
            {
                long timestamp = timeProvider.GetTimestamp();
                lastStarted = timestamp;
                measurementPending = true;
                return new OperationResult<long>.Success(timestamp);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { return ReportFailure<long>(exception); }
        }
    }

    public OperationResult<Unit> ObserveSuccess(long startedTimestamp, FrameTransferReceipt receipt)
    {
        lock (syncRoot)
        {
            if (error is not null) { return new OperationResult<Unit>.Failure(error); }
            if (!measurementPending || lastStarted != startedTimestamp || receipt.JpegBytes is <= 0 or > 1048576
                || receipt.PacketCount <= 0 || receipt.PacketBytes < receipt.JpegBytes + 11L || receipt.FrameNumber < 0
                || receipt.Context.DeviceKey is null || receipt.Context.SessionGeneration <= 0
                || receipt.Context.OperationId == Guid.Empty || receipt.Context.ContentRevision < 0)
            {
                error = AppError.Create("PacingSampleMismatch", "完整帧样本与当前计时不匹配", "请核实同一会话的计时与发送收据");
                return new OperationResult<Unit>.Failure(error);
            }
            measurementPending = false;
            try
            {
                var duration = timeProvider.GetElapsedTime(startedTimestamp, receipt.CompletedTimestamp);
                var gap = lastCompleted is { } prior ? timeProvider.GetElapsedTime(prior, receipt.CompletedTimestamp) : TimeSpan.Zero;
                if (duration < TimeSpan.Zero || gap < TimeSpan.Zero) { return ClockFailure<Unit>(); }
                lastDuration = duration;
                if (policy is not null)
                {
                    if (gap > policy.MaximumHealthySampleGap) { slowCount = 0; healthySince = null; }
                    if (duration >= policy.DelayedFrameThreshold)
                    {
                        healthySince = null;
                        if (++slowCount >= policy.ConsecutiveSlowFrames)
                        {
                            effectiveRate = Math.Max(1, effectiveRate - 1);
                            slowCount = 0;
                        }
                    }
                    else
                    {
                        slowCount = 0;
                        if (healthySince is null || gap > policy.MaximumHealthySampleGap) { healthySince = receipt.CompletedTimestamp; }
                        if (timeProvider.GetElapsedTime(healthySince.Value, receipt.CompletedTimestamp) >= FramePacingPolicy.RecoveryWindow)
                        {
                            effectiveRate = Math.Min(userLimit, effectiveRate + 1);
                            healthySince = receipt.CompletedTimestamp;
                        }
                    }
                }
                lastCompleted = receipt.CompletedTimestamp;
                return new OperationResult<Unit>.Success(new Unit());
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { return ReportFailure<Unit>(exception); }
        }
    }

    public void StopAfterFailure(AppError failure)
    {
        lock (syncRoot) { error = failure; slowCount = 0; healthySince = null; measurementPending = false; }
    }

    public void CompleteUnsuccessfulAttempt()
    {
        lock (syncRoot) { measurementPending = false; slowCount = 0; healthySince = null; lastCompleted = null; }
    }

    // 只能在所属会话确认恢复后调用；保留降速结果，不通过恢复立即回到用户上限。
    public void ResumeAfterRecovery()
    {
        lock (syncRoot) { error = null; slowCount = 0; healthySince = null; lastCompleted = null; lastStarted = null; measurementPending = false; }
    }

    private OperationResult<T> ClockFailure<T>()
    {
        error = AppError.Create("PacingClockInvalid", "单调计时样本异常，已停止发送节奏", "请核实会话时钟后恢复");
        return new OperationResult<T>.Failure(error);
    }
    private OperationResult<T> ReportFailure<T>(Exception exception)
    {
        var report = reporter.Report(exception, "FramePacing");
        error = new(report.ErrorId, "PacingFailed", "发送节奏计算失败，已停止新帧", "请核实会话状态后恢复");
        return new OperationResult<T>.Failure(error);
    }
    private static OperationResult<T> Failed<T>(string code, string reason) => new OperationResult<T>.Failure(
        AppError.Create(code, reason, "请核实发送设置及测量记录"));
}
