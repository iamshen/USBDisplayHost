using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Transport;

public sealed class FramePacingPolicy
{
    private FramePacingPolicy(TimeSpan delayedFrameThreshold, int consecutiveSlowFrames,
        TimeSpan maximumHealthySampleGap, string evidenceReference)
    {
        DelayedFrameThreshold = delayedFrameThreshold; ConsecutiveSlowFrames = consecutiveSlowFrames;
        MaximumHealthySampleGap = maximumHealthySampleGap; EvidenceReference = evidenceReference;
    }
    public TimeSpan DelayedFrameThreshold { get; }
    public int ConsecutiveSlowFrames { get; }
    public TimeSpan MaximumHealthySampleGap { get; }
    public string EvidenceReference { get; }
    public static TimeSpan RecoveryWindow => TimeSpan.FromSeconds(30);

    public static OperationResult<FramePacingPolicy> Create(TimeSpan delayedFrameThreshold, int consecutiveSlowFrames,
        TimeSpan maximumHealthySampleGap, string evidenceReference)
    {
        if (delayedFrameThreshold <= TimeSpan.Zero || delayedFrameThreshold > TimeSpan.FromSeconds(60)
            || consecutiveSlowFrames is < 1 or > 60 || maximumHealthySampleGap < TimeSpan.FromSeconds(1)
            || maximumHealthySampleGap >= RecoveryWindow || maximumHealthySampleGap <= delayedFrameThreshold
            || string.IsNullOrWhiteSpace(evidenceReference) || evidenceReference.Length > 256)
        {
            return new OperationResult<FramePacingPolicy>.Failure(AppError.Create("InvalidPacingPolicy",
                "发送节奏测量阈值或证据无效", "请登记目标 PC／固件／负载测量记录"));
        }
        return new OperationResult<FramePacingPolicy>.Success(new(delayedFrameThreshold, consecutiveSlowFrames,
            maximumHealthySampleGap, evidenceReference));
    }
}
