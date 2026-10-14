using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Windows.Usb;

public interface IDeviceInterfaceIdentityProbe : IAsyncDisposable
{
    // null 只能表示已经明确核实为非目标接口；访问失败或身份未确认必须返回 Failure／Unknown。
    Task<OperationResult<DeviceIdentity?>> ProbeAsync(DeviceInterfacePath path, CancellationToken token);
    // 实际原生完成前不能释放探测句柄或缓冲；取消不等同于已经收敛。
    Task<OperationResult<Unit>> CancelAndDrainAsync(CancellationToken token);
}

// 拥有监视器与探测器；逐项探测，不并发打开接口，不把路径编码当成真实身份。
public sealed class WindowsDeviceDiscovery(WindowsDeviceInterfaceMonitor monitor, IDeviceInterfaceIdentityProbe probe,
    IErrorReporter reporter) : IDeviceDiscovery
{
    public HidInterfaceIdentityProbe? HidProbe { get; internal init; }
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object closeSync = new();
    private Task<OperationResult<Unit>>? closing;
    private AppError? unconfirmedProbe;
    private int stopping, watching;
    internal long ChangeEpoch => monitor.ChangeEpoch;

    public async IAsyncEnumerable<DeviceChange> WatchAsync([EnumeratorCancellation] CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        if (Interlocked.CompareExchange(ref watching, 1, 0) != 0) { throw new InvalidOperationException("Device discovery supports one active consumer."); }
        try
        {
            await foreach (var change in monitor.WatchAsync(linked.Token).ConfigureAwait(false)) { yield return change; }
        }
        finally { Volatile.Write(ref watching, 0); }
    }

    public async Task<OperationResult<ImmutableArray<DeviceIdentity>>> EnumerateAsync(CancellationToken token)
    {
        bool entered = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        try
        {
            await gate.WaitAsync(linked.Token).ConfigureAwait(false); entered = true;
            if (unconfirmedProbe is { } unknown) { return new OperationResult<ImmutableArray<DeviceIdentity>>.Unknown(unknown); }
            if (Volatile.Read(ref stopping) != 0 || Volatile.Read(ref watching) == 0)
            {
                return Reject("DeviceDiscoverySubscriptionMissing", "设备发现已停止或尚未订阅变化", "请先建立变化订阅，再请求初始枚举");
            }
            var paths = await monitor.EnumerateAsync(linked.Token).ConfigureAwait(false);
            if (paths is not OperationResult<DeviceInterfaceInventory>.Success inventory) { return ConvertFailure(paths); }
            if (inventory.Value.ChangedDuringEnumeration || monitor.ChangeEpoch != inventory.Value.ChangeEpoch) { return Changed(); }
            long epoch = inventory.Value.ChangeEpoch;
            var found = ImmutableArray.CreateBuilder<DeviceIdentity>();
            foreach (var path in inventory.Value.Interfaces)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (monitor.ChangeEpoch != epoch || Volatile.Read(ref watching) == 0) { return Changed(); }
                OperationResult<DeviceIdentity?> identity;
                try { identity = await probe.ProbeAsync(path, linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { identity = new OperationResult<DeviceIdentity?>.Cancelled(); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    var report = reporter.Report(exception, "DeviceIdentityProbe");
                    identity = new OperationResult<DeviceIdentity?>.Failure(new(report.ErrorId, "IdentityProbeFailed",
                        "设备身份探测失败，未返回部分发现结果", "请核实接口、权限与兼容记录后重扫"));
                }
                var drained = await DrainProbeAsync().ConfigureAwait(false);
                if (drained is not OperationResult<Unit>.Success)
                {
                    unconfirmedProbe = ErrorFromDrain(drained);
                    return new OperationResult<ImmutableArray<DeviceIdentity>>.Unknown(unconfirmedProbe);
                }
                if (identity is OperationResult<DeviceIdentity?>.Unknown pending)
                {
                    unconfirmedProbe = pending.Error;
                    return new OperationResult<ImmutableArray<DeviceIdentity>>.Unknown(pending.Error);
                }
                if (identity is not OperationResult<DeviceIdentity?>.Success verified) { return ConvertFailure(identity); }
                if (monitor.ChangeEpoch != epoch || Volatile.Read(ref stopping) != 0 || Volatile.Read(ref watching) == 0) { return Changed(); }
                if (verified.Value is not { } value) { continue; }
                if (value.VendorId == 0 || !StringComparer.OrdinalIgnoreCase.Equals(value.InterfacePath, path.Path)
                    || value.InterfacePath.Length > 32767 || value.ContainerId == Guid.Empty
                    || value.Location is { Length: > 1024 }
                    || DeviceKey.FromSerialNumber(value.VendorId, value.SerialNumber) is not OperationResult<DeviceKey>.Success)
                {
                    return Reject("InvalidProbedIdentity", "探测身份字段或接口归属无效", "请核实描述符与真实序列号，不从接口路径推断身份");
                }
                found.Add(value);
            }
            linked.Token.ThrowIfCancellationRequested();
            if (monitor.ChangeEpoch != epoch || Volatile.Read(ref watching) == 0 || Volatile.Read(ref stopping) != 0) { return Changed(); }
            // 不在此自动选择设备；重复序列号及同容器接口归属由明确选择解析器再次核实。
            return new OperationResult<ImmutableArray<DeviceIdentity>>.Success(found.ToImmutable());
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { return new OperationResult<ImmutableArray<DeviceIdentity>>.Cancelled(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DeviceDiscovery");
            return new OperationResult<ImmutableArray<DeviceIdentity>>.Failure(new(report.ErrorId, "DeviceDiscoveryFailed",
                "设备发现未完成，未返回部分设备列表", "请保留当前选择并检查诊断后重新检测"));
        }
        finally { if (entered) { gate.Release(); } }
    }

    private async Task<OperationResult<Unit>> DrainProbeAsync()
    {
        try { return await probe.CancelAndDrainAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DeviceIdentityProbeDrain");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "IdentityProbeDrainUnconfirmed",
                "设备身份探测收敛未确认，所有者已保留", "请停止连接并核实原生资源，不能重复探测"));
        }
    }

    public Task<OperationResult<Unit>> CloseAsync()
    {
        lock (closeSync)
        {
            Volatile.Write(ref stopping, 1);
            return closing ??= CloseCoreAsync();
        }
    }
    private async Task<OperationResult<Unit>> CloseCoreAsync()
    {
        bool entered = false;
        try
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await gate.WaitAsync().ConfigureAwait(false); entered = true;
            var drained = await DrainProbeAsync().ConfigureAwait(false);
            if (drained is not OperationResult<Unit>.Success) { return new OperationResult<Unit>.Unknown(ErrorFromDrain(drained)); }
            await probe.DisposeAsync().ConfigureAwait(false);
            var closed = await monitor.CloseAsync().ConfigureAwait(false);
            if (closed is not OperationResult<Unit>.Success) { return closed; }
            return new OperationResult<Unit>.Success(new());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DeviceDiscoveryClose");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "DiscoveryCloseUnconfirmed",
                "设备发现资源关闭未确认，所有者已保留", "请保留诊断，不能重复创建发现服务"));
        }
        finally { if (entered) { gate.Release(); } }
    }
    public async ValueTask DisposeAsync()
    {
        if (await CloseAsync().ConfigureAwait(false) is not OperationResult<Unit>.Success)
        {
            throw new InvalidOperationException("Device discovery resources cannot be released before confirmed close.");
        }
    }
    private static AppError ErrorFromDrain(OperationResult<Unit> result) => result switch
    {
        OperationResult<Unit>.Failure failure => failure.Error,
        OperationResult<Unit>.Unknown unknown => unknown.Error,
        _ => AppError.Create("IdentityProbeDrainUnconfirmed", "身份探测取消未证明资源已收敛", "请保留探测所有者并停止连接")
    };
    private static OperationResult<ImmutableArray<DeviceIdentity>> ConvertFailure<T>(OperationResult<T> result) => result switch
    {
        OperationResult<T>.Failure failure => new OperationResult<ImmutableArray<DeviceIdentity>>.Failure(failure.Error),
        OperationResult<T>.Unknown unknown => new OperationResult<ImmutableArray<DeviceIdentity>>.Unknown(unknown.Error),
        _ => new OperationResult<ImmutableArray<DeviceIdentity>>.Cancelled()
    };
    private static OperationResult<ImmutableArray<DeviceIdentity>> Changed() => Reject("DiscoveryChanged",
        "枚举或探测期间接口发生变化，旧身份结果未发布", "请等待当前重扫并重新选择设备");
    private static OperationResult<ImmutableArray<DeviceIdentity>> Reject(string code, string reason, string action) =>
        new OperationResult<ImmutableArray<DeviceIdentity>>.Failure(AppError.Create(code, reason, action));
}
