using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Desktop.Services;
using UsbDisplayHost.Desktop.ViewModels;
using WinRT.Interop;

namespace UsbDisplayHost.WinUI.Services;

internal sealed class WinUiWindowLifetime : IWindowExitHost
{
    private readonly MainWindow window;
    private readonly AppWindow appWindow;
    private readonly ThemeWorkspaceLifetime workspace;
    private readonly WinUiDispatcher dispatcher;
    private readonly IErrorReporter reporter;
    private readonly nint handle;
    private readonly WindowExitWorkflow exit;
    private readonly WindowMessageHook messages;
    private readonly WinUiTray? tray;
    private readonly PowerSessionLifetime? power;
    private bool allowClose;
    private bool visible;

    public WinUiWindowLifetime(MainWindow window, ThemeWorkspaceLifetime workspace,
        ApplicationSettingsService settings, DevicesPageViewModel devices, PowerSessionLifetime? power,
        WinUiDispatcher dispatcher, IErrorReporter reporter)
    {
        this.window = window;
        appWindow = window.AppWindow;
        this.workspace = workspace;
        this.dispatcher = dispatcher;
        this.reporter = reporter;
        this.power = power;
        handle = WindowNative.GetWindowHandle(window);
        exit = new WindowExitWorkflow(workspace, reporter, TimeSpan.FromSeconds(2), TimeProvider.System);
        messages = new WindowMessageHook(handle, HandleWindowMessage);
        try
        {
            tray = new WinUiTray(handle, settings, devices, window.RestoreAndActivate,
                () => _ = exit.RequestExitAsync(this), ShowError, reporter);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "TrayInitialization");
            ShowError($"托盘不可用，关闭窗口将执行退出。错误编号：{report.ErrorId}");
        }

        appWindow.Closing += OnClosing;
        appWindow.Changed += OnWindowChanged;
        window.Closed += OnClosed;
    }

    public bool HasTray => tray is not null;
    public bool IsClosed { get; private set; }

    public void Show() => window.RestoreAndActivate();

    public void ShowError(string message) => window.ShowNotice(message, failed: true);

    public void SetEnabled(bool enabled)
    {
        if (!IsClosed) { EnableWindow(handle, enabled); }
    }

    public void FinishExit()
    {
        allowClose = true;
        try { window.Close(); }
        catch
        {
            allowClose = false;
            throw;
        }
    }

    public Task<bool> DeferCloseAsync()
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return dispatcher.TryEnqueue(() => ready.TrySetResult(!IsClosed), background: true) ? ready.Task : Task.FromResult(false);
    }

    public Task<bool> TryHideInTrayAsync() => tray is { IsAvailable: true }
        ? tray.HideWindowAsync(appWindow.Hide)
        : Task.FromResult(false);

    public Task WaitForPendingTrayNoticeAsync() => tray?.WaitForPendingNoticeAsync() ?? Task.CompletedTask;

    public Task<WindowExitChoice> ShowDecisionAsync(string message, WindowExitPrompt prompt, CancellationToken cancellationToken) =>
        window.ShowExitDecisionAsync(message, prompt, cancellationToken);

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowClose || exit.IsSystemExiting) { return; }
        args.Cancel = true;
        _ = exit.HandleClosingAsync(this);
    }

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidVisibilityChange || args.DidPresenterChange) { UpdateVisibility(); }
    }

    public void UpdateVisibility()
    {
        if (IsClosed) { return; }
        bool next = appWindow.IsVisible
            && appWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        if (visible == next) { return; }
        visible = next;
        window.SetWorkspaceVisible(next);
        workspace.Themes.SetWindowVisible(next);
        workspace.SetWindowVisible(next);
    }

    private nint? HandleWindowMessage(uint message, nint wParam, nint lParam)
    {
        const uint Size = 0x0005;
        const uint EndSession = 0x0016;
        const uint PowerBroadcast = 0x0218;
        try
        {
            switch (message)
            {
                case Size:
                    dispatcher.TryEnqueue(UpdateVisibility, background: true);
                    break;
                case EndSession when wParam != 0:
                    exit.NotifySystemExit();
                    break;
                case PowerBroadcast:
                    power?.Notify(wParam);
                    break;
            }
            return tray?.HandleMessage(message, lParam);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reporter.Report(exception, "WindowMessage");
            return null;
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        IsClosed = true;
        appWindow.Closing -= OnClosing;
        appWindow.Changed -= OnWindowChanged;
        window.Closed -= OnClosed;
        Dispose(tray, "TrayDispose");
        Dispose(messages, "WindowMessagesDispose");
        Application.Current.Exit();
    }

    private void Dispose(IDisposable? resource, string stage)
    {
        try { resource?.Dispose(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { reporter.Report(exception, stage); }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool enabled);
}
