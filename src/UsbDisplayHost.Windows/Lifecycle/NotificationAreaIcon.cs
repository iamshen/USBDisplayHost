using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UsbDisplayHost.Windows.Lifecycle;

public sealed class NotificationAreaIcon : IDisposable
{
    public const int CallbackMessage = 0x8001;
    private NotifyIconData data;
    private bool disposed;

    public NotificationAreaIcon(nint window, string iconPath)
    {
        data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 1,
            Flags = 1 | 2 | 4 | 128, Callback = CallbackMessage,
            Icon = NativeMethods.LoadImageW(0, iconPath, 1,
                NativeMethods.GetSystemMetrics(49), NativeMethods.GetSystemMetrics(50), 0x10),
            Tip = "USB Display",
            Info = "", InfoTitle = "", Version = 4
        };
        if (data.Icon == 0) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        try
        {
            TaskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
            if (TaskbarCreatedMessage == 0) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            Add();
        }
        catch
        {
            NativeMethods.DestroyIcon(data.Icon);
            throw;
        }
    }

    public uint TaskbarCreatedMessage { get; }
    public bool IsAvailable { get; private set; }

    public void Add()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        data.Flags = 1 | 2 | 4 | 128;
        IsAvailable = NativeMethods.Shell_NotifyIconW(0, ref data);
        if (!IsAvailable) { throw new InvalidOperationException("托盘图标无法加入通知区域"); }
        if (!NativeMethods.Shell_NotifyIconW(4, ref data))
        {
            NativeMethods.Shell_NotifyIconW(2, ref data);
            IsAvailable = false;
            throw new InvalidOperationException("托盘通知版本无法初始化");
        }
    }

    public bool ShowFirstNotice()
    {
        if (disposed || !IsAvailable) { return false; }
        data.Flags = 16;
        data.InfoTitle = "USB Display 仍在运行";
        data.Info = "关闭窗口会收至托盘。可从托盘打开窗口或选择退出。";
        data.InfoFlags = 1;
        bool result = NativeMethods.Shell_NotifyIconW(1, ref data);
        data.Info = "";
        data.InfoTitle = "";
        return result;
    }

    public void ActivateMenuOwner() => NativeMethods.SetForegroundWindow(data.Window);

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        if (IsAvailable) { NativeMethods.Shell_NotifyIconW(2, ref data); }
        IsAvailable = false;
        // 文件加载的图标由本实例持有，在移除通知区域图标后释放。
        NativeMethods.DestroyIcon(data.Icon);
        data.Icon = 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint LoadImageW(nint instance, string name, uint type, int width, int height, uint flags);
        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(nint icon);
        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint RegisterWindowMessageW(string message);
        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(nint window);
    }
}
