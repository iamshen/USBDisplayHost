using System.Collections.Immutable;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Sessions;

public enum ConnectionState { Disconnected, Discovered, Opening, Ready, Recovering, Faulted }
public enum DeviceMode { Unknown, Theme, SecondaryDisplay }
public enum PowerState { Unknown, On, Off }
public enum StreamingState { Stopped, Running, Paused, Faulted }

public sealed record DeviceSnapshot(
    long Version,
    long SessionGeneration,
    DeviceKey? DeviceKey,
    DeviceIdentity? Identity,
    DeviceCapabilities? Capabilities,
    ConnectionState Connection,
    DeviceMode Mode,
    PowerState Power,
    StreamingState Streaming,
    Guid? PendingOperationId,
    int? RequestedBrightness,
    int? ObservedBrightness,
    AppError? Error)
{
    public ImmutableArray<DeviceIdentity> SelectedInterfaces { get; init; } = [];
    public static DeviceSnapshot Disconnected { get; } = new(
        0, 0, null, null, null, ConnectionState.Disconnected, DeviceMode.Unknown,
        PowerState.Unknown, StreamingState.Stopped, null, null, null, null);
}

public readonly record struct OperationContext(
    DeviceKey DeviceKey, long SessionGeneration, Guid OperationId, long ContentRevision);
