using System.Windows;
using System.Windows.Interop;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;

namespace UsbDisplayHost.Desktop.Services;

public sealed class WindowPowerLifetime
{
    private readonly Window window;
    private readonly IErrorReporter reporter;
    private readonly PowerSessionLifetime power;
    private HwndSource? source;
    private Task<OperationResult<Unit>>? stopping;

    public WindowPowerLifetime(Window window, DeviceCoordinator coordinator, DeviceSessionLifetime sessions,
        DeviceDiscoveryLifetime discovery, IErrorReporter reporter)
    {
        this.window = window;
        this.reporter = reporter;
        power = new(coordinator, sessions, discovery, reporter);
        window.SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (stopping is not null) { return; }
        try
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            if (source is null) { throw new InvalidOperationException("电源通知窗口源未提供"); }
            source.AddHook(OnMessage);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { reporter.Report(exception, "WindowPowerSubscription"); }
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x218 && stopping is null) { power.Notify(wParam); }
        return 0;
    }

    public Task<OperationResult<Unit>> StopAsync()
    {
        if (!window.Dispatcher.CheckAccess())
        {
            return Task.FromResult<OperationResult<Unit>>(new OperationResult<Unit>.Failure(AppError.Create(
                "PowerSubscriptionStopRequiresUi", "电源通知需在窗口线程停止", "请通过工作区关闭链停止订阅")));
        }
        return stopping ??= StopCoreAsync();
    }

    private async Task<OperationResult<Unit>> StopCoreAsync()
    {
        try
        {
            window.SourceInitialized -= OnSourceInitialized;
            source?.RemoveHook(OnMessage);
            var result = await power.StopAsync();
            source = null;
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "WindowPowerStop");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "WindowPowerStopUnconfirmed",
                "电源通知停止未确认", "请保留窗口与实际会话所有者并检查诊断"));
        }
    }
}
