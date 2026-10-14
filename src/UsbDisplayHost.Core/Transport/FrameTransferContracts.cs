using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Core.Transport;

public enum FrameTransferState { RecoveryRequired, Ready, IoUnconfirmed }
public sealed record FrameTransferStatus(FrameTransferState State, long UsbCompletedFrames,
    long ConfirmedPacketBytes, AppError? Error);
public sealed record FrameTransferReceipt(FrameContext Context, long FrameNumber, int JpegBytes,
    int PacketCount, long PacketBytes, long CompletedTimestamp);
public sealed record CompletedHostFrame(EncodedFrame Frame, FrameTransferReceipt Receipt);

public interface IFrameSessionGuard
{
    // 帧边界核对修订／操作及 Theme、On 意图；不能把离线草稿当活动帧。
    bool CanStartFrame(FrameContext context);
    // 帧内仅核对身份／代次，普通新内容不能截断已经开始的完整帧。
    bool IsLiveSession(DeviceKey deviceKey, long sessionGeneration);
    OperationResult<OperationContext> ResolveRecoveryContext(FrameContext context);
}

public sealed record FrameParserRecoveryReceipt(DeviceKey DeviceKey, long SessionGeneration,
    Guid OperationId, string FirmwareVersion, string EvidenceReference, bool ParserCleared,
    bool PreservedPowerIntent);

public interface IFrameParserRecovery
{
    // 仅可实施冻结固件契约认可的清理并核实结果；ResetPipe 本身不满足此契约。
    // 返回前收敛本次恢复 I/O，不得执行会覆盖熄屏／切模式意图的亮屏序列。
    Task<OperationResult<FrameParserRecoveryReceipt>> RecoverAsync(OperationContext context,
        CancellationToken cancellationToken);
    // 包括恢复异常／取消后的内核请求与取消回调；仅 Success 允许释放恢复所有者。
    Task<OperationResult<Unit>> DrainAsync();
}
