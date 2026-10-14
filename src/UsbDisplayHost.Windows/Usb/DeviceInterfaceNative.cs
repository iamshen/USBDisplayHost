using System.Runtime.InteropServices;

namespace UsbDisplayHost.Windows.Usb;

internal static class DeviceInterfaceNative
{
    // Windows SDK cfgmgr32.h: DWORD 四项 + 最大 union WCHAR[200]，x64/x86 均为 416 字节。
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    internal struct NotificationFilter
    {
        [FieldOffset(0)] internal uint Size;
        [FieldOffset(4)] internal uint Flags;
        [FieldOffset(8)] internal uint FilterType;
        [FieldOffset(12)] internal uint Reserved;
        [FieldOffset(16)] internal Guid InterfaceClass;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint NotificationCallback(nint notification, nint context, uint action, nint eventData, uint eventDataSize);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    internal static extern uint CM_Register_Notification(ref NotificationFilter filter, nint context,
        NotificationCallback callback, out nint notification);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    internal static extern uint CM_Unregister_Notification(nint notification);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string? deviceId,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] buffer, uint length, uint flags);
}
