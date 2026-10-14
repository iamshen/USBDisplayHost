using System.Collections.Immutable;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Windows.Usb;

public sealed record WindowsDiscoveryConfiguration(Guid? HidInterfaceClass,
    ImmutableArray<HidIdentityCompatibility> HidCompatibility,
    ImmutableArray<WinUsbIdentityCompatibility> WinUsbCompatibility);

// 显式创建入口；没有研发 VID/PID、接口 GUID 或自动启动默认值。
public static class WindowsDeviceDiscoveryFactory
{
    public static async Task<OperationResult<WindowsDeviceDiscovery>> CreateAsync(WindowsDiscoveryConfiguration configuration,
        IErrorReporter reporter, CancellationToken token)
    {
        if (configuration is null || reporter is null || configuration.HidCompatibility.IsDefault
            || configuration.WinUsbCompatibility.IsDefault
            || configuration.HidCompatibility.IsEmpty && configuration.WinUsbCompatibility.IsEmpty
            || configuration.HidCompatibility.IsEmpty != (configuration.HidInterfaceClass is null)
            || configuration.HidInterfaceClass == Guid.Empty
            || configuration.WinUsbCompatibility.Any(item => item is null || item.InterfaceClass == Guid.Empty))
        {
            return Failure("InvalidDiscoveryConfiguration", "设备发现接口和兼容清单不完整",
                "请提供实际 HID 接口 GUID 和／或 WinUSB 兼容记录；未使用的清单应明确为空");
        }
        var classes = configuration.WinUsbCompatibility.Select(item => item.InterfaceClass)
            .Concat(configuration.HidInterfaceClass is { } hidClass ? [hidClass] : Array.Empty<Guid>()).Distinct().ToImmutableArray();
        if (classes.Length > 4 || configuration.HidInterfaceClass is { } hid
            && configuration.WinUsbCompatibility.Any(item => item.InterfaceClass == hid))
        {
            return Failure("DiscoveryRouteConflict", "接口类数量超限或 HID 与 WinUSB 路由冲突",
                "请为每个实际接口 GUID 选择唯一身份协议");
        }
        if (token.IsCancellationRequested) { return new OperationResult<WindowsDeviceDiscovery>.Cancelled(); }

        var owned = new List<IDeviceInterfaceIdentityProbe>(2);
        CompositeDeviceInterfaceIdentityProbe? composite = null;
        WindowsDeviceInterfaceMonitor? monitor = null;
        HidInterfaceIdentityProbe? hidProbe = null;
        OperationResult<WindowsDeviceDiscovery> result;
        try
        {
            result = await CreateCoreAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { result = new OperationResult<WindowsDeviceDiscovery>.Cancelled(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DeviceDiscoveryCreation");
            result = new OperationResult<WindowsDeviceDiscovery>.Failure(new(report.ErrorId, "DeviceDiscoveryCreationFailed",
                "设备发现创建未完成", "请核实兼容清单和诊断后重试"));
        }
        if (result is OperationResult<WindowsDeviceDiscovery>.Success) { return result; }

        // 失败时不丢弃注销／释放任务；底层未知所有者继续强引用保留预约。
        try
        {
            if (monitor is not null)
            {
                var monitorClose = await monitor.CloseAsync().ConfigureAwait(false);
                if (monitorClose is not OperationResult<Unit>.Success) { return CleanupUnknown(monitorClose); }
            }
            if (composite is not null)
            {
                var closed = await composite.CloseAsync().ConfigureAwait(false);
                if (closed is not OperationResult<Unit>.Success) { return CleanupUnknown(closed); }
            }
            else
            {
                foreach (var probe in owned)
                {
                    var drained = await probe.CancelAndDrainAsync(CancellationToken.None).ConfigureAwait(false);
                    if (drained is not OperationResult<Unit>.Success) { return CleanupUnknown(drained); }
                    await probe.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DeviceDiscoveryCreationCleanup");
            return new OperationResult<WindowsDeviceDiscovery>.Unknown(new(report.ErrorId, "DiscoveryCreationCleanupUnconfirmed",
                "发现服务创建失败后的关闭未确认，原所有者已保留", "请保留诊断，不能用新实例替代未知所有者"));
        }
        return result;

        async Task<OperationResult<WindowsDeviceDiscovery>> CreateCoreAsync()
        {
            var routes = ImmutableArray.CreateBuilder<DeviceIdentityProbeRoute>();
            if (configuration.HidInterfaceClass is { } interfaceClass)
            {
                var created = HidInterfaceIdentityProbe.Create(configuration.HidCompatibility, reporter);
                if (created is not OperationResult<HidInterfaceIdentityProbe>.Success actual) { return Convert(created); }
                hidProbe = actual.Value;
                owned.Add(actual.Value); routes.Add(new(interfaceClass, actual.Value));
            }
            if (!configuration.WinUsbCompatibility.IsEmpty)
            {
                var created = WinUsbInterfaceIdentityProbe.Create(configuration.WinUsbCompatibility, reporter);
                if (created is not OperationResult<WinUsbInterfaceIdentityProbe>.Success actual) { return Convert(created); }
                owned.Add(actual.Value);
                foreach (var winUsbClass in configuration.WinUsbCompatibility.Select(item => item.InterfaceClass).Distinct())
                { routes.Add(new(winUsbClass, actual.Value)); }
            }
            token.ThrowIfCancellationRequested();
            var combined = CompositeDeviceInterfaceIdentityProbe.Create(routes.ToImmutable(), reporter);
            if (combined is not OperationResult<CompositeDeviceInterfaceIdentityProbe>.Success combinedOwner) { return Convert(combined); }
            composite = combinedOwner.Value; owned.Clear();
            var registered = await WindowsDeviceInterfaceMonitor.CreateAsync(classes, reporter, token).ConfigureAwait(false);
            if (registered is not OperationResult<WindowsDeviceInterfaceMonitor>.Success notificationOwner) { return Convert(registered); }
            monitor = notificationOwner.Value;
            token.ThrowIfCancellationRequested();
            return new OperationResult<WindowsDeviceDiscovery>.Success(new(monitor, composite, reporter) { HidProbe = hidProbe });
        }
    }

    private static OperationResult<WindowsDeviceDiscovery> Convert<T>(OperationResult<T> result) => result switch
    {
        OperationResult<T>.Failure failure => new OperationResult<WindowsDeviceDiscovery>.Failure(failure.Error),
        OperationResult<T>.Unknown unknown => new OperationResult<WindowsDeviceDiscovery>.Unknown(unknown.Error),
        _ => new OperationResult<WindowsDeviceDiscovery>.Cancelled()
    };
    private static OperationResult<WindowsDeviceDiscovery> CleanupUnknown(OperationResult<Unit> result) =>
        new OperationResult<WindowsDeviceDiscovery>.Unknown(result switch
        {
            OperationResult<Unit>.Failure failure => failure.Error,
            OperationResult<Unit>.Unknown unknown => unknown.Error,
            _ => AppError.Create("DiscoveryCreationCleanupUnconfirmed", "取消未证明发现资源已经关闭", "请保留原所有者并停止重新创建")
        });
    private static OperationResult<WindowsDeviceDiscovery> Failure(string code, string reason, string action) =>
        new OperationResult<WindowsDeviceDiscovery>.Failure(AppError.Create(code, reason, action));
}
