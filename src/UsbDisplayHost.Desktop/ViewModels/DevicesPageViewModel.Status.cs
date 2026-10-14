using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;

namespace UsbDisplayHost.Desktop.ViewModels;

public sealed partial class DevicesPageViewModel
{
    private Task? statusSampling;
    private bool queryingStatus;

    private async Task SampleDeviceStateAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                if (stopping || protocol is null || !CanControlProtocol() || protocol.HasPendingResources) { continue; }
                queryingStatus = true;
                var before = coordinator.Current;
                RefreshProtocolActions();
                try
                {
                    var result = await protocol.QueryStateAsync(lifetime.Token);
                    if (before.DeviceKey != coordinator.Current.DeviceKey || before.SessionGeneration != coordinator.Current.SessionGeneration)
                    { continue; }
                    if (result is OperationResult<Unit>.Failure failure) { ProtocolErrorText = Describe(failure.Error); }
                    else if (result is OperationResult<Unit>.Unknown unknown) { ProtocolErrorText = Describe(unknown.Error); }
                }
                finally { queryingStatus = false; RefreshProtocolActions(); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DeviceStatusSampling");
            ProtocolErrorText = $"设备状态刷新已停止，请重新连接设备。错误编号：{report.ErrorId}";
        }
    }
}
