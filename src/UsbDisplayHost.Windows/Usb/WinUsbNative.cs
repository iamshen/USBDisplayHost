using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UsbDisplayHost.Windows.Usb;

internal static class WinUsbNative
{
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern SafeUsbFileHandle CreateFile(string path, uint access, uint share, nint security,
        uint creation, uint flags, nint template);
    [DllImport("winusb.dll", EntryPoint = "WinUsb_Initialize", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_Initialize(SafeUsbFileHandle file, out nint handle);
    [StructLayout(LayoutKind.Sequential)]
    internal struct OverlappedData
    {
        internal nuint Internal, InternalHigh;
        internal uint Offset, OffsetHigh;
        internal nint Event;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct SetupPacket
    {
        internal byte RequestType, Request;
        internal ushort Value, Index, Length;
    }
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_WritePipe(SafeWinUsbHandle handle, byte pipe, nint buffer,
        uint length, nint transferred, nint overlapped);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_ControlTransfer(SafeWinUsbHandle handle, SetupPacket setup,
        nint buffer, uint length, nint transferred, nint overlapped);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_GetOverlappedResult(SafeWinUsbHandle primary, nint overlapped,
        out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CancelIoEx(SafeUsbFileHandle file, nint overlapped);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_SetPipePolicy(SafeWinUsbHandle handle, byte pipe, uint policy,
        uint length, ref uint value);
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct InterfaceDescriptor
    {
        internal byte Length, DescriptorType, InterfaceNumber, AlternateSetting, EndpointCount;
        internal byte InterfaceClass, InterfaceSubClass, InterfaceProtocol, InterfaceString;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PipeInformation
    {
        internal int PipeType;
        internal byte PipeId;
        internal ushort MaximumPacketSize;
        internal byte Interval;
    }

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_Free(nint handle);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_GetAssociatedInterface(SafeWinUsbHandle handle, byte index, out nint associated);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_GetDescriptor(SafeWinUsbHandle handle, byte type, byte index, ushort language,
        [Out] byte[] buffer, uint bufferLength, out uint transferred);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_QueryInterfaceSettings(SafeWinUsbHandle handle, byte alternateIndex,
        out InterfaceDescriptor descriptor);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_GetCurrentAlternateSetting(SafeWinUsbHandle handle, out byte setting);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_QueryPipe(SafeWinUsbHandle handle, byte alternateIndex, byte pipeIndex,
        out PipeInformation pipe);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_GetPipePolicy(SafeWinUsbHandle handle, byte pipe, uint policy,
        ref uint length, out uint value);
}
