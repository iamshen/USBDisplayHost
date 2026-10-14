using System.Runtime.InteropServices;

namespace UsbDisplayHost.Windows.Usb;

internal static class HidIoNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct OverlappedData
    {
        internal nuint Internal, InternalHigh;
        internal uint Offset, OffsetHigh;
        internal nint Event;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadFile(SafeUsbFileHandle file, nint buffer, uint bytes,
        nint transferred, nint overlapped);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteFile(SafeUsbFileHandle file, nint buffer, uint bytes,
        nint transferred, nint overlapped);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetOverlappedResult(SafeUsbFileHandle file, nint overlapped,
        out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CancelIoEx(SafeUsbFileHandle file, nint overlapped);
}
