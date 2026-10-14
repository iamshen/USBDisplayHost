using System.Buffers.Binary;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;

namespace UsbDisplayHost.Core.Devices;

public enum Ad102V2Mode : byte { Screen, Theme, Maintenance, Switching }
public enum Ad102V2HidResult : byte { None, Accepted, Busy, Invalid, Unsupported }

public sealed record Ad102V2Feature(Ad102V2Mode CurrentMode, Ad102V2Mode TargetMode,
    bool MaintenanceBusy, uint DeclaredCapabilities);

public sealed record Ad102V2Status(Ad102V2Mode CurrentMode, Ad102V2Mode TargetMode,
    bool DisplayEnabled, bool AutoRlx, bool Switching, byte RawBacklight, byte Rotation,
    bool MaintenanceBusy, byte ErrorCode, byte LastHidCommand, Ad102V2HidResult LastHidResult,
    byte LastToken, uint DeclaredCapabilities, byte LastDisplayCommand, byte LastDisplayResult,
    ushort DisplayCommandCount);

// 仅描述已核对的 V2.0 字节布局；collection、身份和会话归属仍由适配器核实。
public sealed class Ad102V2HidCodec
{
    public const int ReportBytes = 64;
    public const ushort UsagePage = 0xFF00;
    public const ushort Usage = 1;
    public const byte StatusProbeCommand = 0;
    public const byte StatusProbeToken = 255;
    private Ad102V2HidCodec(Ad102V2Feature feature) => Feature = feature;
    public Ad102V2Feature Feature { get; }

    // V2.0 没有周期 Input 或 GET_REPORT(Input)。未知命令只更新命令诊断并回传状态，
    // 不执行设备动作；此探测专属于当前源码，不代表固件新增了查询命令。
    public byte[] CreateStatusProbe()
    {
        byte[] report = new byte[ReportBytes];
        report[0] = 2;
        report[1] = StatusProbeCommand;
        report[3] = StatusProbeToken;
        return report;
    }

    public static OperationResult<Ad102V2HidCodec> Create(ReadOnlySpan<byte> feature)
    {
        if (feature.Length != ReportBytes || feature[0] != 3 || feature[1] != 2 || feature[2] != 0
            || feature[3] > 3 || feature[4] > 3 || feature[5] > 1 || !IsZero(feature[10..]))
        {
            return Failure<Ad102V2HidCodec>("InvalidV2Feature", "HID Feature 长度、版本或字段不符合 AD102 V2.0");
        }
        return new OperationResult<Ad102V2HidCodec>.Success(new(new((Ad102V2Mode)feature[3],
            (Ad102V2Mode)feature[4], feature[5] != 0, BinaryPrimitives.ReadUInt32LittleEndian(feature[6..10]))));
    }

    public OperationResult<Ad102V2Status> DecodeInput(ReadOnlySpan<byte> report)
    {
        if (report.Length != ReportBytes || report[0] != 1 || (report[1] & ~0x0B) != 0
            || report[12] > 3 || report[13] > 3 || report[14] > 1 || report[15] > 3
            || report[17] > 4 || report[25] > 3 || report[28] > 3
            || !IsZero(report[3..12]) || report[19] != 0 || !IsZero(report[29..]))
        {
            return Failure<Ad102V2Status>("InvalidV2Input", "HID Input 长度、标识或字段不符合 AD102 V2.0");
        }
        uint capabilities = BinaryPrimitives.ReadUInt32LittleEndian(report[20..24]);
        if (capabilities != Feature.DeclaredCapabilities)
        {
            return Failure<Ad102V2Status>("V2CapabilitiesChanged", "HID Input 与本会话 Feature 能力声明不一致");
        }
        byte expectedError = report[17] switch { 2 => 1, 3 => 2, 4 => 3, _ => 0 };
        if (report[15] != expectedError || report[18] == 0 && (report[16] != 0 || report[17] != 0))
        {
            return Failure<Ad102V2Status>("InvalidV2CommandResult", "HID 最近命令的 token 或结果关联不一致");
        }
        return new OperationResult<Ad102V2Status>.Success(new((Ad102V2Mode)report[12],
            (Ad102V2Mode)report[13], (report[1] & 1) != 0, (report[1] & 2) != 0,
            (report[1] & 8) != 0, report[2], report[28], report[14] != 0, report[15],
            report[16], (Ad102V2HidResult)report[17], report[18], capabilities,
            report[24], report[25], BinaryPrimitives.ReadUInt16LittleEndian(report[26..28])));
    }

    public OperationResult<int> WriteModeSwitch(DeviceMode target, byte token, Span<byte> destination)
    {
        if (destination.Length != ReportBytes || token == 0 || target is not (DeviceMode.Theme or DeviceMode.SecondaryDisplay))
        {
            return Failure<int>("InvalidV2ModeRequest", "HID 模式请求的目标、token 或缓冲长度无效");
        }
        // 线上的值与固件内部 SCREEN=0／THEME=1 枚举方向相反。
        destination.Clear();
        destination[0] = 2; destination[1] = 0x31;
        destination[2] = target == DeviceMode.Theme ? (byte)0 : (byte)1;
        destination[3] = token;
        return new OperationResult<int>.Success(ReportBytes);
    }

    private static bool IsZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes) { if (value != 0) { return false; } }
        return true;
    }

    private static OperationResult<T> Failure<T>(string code, string reason) =>
        new OperationResult<T>.Failure(AppError.Create(code, reason, "核对当前固件与报告，不修改设备状态或自动重试"));
}
