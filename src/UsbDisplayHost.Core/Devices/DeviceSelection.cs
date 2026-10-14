using System.Collections.Immutable;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Devices;

public sealed record DeviceSelection
{
    internal DeviceSelection(DeviceKey key, ImmutableArray<DeviceIdentity> interfaces)
    {
        Key = key;
        Interfaces = interfaces;
    }
    public DeviceKey Key { get; }
    public ImmutableArray<DeviceIdentity> Interfaces { get; }
}

public static class DeviceSelectionResolver
{
    public static OperationResult<DeviceSelection> Resolve(IEnumerable<DeviceIdentity> discovered, DeviceKey selectedKey)
    {
        var paths = new Dictionary<string, DeviceIdentity>(StringComparer.OrdinalIgnoreCase);
        int inspected = 0;
        foreach (var identity in discovered)
        {
            if (identity is null || string.IsNullOrWhiteSpace(identity.InterfacePath)) { return Reject("InvalidInterfacePath", "设备接口路径无效"); }
            if (++inspected > 1024 || paths.Count >= 256 && !paths.ContainsKey(identity.InterfacePath))
            {
                return Reject("DiscoveryLimitExceeded", "设备发现结果超过处理上限");
            }
            if (paths.TryGetValue(identity.InterfacePath, out var existing))
            {
                if (existing.VendorId != identity.VendorId || existing.ProductId != identity.ProductId
                    || existing.SerialNumber != identity.SerialNumber || existing.ContainerId != identity.ContainerId)
                {
                    return Reject("ConflictingInterfaceIdentity", "同一接口返回了相互冲突的设备身份");
                }
            }
            else { paths.Add(identity.InterfacePath, identity); }
        }
        var matching = paths.Values.Where(identity => DeviceKey.FromSerialNumber(identity.VendorId, identity.SerialNumber)
            is OperationResult<DeviceKey>.Success key && key.Value == selectedKey).ToImmutableArray();
        if (matching.IsEmpty) { return Reject("SelectedDeviceMissing", "选定设备不在当前发现结果中"); }
        if (matching.Length > 1 && (matching.Any(identity => identity.ContainerId is null || identity.ContainerId == Guid.Empty)
            || matching.Select(identity => identity.ContainerId).Distinct().Count() != 1))
        {
            return Reject("AmbiguousDeviceIdentity", "设备序列号重复或接口关联存在歧义");
        }
        return new OperationResult<DeviceSelection>.Success(new DeviceSelection(selectedKey, matching));
    }

    private static OperationResult<DeviceSelection> Reject(string code, string reason) =>
        new OperationResult<DeviceSelection>.Failure(AppError.Create(code, reason, "请重新检测并明确选择设备，不进行自动接管"));
}
