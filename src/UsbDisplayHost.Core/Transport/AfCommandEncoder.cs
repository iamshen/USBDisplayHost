using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Transport;

public enum AfDisplayCommand : byte
{
    ColorFormat = 0x00, WidthHigh = 0x01, WidthLow = 0x02, HeightHigh = 0x03, HeightLow = 0x04,
    Backlight = 0x05, AutoRlx = 0x06, Mjpeg = 0x11, BlankMode = 0x1F,
    ClearParser = 0x30, Rotation = 0x34, Power = 0x36, Heartbeat = 0x38
}

public static class AfCommandEncoder
{
    public const int CommandBytes = 4;

    public static OperationResult<int> TryWrite(AfDisplayCommand command, byte value, Span<byte> destination)
    {
        if (!Enum.IsDefined(command)) { return Reject("UnknownDisplayCommand", "数据面命令不在已定义白名单", "请核对冻结协议，不试发扩展命令"); }
        if (command == AfDisplayCommand.Backlight && value > 100
            || command is AfDisplayCommand.AutoRlx or AfDisplayCommand.Mjpeg && value > 1
            || command == AfDisplayCommand.BlankMode && value != 1
            || command is AfDisplayCommand.ClearParser or AfDisplayCommand.Heartbeat && value != 0
            || command == AfDisplayCommand.Rotation && value > 3
            || command == AfDisplayCommand.Power && value > 1)
        {
            return Reject("InvalidDisplayCommandValue", "命令参数超出当前支持范围", "请使用协议确认值；熄屏通过独立已验证控制路径处理");
        }
        if (destination.Length < CommandBytes) { return Reject("CommandBufferTooSmall", "命令缓冲不足", "请使用至少四字节缓冲"); }
        destination[0] = 0xAF;
        destination[1] = 0x20;
        destination[2] = (byte)command;
        destination[3] = value;
        return new OperationResult<int>.Success(CommandBytes);
    }

    private static OperationResult<int> Reject(string code, string reason, string action) =>
        new OperationResult<int>.Failure(AppError.Create(code, reason, action));
}
