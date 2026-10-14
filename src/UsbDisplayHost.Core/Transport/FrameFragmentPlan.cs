using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Transport;

public readonly record struct FrameFragment(int PayloadOffset, int PayloadBytes, int HeaderBytes, int PacketBytes, bool IsFinal);

// 仅负责字节分片；合法 JPEG 和匹配固件必须在完整发送入口另行验证。
public sealed class FrameFragmentPlan
{
    public const int MaximumFrameBytes = 1_048_576;
    public const int MaximumTransferBytes = 245_760;
    private FrameFragmentPlan(int frameBytes, int transferBytes)
    {
        FrameBytes = frameBytes;
        TransferBytes = transferBytes;
    }
    public int FrameBytes { get; }
    public int TransferBytes { get; }

    public static OperationResult<FrameFragmentPlan> Create(int frameBytes, int firmwareFrameLimit,
        int firmwareTransferLimit, int hostTransferLimit)
    {
        int frameLimit = Math.Min(MaximumFrameBytes, firmwareFrameLimit);
        int transfer = Math.Min(MaximumTransferBytes, Math.Min(firmwareTransferLimit, hostTransferLimit));
        if (frameBytes <= 0 || frameLimit <= 0 || frameBytes > frameLimit)
        {
            return new OperationResult<FrameFragmentPlan>.Failure(AppError.Create("InvalidFrameLength",
                "帧为空或超过允许长度", "请检查编码结果及已验证的固件帧上限"));
        }
        if (transfer <= 11)
        {
            return new OperationResult<FrameFragmentPlan>.Failure(AppError.Create("InvalidTransferLimit",
                "传输上限无法容纳非空尾片", "请核对固件与主机传输上限"));
        }
        return new OperationResult<FrameFragmentPlan>.Success(new FrameFragmentPlan(frameBytes, transfer));
    }

    public FrameFragmentCursor CreateCursor() => new(this);
}

// 一个发送执行者持有游标和可复用缓冲；必须等上一片原生完成后才写下一片。
public sealed class FrameFragmentCursor
{
    private readonly FrameFragmentPlan plan;
    private int offset;
    internal FrameFragmentCursor(FrameFragmentPlan plan) => this.plan = plan;
    public int RemainingBytes => plan.FrameBytes - offset;
    public bool IsComplete => offset == plan.FrameBytes;

    public OperationResult<FrameFragment> TryWriteNext(ReadOnlySpan<byte> frame, Span<byte> destination)
    {
        if (frame.Length != plan.FrameBytes)
        {
            return Reject("FrameLengthChanged", "帧长度与受理时不一致", "请使用同一不可变完整帧重新开始发送");
        }
        if (IsComplete) { return Reject("FrameAlreadyFragmented", "该帧已完成分片", "请勿追加空尾片或重复该发送游标"); }
        int remaining = RemainingBytes;
        bool final = remaining <= plan.TransferBytes - 11;
        int headerBytes = final ? 11 : 8;
        int payloadBytes = final ? remaining : Math.Min(plan.TransferBytes - 8, remaining - 1);
        int packetBytes = headerBytes + payloadBytes;
        if (destination.Length < packetBytes)
        {
            return Reject("TransferBufferTooSmall", "传输缓冲不足，未推进分片", "请按协商的最大传输长度分配缓冲");
        }
        destination[0] = 0xAF;
        destination[1] = final ? (byte)0x69 : (byte)0x68;
        WriteUInt24(destination[2..5], offset);
        WriteUInt24(destination[5..8], payloadBytes);
        if (final) { destination[8..11].Clear(); }
        frame.Slice(offset, payloadBytes).CopyTo(destination.Slice(headerBytes, payloadBytes));
        var fragment = new FrameFragment(offset, payloadBytes, headerBytes, packetBytes, final);
        offset += payloadBytes;
        return new OperationResult<FrameFragment>.Success(fragment);
    }

    private static void WriteUInt24(Span<byte> destination, int value)
    {
        destination[0] = (byte)(value >> 16);
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)value;
    }
    private static OperationResult<FrameFragment> Reject(string code, string reason, string action) =>
        new OperationResult<FrameFragment>.Failure(AppError.Create(code, reason, action));
}
