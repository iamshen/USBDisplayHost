using System.Windows.Threading;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Desktop.ViewModels;
using UsbDisplayHost.Windows.Usb;

namespace UsbDisplayHost.Desktop.Services;

// 工作区拥有可选发现生命周期；未附加时不创建监视器或执行原生调用。
public sealed class DeviceDiscoveryLifetime(Dispatcher dispatcher, DevicesPageViewModel devices,
    DeviceCoordinator coordinator, DeviceSessionLifetime deviceSessions, IErrorReporter reporter)
{
    private readonly object sync = new();
    private WindowsDeviceDiscoverySession? session;
    private DeviceDiscoveryPresenter? presenter;
    private Task<OperationResult<Unit>>? closing;
    private bool visible;
    private WindowsDeviceDiscovery? discoveryOwner;
    public HidInterfaceIdentityProbe? HidProbe => discoveryOwner?.HidProbe;
    public WindowsDeviceDiscoverySession? Session => session;

    public async Task<OperationResult<Unit>> StartAsync(CancellationToken token)
    {
        if (closing is not null || deviceSessions.HasRetainedSession)
        { return new OperationResult<Unit>.Failure(AppError.Create("DiscoveryBusy", "原会话尚未释放", "断开设备后重新检测")); }
        if (presenter is not null) { await presenter.DisposeAsync(); presenter = null; }
        if (session is not null)
        {
            var closed = await session.CloseAsync();
            if (closed is not OperationResult<Unit>.Success) { return closed; }
            session = null; discoveryOwner = null;
        }
        if (discoveryOwner is not null)
        {
            var closed = await discoveryOwner.CloseAsync();
            if (closed is not OperationResult<Unit>.Success) { return closed; }
            discoveryOwner = null;
        }
        var created = await WindowsDeviceDiscoveryFactory.CreateAsync(Ad102V2DiscoveryConfiguration.ReadInstalledBindings(), reporter, token);
        if (created is not OperationResult<WindowsDeviceDiscovery>.Success discovery)
        { return Ad102V2DeviceSession.Convert<Unit, WindowsDeviceDiscovery>(created); }
        discoveryOwner = discovery.Value;
        return Attach(discovery.Value);
    }

    public OperationResult<Unit> Attach(WindowsDeviceDiscovery value)
    {
        if (!dispatcher.CheckAccess())
        { return new OperationResult<Unit>.Failure(AppError.Create("DiscoveryAttachRequiresUiThread",
            "发现列表需在界面线程附加", "请通过工作区界面线程附加，调用方继续持有发现生命周期")); }
        lock (sync)
        {
            if (value is null || session is not null || closing is not null)
            { return new OperationResult<Unit>.Failure(AppError.Create("DiscoveryWorkspaceOccupied", "工作区发现入口不可接管",
                "请保留现有所有者；附加失败时发现生命周期仍由调用方持有")); }
            var created = WindowsDeviceDiscoverySession.Create(value, reporter, HandleRemoval);
            if (created is not OperationResult<WindowsDeviceDiscoverySession>.Success actual)
            {
                return created is OperationResult<WindowsDeviceDiscoverySession>.Failure failure
                    ? new OperationResult<Unit>.Failure(failure.Error)
                    : new OperationResult<Unit>.Unknown(AppError.Create("DiscoverySessionCreationUnconfirmed",
                        "发现生命周期创建未确认", "请保留调用方发现服务并检查诊断"));
            }
            try
            {
                session = actual.Value;
                presenter = new(session, dispatcher, devices, reporter);
                presenter.SetVisible(visible);
                return new OperationResult<Unit>.Success(new());
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = reporter.Report(exception, "DeviceDiscoveryAttach");
                return new OperationResult<Unit>.Unknown(new(report.ErrorId, "DeviceDiscoveryAttachUnconfirmed",
                    "发现列表附加未完成，工作区已保留发现所有者", "请先确认工作区发现关闭，不能再次接管"));
            }
        }
    }
    private void HandleRemoval(DeviceChange change)
    {
        var current = coordinator.Current;
        if (current.DeviceKey is not { } key || current.Identity is not { } identity
            || current.Connection == ConnectionState.Disconnected) { return; }
        var error = change.InterfacePath is null
            ? AppError.Create("DeviceRemovalUnconfirmed", "设备移除通知无法完整归属，原会话已停止沿用", "请确认原会话收敛后重新检测并选择设备")
            : AppError.Create("DeviceInterfaceRemoved", "活动设备接口已移除，原会话已失效", "请确认原会话收敛后重新检测并选择设备");
        if (coordinator.NotifyInterfaceRemoved(key, current.SessionGeneration, change.InterfacePath, error))
        {
            // 所有者缓存并捕获关闭结果；通知消费不等待驱动完成，重扫不能覆盖原所有者。
            _ = deviceSessions.CloseDisconnectedAsync(key, current.SessionGeneration);
            RequestReconnect(key, current.SessionGeneration);
        }
    }
    public void RequestReconnect(DeviceKey key, long generation)
    {
        if (closing is not null || dispatcher.HasShutdownStarted) { return; }
        dispatcher.BeginInvoke(new Action(() =>
        {
            if (closing is null && coordinator.Current.DeviceKey == key
                && coordinator.Current.Connection is ConnectionState.Disconnected or ConnectionState.Faulted)
            { _ = devices.ReconnectProtocolAsync(key, generation); }
        }));
    }
    public void SetVisible(bool value)
    { devices.SetPreviewWindowVisible(value); lock (sync) { visible = value; presenter?.SetVisible(value); } }
    public bool RequestResumeRescan()
    { lock (sync) { return closing is null && session?.RequestRescan() == true; } }
    public Task<OperationResult<Unit>> CloseAsync()
    { lock (sync) { return closing ??= CloseCoreAsync(); } }

    public async Task<OperationResult<Unit>> SuspendForUpdateAsync()
    {
        // 暂停发现及探测句柄，但保留 ViewModel 生命周期，更新器启动失败后可重新检测。
        if (closing is not null) { return await closing; }
        if (presenter is not null) { await presenter.DisposeAsync(); }
        var result = session is not null ? await session.CloseAsync()
            : discoveryOwner is not null ? await discoveryOwner.CloseAsync() : new OperationResult<Unit>.Success(new());
        if (result is OperationResult<Unit>.Success) { presenter = null; session = null; discoveryOwner = null; }
        return result;
    }
    private async Task<OperationResult<Unit>> CloseCoreAsync()
    {
        try
        {
            await devices.StopAsync();
            if (presenter is not null) { await presenter.DisposeAsync(); }
            if (session is null)
            {
                return discoveryOwner is null ? new OperationResult<Unit>.Success(new()) : await discoveryOwner.CloseAsync();
            }
            var result = await session.CloseAsync();
            if (result is OperationResult<Unit>.Success) { presenter = null; session = null; }
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DeviceDiscoveryWorkspaceClose");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "DiscoveryWorkspaceCloseUnconfirmed",
                "工作区发现关闭未确认，所有者已保留", "请检查诊断，不能重复接管或继续退出"));
        }
    }
}
