using System.Collections.Immutable;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Devices;

public enum DeviceChangeKind { Arrived, Removed, RescanRequired }
public sealed record DeviceChange(DeviceChangeKind Kind, string? InterfacePath)
{
    public long? NotificationEpoch { get; init; }
}

public interface IDeviceDiscovery : IAsyncDisposable
{
    // 使用者先订阅变化流，再发起初始枚举；实现不得在回调线程执行连接操作。
    IAsyncEnumerable<DeviceChange> WatchAsync(CancellationToken cancellationToken);
    Task<OperationResult<ImmutableArray<DeviceIdentity>>> EnumerateAsync(CancellationToken cancellationToken);
}
