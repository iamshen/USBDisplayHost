using System.Collections.Immutable;
using UsbDisplayHost.Core.Devices;

namespace UsbDisplayHost.Core.Themes;

public enum FramePurpose { Draft, Active }
public readonly record struct FrameContext(
    DeviceKey? DeviceKey, long SessionGeneration, Guid OperationId, long ContentRevision);

// 帧拥有不可变字节副本，不向预览或发送器暴露可归还的池缓冲。
public sealed record EncodedFrame(
    ImmutableArray<byte> Jpeg, int Width, int Height, long GeneratedTimestamp,
    FrameContext Context, long FrameNumber, FramePurpose Purpose);
