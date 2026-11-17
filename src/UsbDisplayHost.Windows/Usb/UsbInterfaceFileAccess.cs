using System.ComponentModel;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Windows.Usb;

internal static class UsbInterfaceFileAccess
{
    private const uint FileShareReadWrite = 0x00000003;
    private const int ErrorSharingViolation = 32;

    public static uint GetShareMode(bool exclusiveAccess) => exclusiveAccess ? 0 : FileShareReadWrite;

    public static AppError? DescribeSharingViolation(Exception exception, string errorId, bool exclusiveFileOpen)
        => exclusiveFileOpen && exception is Win32Exception { NativeErrorCode: ErrorSharingViolation }
            ? new(errorId, "UsbInterfaceInUse", "USB 接口已被占用", "请在占用该设备的应用中断开连接，然后重新检测或连接")
            : null;
}
