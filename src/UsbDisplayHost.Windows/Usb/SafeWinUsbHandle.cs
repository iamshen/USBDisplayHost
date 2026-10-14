using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UsbDisplayHost.Windows.Usb;

internal sealed class SafeWinUsbHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly SafeHandle file;
    private bool fileReference;
    internal bool ReleaseConfirmed { get; private set; }
    internal bool NativeReleaseConfirmed { get; private set; }
    internal Exception? ReleaseError { get; private set; }

    internal SafeWinUsbHandle(nint value, SafeHandle file) : base(true)
    {
        this.file = file;
        SetHandle(value);
        try { file.DangerousAddRef(ref fileReference); }
        catch { Dispose(); throw; }
    }

    protected override bool ReleaseHandle()
    {
        try
        {
            NativeReleaseConfirmed = WinUsbNative.WinUsb_Free(handle);
            if (!NativeReleaseConfirmed)
            {
                ReleaseError = new Win32Exception(Marshal.GetLastPInvokeError());
                return false;
            }
            // 仅在原生释放确认后解除文件引用；引用解除异常仍需保留完整所有者。
            if (fileReference) { file.DangerousRelease(); fileReference = false; }
            ReleaseConfirmed = true;
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ReleaseError = exception;
            return false;
        }
    }
}
