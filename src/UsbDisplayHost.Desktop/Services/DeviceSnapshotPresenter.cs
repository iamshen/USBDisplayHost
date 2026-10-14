using System.Windows.Threading;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Desktop.ViewModels;

namespace UsbDisplayHost.Desktop.Services;

// 消费协调器最新槽，不等待界面呈现；隐藏时仅保留一个快照，Dispatcher 最多一个待处理通知。
public sealed class DeviceSnapshotPresenter : IAsyncDisposable
{
    private readonly object syncRoot = new();
    private readonly DeviceCoordinator coordinator;
    private readonly DeviceSessionLifetime sessions;
    private readonly Dispatcher dispatcher;
    private readonly DevicesPageViewModel devices;
    private readonly WorkspaceViewModel workspace;
    private readonly IErrorReporter reporter;
    private readonly ThemePageViewModel themes;
    private readonly DeviceSettingsPageViewModel deviceSettings;
    private readonly ApplicationSettingsPageViewModel applicationSettings;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DeviceSnapshotSubscription? subscription;
    private readonly Task reading;
    private readonly Task readingLifecycle;
    private DeviceSnapshot latest;
    private SessionLifecycleSnapshot latestLifecycle;
    private string? pendingError;
    private bool visible, stopped;
    private DispatcherOperation? pending;
    private Task? stopping;

    public DeviceSnapshotPresenter(DeviceCoordinator coordinator, DeviceSessionLifetime sessions, Dispatcher dispatcher, DevicesPageViewModel devices,
        WorkspaceViewModel workspace, IErrorReporter reporter, ThemePageViewModel themes, DeviceSettingsPageViewModel deviceSettings,
        ApplicationSettingsPageViewModel applicationSettings)
    {
        this.coordinator = coordinator; this.dispatcher = dispatcher; this.devices = devices;
        this.sessions = sessions; latestLifecycle = sessions.Current;
        this.workspace = workspace; this.reporter = reporter; latest = coordinator.Current;
        this.themes = themes;
        this.deviceSettings = deviceSettings;
        this.applicationSettings = applicationSettings;
        devices.Apply(latest);
        devices.ApplyLifecycle(latestLifecycle);
        readingLifecycle = Task.Run(ReadLifecycleAsync);
        var result = coordinator.Subscribe();
        if (result is OperationResult<DeviceSnapshotSubscription>.Success subscribed)
        {
            subscription = subscribed.Value; reading = Task.Run(ReadAsync);
        }
        else
        {
            devices.SubscriptionErrorText = result is OperationResult<DeviceSnapshotSubscription>.Failure failure
                ? $"{failure.Error.Reason}。{failure.Error.Action}。错误编号：{failure.Error.ErrorId}" : "设备状态订阅未完成";
            reading = Task.CompletedTask;
        }
    }

    public void SetVisible(bool value)
    {
        lock (syncRoot)
        {
            if (stopped) { return; }
            visible = value;
            if (visible)
            {
                if (coordinator.Current.Version > latest.Version) { latest = coordinator.Current; }
                if (sessions.Current.Version > latestLifecycle.Version) { latestLifecycle = sessions.Current; }
                Schedule();
            }
        }
    }

    private async Task ReadLifecycleAsync()
    {
        try
        {
            await foreach (var value in sessions.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                lock (syncRoot)
                {
                    if (stopped) { return; }
                    if (value.Version > latestLifecycle.Version) { latestLifecycle = value; Schedule(); }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "SessionLifecycleSubscription");
            lock (syncRoot) { pendingError = $"会话关闭状态更新未完成，请检查诊断。错误编号：{report.ErrorId}"; Schedule(); }
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            await foreach (var snapshot in subscription!.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                lock (syncRoot)
                {
                    if (stopped) { return; }
                    if (snapshot.Version > latest.Version) { latest = snapshot; Schedule(); }
                }
            }
            lock (syncRoot)
            {
                if (stopped || lifetime.IsCancellationRequested) { return; }
            }
            throw new InvalidOperationException("Device snapshot subscription ended before workspace shutdown.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DeviceSnapshotSubscription");
            lock (syncRoot)
            {
                pendingError = $"设备状态更新未完成，请检查诊断。错误编号：{report.ErrorId}";
                Schedule();
            }
        }
    }

    private void Schedule()
    {
        if (!visible || stopped || pending is not null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) { return; }
        try { pending = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Present)); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reporter.Report(exception, "DeviceSnapshotDispatch");
        }
    }

    private void Present()
    {
        DeviceSnapshot snapshot;
        SessionLifecycleSnapshot lifecycle;
        string? error;
        lock (syncRoot)
        {
            pending = null;
            if (!visible || stopped) { return; }
            snapshot = latest; error = pendingError;
            lifecycle = latestLifecycle;
        }
        try
        {
            devices.ApplyLifecycle(lifecycle);
            themes.RefreshDeviceActions();
            deviceSettings.RefreshDeviceActions();
            applicationSettings.RefreshModeAvailability();
            if (devices.Apply(snapshot))
            {
                workspace.StatusText = snapshot.Connection switch
                {
                    ConnectionState.Ready => "设备已就绪", ConnectionState.Opening => "设备连接中",
                    ConnectionState.Recovering => "设备恢复中", ConnectionState.Faulted => "设备连接故障",
                    ConnectionState.Discovered => "已发现设备", _ => "未连接设备"
                };
            }
            if (error is not null) { devices.SubscriptionErrorText = error; }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reporter.Report(exception, "DeviceSnapshotPresentation");
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (syncRoot) { return new(stopping ??= StopCoreAsync()); }
    }
    private async Task StopCoreAsync()
    {
        stopped = true; pending?.Abort(); pending = null;
        await lifetime.CancelAsync().ConfigureAwait(false);
        if (subscription is not null) { await subscription.DisposeAsync().ConfigureAwait(false); }
        await reading.ConfigureAwait(false);
        await readingLifecycle.ConfigureAwait(false);
        lifetime.Dispose();
    }
}
