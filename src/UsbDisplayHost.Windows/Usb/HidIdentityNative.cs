using System.Runtime.InteropServices;

namespace UsbDisplayHost.Windows.Usb;

internal static class HidIdentityNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Attributes
    {
        internal uint Size;
        internal ushort VendorId, ProductId, Version;
    }
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct Caps
    {
        [FieldOffset(0)] internal ushort Usage;
        [FieldOffset(2)] internal ushort UsagePage;
        [FieldOffset(4)] internal ushort InputBytes;
        [FieldOffset(6)] internal ushort OutputBytes;
        [FieldOffset(8)] internal ushort FeatureBytes;
    }
    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetPreparsedData(SafeUsbFileHandle handle, out SafeHidPreparsedDataHandle data);
    [DllImport("hid.dll", ExactSpelling = true)]
    internal static extern int HidP_GetCaps(SafeHidPreparsedDataHandle data, out Caps caps);
    [DllImport("hid.dll", ExactSpelling = true)]
    internal static extern void HidD_GetHidGuid(out Guid interfaceClass);
    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetAttributes(SafeUsbFileHandle handle, ref Attributes attributes);
    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetSerialNumberString(SafeUsbFileHandle handle, [Out] byte[] buffer, uint bytes);
    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetFeature(SafeUsbFileHandle handle, [In, Out] byte[] buffer, uint bytes);
    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_FlushQueue(SafeUsbFileHandle handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern SafeUsbFileHandle CreateFileW(string path, uint access, uint share, nint security,
        uint creation, uint flags, nint template);
}
