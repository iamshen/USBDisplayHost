using System.Collections.Immutable;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Windows.Usb;

public sealed record DeviceIdentityProbeRoute(Guid InterfaceClass, IDeviceInterfaceIdentityProbe Probe);

// 创建成功后拥有各探测器；同一探测器的多个路由只关闭一次。
public sealed class CompositeDeviceInterfaceIdentityProbe : IDeviceInterfaceIdentityProbe
{
    private static readonly object OwnersSync = new();
    private static readonly HashSet<CompositeDeviceInterfaceIdentityProbe> Owners = [];
    private readonly ImmutableArray<DeviceIdentityProbeRoute> routes;
    private readonly ImmutableArray<IDeviceInterfaceIdentityProbe> probes;
    private readonly IErrorReporter reporter;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object closeSync = new();
    private IDeviceInterfaceIdentityProbe? current;
    private AppError? pending;
    private AppError? closeUnconfirmed;
    private Task<OperationResult<Unit>>? closing;
    private int stopping;

    private CompositeDeviceInterfaceIdentityProbe(ImmutableArray<DeviceIdentityProbeRoute> routes, IErrorReporter reporter)
    {
        this.routes = routes;
        this.reporter = reporter;
        probes = routes.Select(route => route.Probe).Distinct<IDeviceInterfaceIdentityProbe>(ReferenceEqualityComparer.Instance).ToImmutableArray();
    }

    public static OperationResult<CompositeDeviceInterfaceIdentityProbe> Create(
        ImmutableArray<DeviceIdentityProbeRoute> routes, IErrorReporter reporter)
    {
        if (reporter is null || routes.IsDefaultOrEmpty || routes.Length > 4
            || routes.Any(route => route is null || route.InterfaceClass == Guid.Empty || route.Probe is null)
            || routes.Select(route => route.InterfaceClass).Distinct().Count() != routes.Length)
        {
            return new OperationResult<CompositeDeviceInterfaceIdentityProbe>.Failure(AppError.Create("InvalidIdentityProbeRoutes",
                "身份探测路由缺失、重复或超出限额", "请为每个实际接口 GUID 指定一个探测器"));
        }
        lock (OwnersSync)
        {
            if (Owners.Count >= 8 || Owners.Any(owner => owner.probes.Any(probe => routes.Any(route => ReferenceEquals(route.Probe, probe)))))
            {
                return new OperationResult<CompositeDeviceInterfaceIdentityProbe>.Failure(AppError.Create("IdentityProbeAlreadyOwned",
                    "探测器已有组合所有者或达到限额", "请确认现有所有者关闭后再创建；创建失败时探测器仍由调用方持有"));
            }
            var owner = new CompositeDeviceInterfaceIdentityProbe(routes, reporter);
            Owners.Add(owner);
            return new OperationResult<CompositeDeviceInterfaceIdentityProbe>.Success(owner);
        }
    }

    public async Task<OperationResult<DeviceIdentity?>> ProbeAsync(DeviceInterfacePath path, CancellationToken token)
    {
        bool entered = false;
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false); entered = true;
            if (pending is { } error) { return new OperationResult<DeviceIdentity?>.Unknown(error); }
            if (Volatile.Read(ref stopping) != 0 || current is not null)
            {
                return new OperationResult<DeviceIdentity?>.Failure(AppError.Create("IdentityProbeUnavailable",
                    "组合探测器已停止或上一项尚未收敛", "请先确认当前探测收敛，不能覆盖现有所有者"));
            }
            var route = routes.FirstOrDefault(route => route.InterfaceClass == path.InterfaceClass);
            if (route is null)
            {
                return new OperationResult<DeviceIdentity?>.Failure(AppError.Create("IdentityProbeRouteMissing",
                    "接口 GUID 没有明确的身份探测路由", "请核实接口清单，不通过路径文字推断协议"));
            }
            current = route.Probe;
            var result = await current.ProbeAsync(path, token).ConfigureAwait(false);
            if (result is OperationResult<DeviceIdentity?>.Unknown unknown) { pending = unknown.Error; }
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new OperationResult<DeviceIdentity?>.Cancelled(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new OperationResult<DeviceIdentity?>.Failure(Report(exception, "CompositeIdentityProbe",
                "身份探测未完成", "请先确认探测资源收敛，再检查诊断"));
        }
        finally { if (entered) { gate.Release(); } }
    }

    public async Task<OperationResult<Unit>> CancelAndDrainAsync(CancellationToken token)
    {
        bool entered = false;
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false); entered = true;
            if (closeUnconfirmed is { } unknown) { return new OperationResult<Unit>.Unknown(unknown); }
            if (current is null) { return new OperationResult<Unit>.Success(new()); }
            var result = await DrainAsync(current).ConfigureAwait(false);
            if (result is OperationResult<Unit>.Success) { current = null; }
            else { pending = GetDrainError(result); }
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new OperationResult<Unit>.Cancelled(); }
        finally { if (entered) { gate.Release(); } }
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
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // 先全部收敛，再释放任何探测器，避免未知完成时关闭另一个协议入口。
            foreach (var probe in probes)
            {
                var result = await DrainAsync(probe).ConfigureAwait(false);
                if (result is not OperationResult<Unit>.Success)
                {
                    pending = GetDrainError(result);
                    closeUnconfirmed = pending;
                    return new OperationResult<Unit>.Unknown(pending);
                }
            }
            current = null;
            foreach (var probe in probes) { await probe.DisposeAsync().ConfigureAwait(false); }
            lock (OwnersSync) { Owners.Remove(this); }
            return new OperationResult<Unit>.Success(new());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            pending = Report(exception, "CompositeIdentityProbeClose", "组合探测器关闭未确认，所有者已保留",
                "请保留诊断，不能重复关闭或重新接管探测器");
            closeUnconfirmed = pending;
            return new OperationResult<Unit>.Unknown(pending);
        }
        finally { gate.Release(); }
    }

    private async Task<OperationResult<Unit>> DrainAsync(IDeviceInterfaceIdentityProbe probe)
    {
        try { return await probe.CancelAndDrainAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new OperationResult<Unit>.Unknown(Report(exception, "CompositeIdentityProbeDrain",
                "身份探测资源收敛未确认，所有者已保留", "请停止后续探测并核实原生资源"));
        }
    }

    private static AppError GetDrainError(OperationResult<Unit> result) => result switch
    {
        OperationResult<Unit>.Failure failure => failure.Error,
        OperationResult<Unit>.Unknown unknown => unknown.Error,
        _ => AppError.Create("IdentityProbeDrainUnconfirmed", "探测取消未证明资源已经收敛", "请保留所有者并停止后续探测")
    };

    private AppError Report(Exception exception, string operation, string reason, string action)
    {
        var report = reporter.Report(exception, operation);
        return new(report.ErrorId, operation, reason, action);
    }

    public async ValueTask DisposeAsync()
    {
        if (await CloseAsync().ConfigureAwait(false) is not OperationResult<Unit>.Success)
        {
            throw new InvalidOperationException("Composite identity probe close is unconfirmed; ownership is retained.");
        }
    }
}
