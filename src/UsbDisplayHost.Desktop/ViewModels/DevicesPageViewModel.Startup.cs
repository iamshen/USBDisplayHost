using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;

namespace UsbDisplayHost.Desktop.ViewModels;

public sealed partial class DevicesPageViewModel
{
    private Task? startupProtocol;

    public Task InitializeProtocolAsync(ApplicationSettingsPageViewModel settings) =>
        startupProtocol ??= InitializeProtocolCoreAsync(settings);

    private async Task InitializeProtocolCoreAsync(ApplicationSettingsPageViewModel settings)
    {
        try
        {
            await settings.InitializeAsync();
            if (stopping || beginDiscovery is null || protocol is null || IsProtocolBusy) { return; }
            bool switchAtStartup = settings.IsReady && settings.ErrorText.Length == 0 && !settings.HasUnconfirmedSave
                && settings.SavedSettings.SwitchToThemeOnStartup;
            await RunProtocolAsync(async () =>
            {
                var started = await beginDiscovery(ProtocolToken);
                if (started is not OperationResult<Unit>.Success || !switchAtStartup) { return started; }
                var catalog = discovery;
                if (catalog is null) { return new OperationResult<Unit>.Cancelled(); }
                long began = TimeProvider.System.GetTimestamp();
                while (catalog.Current.Version == 0 || catalog.Current.IsScanning)
                {
                    if (TimeProvider.System.GetElapsedTime(began) >= TimeSpan.FromSeconds(8))
                    { return StartupFailure("StartupDiscoveryTimeout", "启动检测未完成，请稍后手动检测"); }
                    await Task.Delay(100, ProtocolToken);
                }
                var inventory = catalog.Current;
                if (!inventory.CanSelect || inventory.Interfaces.IsEmpty)
                { return StartupFailure("StartupDeviceUnavailable", "启动时未发现可连接设备"); }
                var keys = inventory.Interfaces.Select(identity => DeviceKey.FromSerialNumber(identity.VendorId, identity.SerialNumber)).ToArray();
                if (keys.Any(result => result is not OperationResult<DeviceKey>.Success))
                { return StartupFailure("StartupIdentityInvalid", "启动设备身份不完整，请手动检测并选择"); }
                var unique = keys.Cast<OperationResult<DeviceKey>.Success>().Select(result => result.Value).Distinct().ToArray();
                if (unique.Length != 1)
                { return StartupFailure("StartupSelectionRequired", "检测到多台设备，请手动选择，不自动切换"); }
                var selected = catalog.Select(inventory.Version, unique[0]);
                if (selected is not OperationResult<DeviceSelectionAdmission>.Success admission)
                { return StartupFailure("StartupSelectionAmbiguous", "设备接口存在歧义，请手动检测并选择"); }
                var accepted = await coordinator.SelectAsync(admission.Value, ProtocolToken);
                if (accepted is not OperationResult<DeviceSnapshot>.Success) { return StartupConvert(accepted); }
                var connected = await protocol.ConnectAsync(admission.Value, ProtocolToken);
                if (connected is not OperationResult<Unit>.Success) { return connected; }
                var before = coordinator.Current;
                if (before.Mode == DeviceMode.Theme) { return connected; }
                // 启动策略只执行一次；拔插与唤醒恢复不走此入口，也不自动点亮 Off 设备。
                if (before.Power != PowerState.On || before.Capabilities is not { IsProtocolMatched: true } caps
                    || !caps.Commands.Contains(DeviceCommand.SwitchMode))
                { return StartupFailure("StartupModeUnavailable", "当前电源或能力不允许启动切主题，已保留设备模式"); }
                var confirmed = confirmModeSwitch is null ? new OperationResult<Unit>.Cancelled()
                    : await confirmModeSwitch(ProtocolToken);
                if (confirmed is not OperationResult<Unit>.Success) { return confirmed; }
                if (before.DeviceKey != coordinator.Current.DeviceKey || before.SessionGeneration != coordinator.Current.SessionGeneration)
                { return StartupFailure("StartupSessionExpired", "提示期间设备会话已变化，未执行自动切换"); }
                return await protocol.SwitchModeAsync(DeviceMode.Theme, ProtocolToken);
            }, switchAtStartup ? "启动设备处理已完成" : "设备检测已启动");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "StartupDevicePolicy");
            ProtocolErrorText = $"启动设备处理未完成，请手动检测。错误编号：{report.ErrorId}";
        }
        finally { if (!stopping) { statusSampling ??= SampleDeviceStateAsync(); } }
    }

    private static OperationResult<Unit> StartupFailure(string code, string reason) =>
        new OperationResult<Unit>.Failure(AppError.Create(code, reason, "请在设备页核实状态后操作"));

    private static OperationResult<Unit> StartupConvert<T>(OperationResult<T> result) => result switch
    {
        OperationResult<T>.Failure failure => new OperationResult<Unit>.Failure(failure.Error),
        OperationResult<T>.Unknown unknown => new OperationResult<Unit>.Unknown(unknown.Error),
        _ => new OperationResult<Unit>.Cancelled()
    };
}
