using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Desktop.ViewModels;
using UsbDisplayHost.Windows.Lifecycle;

namespace UsbDisplayHost.WinUI.Services;

internal sealed class WinUiTray : IDisposable
{
    private const uint ContextMenu = 0x007b;
    private const uint Select = 0x0400;
    private const uint KeySelect = 0x0401;
    private const uint LeftButtonDoubleClick = 0x0203;
    private readonly nint window;
    private readonly NotificationAreaIcon icon;
    private readonly ApplicationSettingsService settings;
    private readonly DevicesPageViewModel devices;
    private readonly Action openWindow;
    private readonly Action<string> showError;
    private readonly IErrorReporter reporter;
    private readonly (string Label, ICommand? Command)[] menu;
    private bool noticeShown;
    private bool disposed;
    private Task<bool>? hiding;

    public WinUiTray(nint window, ApplicationSettingsService settings, DevicesPageViewModel devices,
        Action openWindow, Action requestExit, Action<string> showError, IErrorReporter reporter)
    {
        this.window = window;
        this.settings = settings;
        this.devices = devices;
        this.openWindow = openWindow;
        this.showError = showError;
        this.reporter = reporter;
        menu =
        [
            ($"打开 {Program.ProductName}", new RelayCommand(openWindow)),
            (string.Empty, null),
            ("切换主题", devices.SwitchThemeCommand),
            ("切换副屏", devices.SwitchScreenCommand),
            ("暂停推流", devices.PauseStreamingCommand),
            ("恢复推流", devices.ResumeStreamingCommand),
            (string.Empty, null),
            ("退出", new RelayCommand(requestExit))
        ];
        icon = new NotificationAreaIcon(window, Path.Combine(AppContext.BaseDirectory, "Assets", "Branding", "app.ico"),
            Program.ProductName);
        devices.PropertyChanged += OnDeviceChanged;
    }

    public bool IsAvailable => !disposed && icon.IsAvailable;

    public Task<bool> HideWindowAsync(Action hideWindow) =>
        hiding is { IsCompleted: false } ? hiding : hiding = HideCoreAsync(hideWindow);

    public Task WaitForPendingNoticeAsync() => hiding ?? Task.CompletedTask;

    private async Task<bool> HideCoreAsync(Action hideWindow)
    {
        if (!IsAvailable) { return false; }
        if (!noticeShown)
        {
            var result = await settings.LoadAsync(CancellationToken.None);
            if (!IsAvailable) { return false; }
            var read = (result as OperationResult<ApplicationSettingsReadResult>.Success)?.Value;
            if (read?.Settings.HasSeenTrayNotice != true)
            {
                if (!icon.ShowFirstNotice()) { return false; }
                noticeShown = true;
                if (read is { Error: null })
                {
                    var saved = await settings.MarkTrayNoticeAsync(CancellationToken.None);
                    if (saved is OperationResult<Unit>.Failure failure) { Present(failure.Error); }
                    else if (saved is OperationResult<Unit>.Unknown unknown) { Present(unknown.Error); }
                }
                else if (read?.Error is { } error) { Present(error); }
                else if (result is OperationResult<ApplicationSettingsReadResult>.Failure failure) { Present(failure.Error); }
            }
            else { noticeShown = true; }
        }
        if (!IsAvailable) { return false; }
        hideWindow();
        return true;
    }

    public nint? HandleMessage(uint message, nint lParam)
    {
        if (disposed) { return null; }
        try
        {
            if (message == icon.TaskbarCreatedMessage) { icon.Add(); }
            else if (message == NotificationAreaIcon.CallbackMessage)
            {
                uint notification = (uint)(lParam & 0xffff);
                if (notification is Select or KeySelect or LeftButtonDoubleClick) { openWindow(); }
                else if (notification == ContextMenu) { OpenContextMenu(); }
                return 0;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "Tray");
            showError($"托盘操作未完成，请从窗口重试。错误编号：{report.ErrorId}");
            openWindow();
        }
        return null;
    }

    private void OpenContextMenu()
    {
        devices.RefreshStreamingActions();
        nint popup = CreatePopupMenu();
        if (popup == 0) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }

        uint selected;
        try
        {
            for (int index = 0; index < menu.Length; index++)
            {
                var item = menu[index];
                uint flags = item.Command is null ? 0x800u : item.Command.CanExecute(null) ? 0u : 0x1u;
                if (!AppendMenuW(popup, flags, (nuint)(index + 1), item.Label))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
            if (!icon.TryGetMenuAnchor(out int x, out int y))
            {
                if (!GetCursorPos(out var point)) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
                (x, y) = (point.X, point.Y);
            }
            icon.ActivateMenuOwner();
            const uint RightButton = 0x2;
            const uint BottomAlign = 0x20;
            const uint NoNotify = 0x80;
            const uint ReturnCommand = 0x100;
            selected = TrackPopupMenu(popup, RightButton | BottomAlign | NoNotify | ReturnCommand, x, y, 0, window, 0);
            icon.FinishMenu();
        }
        finally { DestroyMenu(popup); }

        if (selected > 0 && selected <= menu.Length && menu[selected - 1].Command is { } command && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void OnDeviceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (disposed || args.PropertyName != nameof(DevicesPageViewModel.StreamingErrorText)
            || string.IsNullOrWhiteSpace(devices.StreamingErrorText)) { return; }
        showError(devices.StreamingErrorText);
        openWindow();
    }

    private void Present(AppError error) => showError($"{error.Reason}。{error.Action}。错误编号：{error.ErrorId}");

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        devices.PropertyChanged -= OnDeviceChanged;
        icon.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(nint menu, uint flags, nuint itemId, string label);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);
}
