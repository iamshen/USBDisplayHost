using System.Collections.Immutable;
using Microsoft.Win32;
using UsbDisplayHost.Core.Transport;

namespace UsbDisplayHost.Windows.Usb;

public static class Ad102V2DiscoveryConfiguration
{
    public static readonly Guid HidInterfaceClass = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    // 固件只声明 WINUSB compatible ID；设备接口 GUID 由实际 Windows 驱动注册，不能从 MI 编号猜测。
    public static WindowsDiscoveryConfiguration ReadInstalledBindings()
    {
        var classes = new HashSet<Guid>();
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb is not null)
        {
            foreach (string deviceName in usb.GetSubKeyNames().Where(name => name.StartsWith("VID_A108&PID_ADA0", StringComparison.OrdinalIgnoreCase)))
            {
                using var device = usb.OpenSubKey(deviceName);
                if (device is null) { continue; }
                foreach (string instanceName in device.GetSubKeyNames())
                {
                    using var instance = device.OpenSubKey(instanceName);
                    if (instance is null || !string.Equals(instance.GetValue("Service") as string, "WinUSB", StringComparison.OrdinalIgnoreCase)) { continue; }
                    using var parameters = instance.OpenSubKey("Device Parameters");
                    var raw = parameters?.GetValue("DeviceInterfaceGUIDs");
                    IEnumerable<string> values = raw is string[] list ? list : raw is string text ? [text] : [];
                    foreach (string value in values)
                    { if (Guid.TryParse(value, out var guid) && guid != Guid.Empty) { classes.Add(guid); } }
                }
            }
        }
        var collection = new HidCollectionCompatibility(0xFF00, 1, 64, 64, 64);
        return new(HidInterfaceClass,
            [new(0xA108, 0xADA0, null, Ad102V2SourceProtocol.Evidence, collection),
             new(0xA108, 0xAD11, null, Ad102V2SourceProtocol.Evidence, collection)],
            classes.Select(guid => new WinUsbIdentityCompatibility(guid, 0xA108, 0xADA0, null,
                Ad102V2SourceProtocol.Evidence, true)).ToImmutableArray());
    }
}
