using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Transport;

// 当前固件源码的协议配置；不声明实际镜像、驱动或接口已核验。
public static class Ad102V2SourceProtocol
{
    public const string SourceRevision = "b887bc192d1ef45ebe40662aecfbcddfbb6e516d";
    public const int Width = 1920;
    public const int Height = 1080;
    public const string FirmwareIdentity = "AD102 V2.0 source b887bc1";
    public const string Evidence = "USB_DISPLAY_AD102@b887bc1:usb_v2_hid.c/usb_display.c";

    public static OperationResult<DeviceCapabilities> CreateCapabilities(Ad102V2Feature feature)
    {
        if (feature.DeclaredCapabilities != 0x15F || feature.MaintenanceBusy
            || feature.CurrentMode != feature.TargetMode || feature.CurrentMode is not (Ad102V2Mode.Theme or Ad102V2Mode.Screen))
        { return new OperationResult<DeviceCapabilities>.Failure(AppError.Create("SourceProtocolMismatch",
            "设备协议声明与当前源码基线不匹配或设备正在切换／维护", "核对固件后重新连接")); }
        DeviceCommand[] commands = feature.CurrentMode == Ad102V2Mode.Theme
            ? [DeviceCommand.QueryState, DeviceCommand.SwitchMode, DeviceCommand.SetBrightness, DeviceCommand.SetPower]
            : [DeviceCommand.QueryState, DeviceCommand.SwitchMode];
        return DeviceCapabilities.Create(2, Width, Height, 1_048_576, 245_760, FirmwareIdentity,
            Evidence, CapabilityEvidence.SourceMatched, commands, false);
    }

    public static OperationResult<FirmwareJpegProfile> CreateJpegProfile() => FirmwareJpegProfile.Create(
        FirmwareIdentity, Evidence,
        [new(0xC0, 8, [new(1, 2, 2), new(2, 1, 1), new(3, 1, 1)]),
         new(0xC0, 8, [new(1, 2, 1), new(2, 1, 1), new(3, 1, 1)]),
         new(0xC0, 8, [new(1, 1, 1), new(2, 1, 1), new(3, 1, 1)])]);
    private const byte ReadyMask = 0x08 | 0x10;
    private const byte Xrgb888 = 1 << 1;

    // 接口号必须来自所选 Display function 的描述符，不固定为 MI_00。
    public static OperationResult<ThemeDataPlaneContract> CreateThemeContract(string firmwareVersion, byte displayInterfaceNumber) =>
        ThemeDataPlaneContract.CreateWithEdid(firmwareVersion,
            $"USB_DISPLAY_AD102@{SourceRevision}:gadget_display.h/f_display.c/usb_display.c",
            new ControlSetup(0xC1, 0x01, 0, displayInterfaceNumber), [ReadyMask], [ReadyMask],
            new ControlSetup(0xC1, 0x02, 0, displayInterfaceNumber), 54,
            queriesReadOnly: true, queriesSafeWhileOff: true, colorFormat: Xrgb888);

    public static ControlSetup CreateBacklightQuery(byte displayInterfaceNumber) =>
        new(0xC1, 0x04, 0, displayInterfaceNumber);
}
