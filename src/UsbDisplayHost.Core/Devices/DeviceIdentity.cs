namespace UsbDisplayHost.Core.Devices;

public sealed record DeviceIdentity(
    ushort VendorId,
    ushort ProductId,
    string? SerialNumber,
    Guid? ContainerId,
    string? Location,
    string InterfacePath);
