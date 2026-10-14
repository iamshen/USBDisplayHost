using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UsbDisplayHost.Windows.Storage;

internal static class AssetFileDeletion
{
    // 打开后核对真实路径与文件属性，再对同一独占句柄标记删除，避免路径替换造成越界删除。
    public static void DeleteVerified(string path, Action enteringDeletion)
    {
        using (var handle = CreateFileW(path, 0x00010080, 0, nint.Zero, 3, 0x00200000, nint.Zero))
        {
            if (handle.IsInvalid) { throw NativeFailure(); }
            if (!GetFileInformationByHandle(handle, out var information)) { throw NativeFailure(); }
            if ((information.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException("Asset deletion requires an ordinary file.");
            }
            var buffer = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) { throw NativeFailure(); }
            string full = Path.GetFullPath(path);
            string expected = full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
            if (length >= buffer.Capacity || !StringComparer.OrdinalIgnoreCase.Equals(buffer.ToString(), expected))
            {
                throw new InvalidDataException("Asset handle resolved outside the verified file path.");
            }
            // FILE_DISPOSITION_INFO.DeleteFile 为 BOOLEAN（一个字节），不是 Win32 BOOL。
            var disposition = new FileDisposition { DeleteFile = 1 };
            enteringDeletion();
            if (!SetFileInformationByHandle(handle, 4, ref disposition, 1)) { throw NativeFailure(); }
        }
        VerifyAbsent(path);
    }

    public static void VerifyAbsent(string path)
    {
        uint attributes = GetFileAttributesW(path);
        if (attributes != uint.MaxValue) { throw new IOException("Asset deletion is not confirmed."); }
        int error = Marshal.GetLastWin32Error();
        if (error is not (2 or 3)) { throw new Win32Exception(error); }
    }

    private static Win32Exception NativeFailure() => new(Marshal.GetLastWin32Error());

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { public byte DeleteFile; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME AccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME WriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Links;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref FileDisposition disposition, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFileAttributesW(string path);
}
