using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UsbDisplayHost.Windows.Usb;

// USB 会话与身份探测共用文件所有权；IsClosed 不代表 CloseHandle 已确认成功。
internal sealed class SafeUsbFileHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeUsbFileHandle() : base(true) { }
    internal bool ReleaseConfirmed { get; private set; }
    internal Exception? ReleaseError { get; private set; }
    protected override bool ReleaseHandle()
    {
        try
        {
            ReleaseConfirmed = CloseHandle(handle);
            if (!ReleaseConfirmed) { ReleaseError = new Win32Exception(Marshal.GetLastPInvokeError()); }
            return ReleaseConfirmed;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ReleaseError = exception;
            return false;
        }
    }
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
