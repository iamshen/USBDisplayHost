using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Devices;

public sealed record DeviceKey
{
    private DeviceKey(string value) => Value = value;

    public string Value { get; }

    public static OperationResult<DeviceKey> FromSerialNumber(ushort vendorId, string? serialNumber)
    {
        if (string.IsNullOrWhiteSpace(serialNumber) || serialNumber.Length > 256
            || serialNumber.Any(char.IsControl))
        {
            return new OperationResult<DeviceKey>.Failure(AppError.Create(
                "IdentityUnavailable", "设备没有可用的唯一序列号", "请重新选择设备，不进行自动接管"));
        }

        // PID 会随模式改变，不能参与持久化身份；重复序列号仍须由发现层拒绝关联。
        return new OperationResult<DeviceKey>.Success(new DeviceKey($"{vendorId:X4}:{serialNumber}"));
    }
}
