using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Windows.Usb;

// 由冻结兼容记录提供，没有默认 VID/PID、接口号或端点。
public sealed record WinUsbBinding
{
    public bool DiscoverDisplayInterface { get; private init; }
    public static WinUsbBinding ForAd102V2(Guid interfaceClass) =>
        new(0xA108, 0xADA0, 0, interfaceClass, null, 0, 0, 0, 1,
            "USB_DISPLAY_AD102@b887bc1:Display FF/FF") { DiscoverDisplayInterface = true };
    private WinUsbBinding(ushort vendorId, ushort productId, ushort deviceRelease, Guid interfaceClass,
        byte? associatedInterfaceIndex, byte interfaceNumber, byte alternateSettingIndex,
        byte alternateSetting, byte bulkOutPipe, string evidenceReference)
    {
        VendorId = vendorId; ProductId = productId; DeviceRelease = deviceRelease; InterfaceClass = interfaceClass;
        AssociatedInterfaceIndex = associatedInterfaceIndex; InterfaceNumber = interfaceNumber;
        AlternateSettingIndex = alternateSettingIndex; AlternateSetting = alternateSetting;
        BulkOutPipe = bulkOutPipe; EvidenceReference = evidenceReference;
    }

    public ushort VendorId { get; }
    public ushort ProductId { get; }
    public ushort DeviceRelease { get; }
    public Guid InterfaceClass { get; }
    public byte? AssociatedInterfaceIndex { get; }
    public byte InterfaceNumber { get; }
    public byte AlternateSettingIndex { get; }
    public byte AlternateSetting { get; }
    public byte BulkOutPipe { get; }
    public string EvidenceReference { get; }

    public static OperationResult<WinUsbBinding> Create(ushort vendorId, ushort productId, ushort deviceRelease,
        Guid interfaceClass, byte? associatedInterfaceIndex, byte interfaceNumber, byte alternateSettingIndex,
        byte alternateSetting, byte bulkOutPipe, string evidenceReference)
    {
        if (vendorId == 0 || interfaceClass == Guid.Empty || bulkOutPipe is < 1 or > 15
            || string.IsNullOrWhiteSpace(evidenceReference) || evidenceReference.Length > 256)
        {
            return new OperationResult<WinUsbBinding>.Failure(AppError.Create("InvalidWinUsbBinding",
                "WinUSB 兼容记录字段无效", "请提供实际设备的接口与端点记录"));
        }
        return new OperationResult<WinUsbBinding>.Success(new(vendorId, productId, deviceRelease, interfaceClass,
            associatedInterfaceIndex, interfaceNumber, alternateSettingIndex, alternateSetting, bulkOutPipe, evidenceReference));
    }
}
