using System.Runtime.InteropServices;

namespace UsbDisplayHost.WinUI.Services;

internal sealed class WindowMessageHook : IDisposable
{
    private const uint NonClientDestroy = 0x0082;
    private const nuint SubclassId = 1;
    private readonly Func<uint, nint, nint, nint?> processMessage;
    private readonly SubclassProc callback;
    private nint handle;

    public WindowMessageHook(nint handle, Func<uint, nint, nint, nint?> processMessage)
    {
        this.handle = handle;
        this.processMessage = processMessage;
        callback = OnMessage;
        if (!SetWindowSubclass(handle, callback, SubclassId, 0))
        {
            throw new InvalidOperationException("无法注册主窗口系统消息");
        }
    }

    public void Dispose()
    {
        if (handle == 0) { return; }
        RemoveWindowSubclass(handle, callback, SubclassId);
        handle = 0;
    }

    private nint OnMessage(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == NonClientDestroy) { Dispose(); }
        else if (processMessage(message, wParam, lParam) is { } handled) { return handled; }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);
}
