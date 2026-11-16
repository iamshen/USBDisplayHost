using System.Threading.Channels;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Core.Devices;

namespace UsbDisplayHost.Desktop.Services;

// 窗口通知只记录事件；关闭原会话由单一后台消费者完成。
internal sealed class PowerSessionLifetime
{
    private readonly DeviceCoordinator coordinator;
    private readonly DeviceSessionLifetime sessions;
    private readonly DeviceDiscoveryLifetime discovery;
    private readonly IErrorReporter reporter;
    private readonly Channel<byte> signal = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Task worker;
    private sealed record PowerEvent(DeviceKey? SelectedKey, long SelectedGeneration, DeviceKey? OwnerKey, long OwnerGeneration);
    private PowerEvent? suspended, resumed;
    private int stopped;
    private Task<OperationResult<Unit>>? stopping;

    public PowerSessionLifetime(DeviceCoordinator coordinator, DeviceSessionLifetime sessions,
        DeviceDiscoveryLifetime discovery, IErrorReporter reporter)
    {
        this.coordinator = coordinator; this.sessions = sessions;
        this.discovery = discovery; this.reporter = reporter;
        worker = Task.Run(ReadAsync);
    }

    public void Notify(nint kind)
    {
        if (Volatile.Read(ref stopped) != 0 || kind is not (4 or 0x12)) { return; }
        var state = coordinator.Current;
        var owner = sessions.Current;
        var value = new PowerEvent(state.DeviceKey, state.SessionGeneration, owner.DeviceKey, owner.Generation);
        if (kind == 4) { Interlocked.Exchange(ref suspended, value); }
        else { Interlocked.Exchange(ref resumed, value); }
        signal.Writer.TryWrite(0);
    }

    private async Task ReadAsync()
    {
        await foreach (byte _ in signal.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (Volatile.Read(ref stopped) != 0) { break; }
            var sleep = Interlocked.Exchange(ref suspended, null);
            var wake = Interlocked.Exchange(ref resumed, null);
            if (sleep is null && wake is null) { continue; }
            try
            {
                // 唤醒通知也撤销旧会话：不能依赖应用一定收到此前的休眠通知。
                var error = AppError.Create("HostPowerSessionExpired", "主机休眠或唤醒，原设备会话已失效",
                    "等待原资源关闭后重新检测并选择设备，不沿用旧句柄");
                if (sleep is not null) { await ExpireAsync(sleep, error).ConfigureAwait(false); }
                if (wake is not null) { await ExpireAsync(wake, error).ConfigureAwait(false); }
                if (wake is not null && Volatile.Read(ref stopped) == 0 && !sessions.HasRetainedSession && !sessions.IsBusy)
                {
                    discovery.RequestResumeRescan();
                    if (wake.SelectedKey is { } key) { discovery.RequestReconnect(key, wake.OwnerGeneration); }
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { reporter.Report(exception, "HostPowerSessionCleanup"); }
        }
    }
    private async Task ExpireAsync(PowerEvent value, AppError error)
    {
        if (value.SelectedKey is { } key) { coordinator.NotifyDisconnected(key, value.SelectedGeneration, error); }
        if (value.OwnerKey is { } ownedKey)
        { await sessions.CloseDisconnectedAsync(ownedKey, value.OwnerGeneration).ConfigureAwait(false); }
    }
    public Task<OperationResult<Unit>> StopAsync() => stopping ??= StopCoreAsync();

    private async Task<OperationResult<Unit>> StopCoreAsync()
    {
        try
        {
            Interlocked.Exchange(ref stopped, 1);
            signal.Writer.TryComplete();
            await worker;
            return new OperationResult<Unit>.Success(new());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "WindowPowerStop");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "WindowPowerStopUnconfirmed",
                "电源通知停止未确认", "请保留窗口与实际会话所有者并检查诊断"));
        }
    }
}
