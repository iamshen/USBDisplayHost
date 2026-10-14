using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.ComponentModel;
using UsbDisplayHost.Desktop.ViewModels;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Windows.Lifecycle;

namespace UsbDisplayHost.Desktop.Services;

public sealed class TrayService : IDisposable
{
    private readonly HwndSource source;
    private readonly NotificationAreaIcon icon;
    private readonly Action openWindow;
    private readonly Action exit;
    private readonly Action<string> presentError;
    private readonly IErrorReporter errorReporter;
    private readonly ApplicationSettingsService settings;
    private readonly ContextMenu menu;
    private readonly DevicesPageViewModel devices;
    private bool noticeShown;
    private bool disposed;
    private Task<bool>? hiding;

    public TrayService(Action openWindow, Action exit, Action<string> presentError,
        ApplicationSettingsService settings, IErrorReporter errorReporter, DevicesPageViewModel devices)
    {
        this.openWindow = openWindow;
        this.exit = exit;
        this.presentError = presentError;
        this.settings = settings;
        this.errorReporter = errorReporter;
        this.devices = devices;
        source = new HwndSource(new HwndSourceParameters("USB Display — 通知区域")
        {
            Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80
        });
        try
        {
            icon = new NotificationAreaIcon(source.Handle,
                System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Branding", "app.ico"));
        }
        catch { source.Dispose(); throw; }
        source.AddHook(OnMessage);
        menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        AddItem("打开 USB Display", openWindow);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "切换主题", Command = devices.SwitchThemeCommand });
        menu.Items.Add(new MenuItem { Header = "切换副屏", Command = devices.SwitchScreenCommand });
        menu.Items.Add(new MenuItem { Header = "暂停推流", Command = devices.PauseStreamingCommand });
        menu.Items.Add(new MenuItem { Header = "恢复推流", Command = devices.ResumeStreamingCommand });
        menu.Items.Add(new Separator());
        AddItem("退出", () => { openWindow(); exit(); });
        devices.PropertyChanged += OnDevicePropertyChanged;
    }

    public bool IsAvailable => !disposed && icon.IsAvailable;

    public Task<bool> HideWindowAsync(Window window) =>
        hiding is { IsCompleted: false } ? hiding : hiding = HideCoreAsync(window);

    public async Task WaitForPendingNoticeAsync()
    {
        if (hiding is not null) { await hiding; }
    }

    private async Task<bool> HideCoreAsync(Window window)
    {
        if (!IsAvailable) { return false; }
        if (!noticeShown)
        {
            var result = await settings.LoadAsync(CancellationToken.None);
            if (disposed || !icon.IsAvailable) { return false; }
            if (result is OperationResult<ApplicationSettingsReadResult>.Success success)
            {
                if (!success.Value.Settings.HasSeenTrayNotice)
                {
                    if (!icon.ShowFirstNotice()) { return false; }
                    noticeShown = true;
                    if (success.Value.Error is null)
                    {
                        var save = await settings.MarkTrayNoticeAsync(CancellationToken.None);
                        if (save is OperationResult<Unit>.Failure failure) { Present(failure.Error); }
                        else if (save is OperationResult<Unit>.Unknown unknown) { Present(unknown.Error); }
                    }
                    else { Present(success.Value.Error); }
                }
                else { noticeShown = true; }
            }
            else
            {
                if (!icon.ShowFirstNotice()) { return false; }
                noticeShown = true;
                if (result is OperationResult<ApplicationSettingsReadResult>.Failure failure) { Present(failure.Error); }
            }
        }
        if (!IsAvailable) { return false; }
        window.Hide();
        return true;
    }

    private void Present(AppError error) => presentError($"{error.Reason}。{error.Action}。错误编号：{error.ErrorId}");

    private void AddItem(string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { Report(exception); }
        };
        menu.Items.Add(item);
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (disposed || e.PropertyName != nameof(DevicesPageViewModel.StreamingErrorText)
            || string.IsNullOrWhiteSpace(devices.StreamingErrorText)) { return; }
        try
        {
            presentError(devices.StreamingErrorText);
            openWindow();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Report(exception); }
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (disposed) { return 0; }
        try
        {
            if ((uint)message == icon.TaskbarCreatedMessage) { icon.Add(); }
            else if (message == NotificationAreaIcon.CallbackMessage)
            {
                handled = true;
                int notification = (int)((long)lParam & 0xffff);
                if (notification is 0x400 or 0x401 or 0x203) { openWindow(); }
                else if (notification == 0x7b)
                {
                    // 隐藏窗口暂停呈现时，菜单仍按实际所有者重新核对动作门禁。
                    devices.RefreshStreamingActions();
                    icon.ActivateMenuOwner();
                    menu.IsOpen = true;
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Report(exception);
            try { openWindow(); }
            catch (Exception restoreException) when (restoreException is not OutOfMemoryException)
            {
                errorReporter.Report(restoreException, "TrayRestoreWindow");
            }
        }
        return 0;
    }

    private void Report(Exception exception)
    {
        var report = errorReporter.Report(exception, "Tray");
        try { presentError($"托盘操作未完成，请从窗口重试。错误编号：{report.ErrorId}"); }
        catch (Exception presentationException) when (presentationException is not OutOfMemoryException)
        { errorReporter.Report(presentationException, "TrayErrorPresentation"); }
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        devices.PropertyChanged -= OnDevicePropertyChanged;
        menu.IsOpen = false;
        icon.Dispose();
        source.RemoveHook(OnMessage);
        source.Dispose();
    }
}
