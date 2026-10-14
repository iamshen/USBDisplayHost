using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Configuration;

public sealed record DeviceSettingsDraft(DeviceKey? DeviceKey, long Revision, DeviceSettings Settings);
public sealed record DeviceSettingsDraftReadResult(DeviceSettingsDraft? Draft, ProfileRecovery Recovery, AppError? Error);

public interface IDeviceSettingsDraftStore
{
    Task<OperationResult<DeviceSettingsDraftReadResult>> LoadDeviceSettingsDraftAsync(DeviceKey? deviceKey, CancellationToken cancellationToken);
    Task<OperationResult<Unit>> SaveDeviceSettingsDraftAsync(DeviceSettingsDraft draft, CancellationToken cancellationToken);
}
