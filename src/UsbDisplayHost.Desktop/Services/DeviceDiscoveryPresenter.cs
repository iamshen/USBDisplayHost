using System.Windows.Threading;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Desktop.ViewModels;
using UsbDisplayHost.Windows.Usb;

namespace UsbDisplayHost.Desktop.Services;

// 只消费一个最新快照槽；隐藏时停止 Dispatcher 更新，不阻塞发现任务。
public sealed class DeviceDiscoveryPresenter : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly WindowsDeviceDiscoverySession session;
    private readonly Dispatcher dispatcher;
    private readonly DevicesPageViewModel devices;
    private readonly IErrorReporter reporter;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task reading;
    private DeviceDiscoverySnapshot latest;
    private DispatcherOperation? pending;
    private string? error;
    private bool visible, stopped;
    private Task? closing;

    public DeviceDiscoveryPresenter(WindowsDeviceDiscoverySession session, Dispatcher dispatcher,
        DevicesPageViewModel devices, IErrorReporter reporter)
    {
        dispatcher.VerifyAccess();
        this.session = session; this.dispatcher = dispatcher; this.devices = devices; this.reporter = reporter;
        latest = session.Current; devices.AttachDiscovery(session);
        reading = Task.Run(ReadAsync);
    }
    public void SetVisible(bool value)
    {
        lock (sync)
        {
            if (stopped) { return; }
            visible = value;
            if (visible) { latest = session.Current; ScheduleLocked(); }
        }
    }
    private async Task ReadAsync()
    {
        try
        {
            await foreach (var value in session.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                lock (sync)
                {
                    if (stopped) { return; }
                    if (value.Version >= latest.Version) { latest = value; ScheduleLocked(); }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DeviceDiscoveryPresentationRead");
            lock (sync) { error = $"发现列表更新未完成，请检查诊断。错误编号：{report.ErrorId}"; ScheduleLocked(); }
        }
    }
    private void ScheduleLocked()
    {
        if (stopped || !visible || pending is not null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) { return; }
        try { pending = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Present)); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { reporter.Report(exception, "DeviceDiscoveryDispatch"); }
    }
    private void Present()
    {
        DeviceDiscoverySnapshot value;
        string? message;
        lock (sync)
        {
            pending = null;
            if (stopped || !visible) { return; }
            value = latest; message = error;
        }
        try
        {
            devices.ApplyDiscovery(value);
            if (message is not null) { devices.DiscoveryErrorText = message; }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { reporter.Report(exception, "DeviceDiscoveryPresentation"); }
    }
    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            stopped = true; pending?.Abort(); pending = null;
            return new(closing ??= StopCoreAsync());
        }
    }
    private async Task StopCoreAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await reading.ConfigureAwait(false);
        lifetime.Dispose();
    }
}
