using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Windows.Windowing;

namespace UsbDisplayHost.Desktop.Services;

public sealed class WindowWorkAreaLifetime
{
    private readonly Window window;
    private readonly IErrorReporter reporter;
    private readonly Action<string> showError;
    private HwndSource? source;
    private bool pending;
    private bool constrainPending;
    private bool closed;

    public WindowWorkAreaLifetime(Window window, IErrorReporter reporter, Action<string> showError)
    {
        this.window = window;
        this.reporter = reporter;
        this.showError = showError;
        window.SourceInitialized += OnSourceInitialized;
        window.Loaded += OnLoaded;
        window.LocationChanged += OnLocationChanged;
        window.StateChanged += OnStateChanged;
        window.IsVisibleChanged += OnVisibilityChanged;
        window.Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source?.AddHook(OnMessage);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { Report(exception); }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Queue(true);
    private void OnLocationChanged(object? sender, EventArgs e) => Queue(false);
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (window.IsVisible) { Queue(true); }
    }
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (window.WindowState == WindowState.Normal) { Queue(true); }
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // DPI、显示器拓扑或工作区变化后，等待 WPF 完成本轮原生消息处理。
        if (message is 0x02E0 or 0x007E or 0x001A) { Queue(true); }
        return 0;
    }

    private void Queue(bool constrain)
    {
        if (closed || !window.IsLoaded || window.Dispatcher.HasShutdownStarted) { return; }
        constrainPending |= constrain;
        if (pending) { return; }
        pending = true;
        try { window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Apply)); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            pending = false;
            constrainPending = false;
            Report(exception);
        }
    }

    private void Apply()
    {
        bool constrain = constrainPending;
        constrainPending = false;
        pending = false;
        if (closed || window.Dispatcher.HasShutdownStarted || window.WindowState != WindowState.Normal) { return; }
        try
        {
            nint handle = new WindowInteropHelper(window).Handle;
            var area = WindowWorkAreaAdapter.Read(handle);
            window.MinWidth = area.MinimumWidthDip;
            window.MinHeight = area.MinimumHeightDip;
            if (constrain) { WindowWorkAreaAdapter.Constrain(handle, area); }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { Report(exception); }
    }

    private void Report(Exception exception)
    {
        var error = reporter.Report(exception, "WindowWorkArea");
        showError($"窗口工作区调整未完成，请手动移动或缩小窗口。错误编号：{error.ErrorId}");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        closed = true;
        window.SourceInitialized -= OnSourceInitialized;
        window.Loaded -= OnLoaded;
        window.LocationChanged -= OnLocationChanged;
        window.StateChanged -= OnStateChanged;
        window.IsVisibleChanged -= OnVisibilityChanged;
        window.Closed -= OnClosed;
        try { source?.RemoveHook(OnMessage); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { reporter.Report(exception, "WindowWorkAreaUnsubscribe"); }
        source = null;
    }
}
