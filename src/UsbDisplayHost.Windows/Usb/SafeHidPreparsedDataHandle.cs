using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UsbDisplayHost.Windows.Usb;

internal sealed class SafeHidPreparsedDataHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeHidPreparsedDataHandle() : base(true) { }
    internal bool ReleaseConfirmed { get; private set; }
    internal Exception? ReleaseError { get; private set; }
    protected override bool ReleaseHandle()
    {
        try
        {
            ReleaseConfirmed = HidD_FreePreparsedData(handle);
            if (!ReleaseConfirmed) { ReleaseError = new IOException("HID preparsed data release unconfirmed."); }
            return ReleaseConfirmed;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { ReleaseError = exception; return false; }
    }
    [DllImport("hid.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(nint data);
}
