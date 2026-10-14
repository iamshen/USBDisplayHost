using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Devices;

namespace UsbDisplayHost.Windows.Usb;

internal sealed class WinUsbPathOwnership : IDisposable
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, WinUsbPathOwnership> Paths = new(StringComparer.OrdinalIgnoreCase);
    internal sealed record VerifiedIdentity(WinUsbPathOwnership Owner, Guid InterfaceClass, ushort Revision, DeviceIdentity Identity);
    private VerifiedIdentity? verified;
    internal object? ResourceOwner { get; private set; }
    private readonly string path;
    private int released;
    private WinUsbPathOwnership(string path) { this.path = path; }
    internal static OperationResult<WinUsbPathOwnership> Acquire(string path)
    {
        lock (Sync)
        {
            if (Paths.Count == 32 || Paths.ContainsKey(path))
            {
                return new OperationResult<WinUsbPathOwnership>.Failure(AppError.Create("WinUsbPathOccupied",
                    "接口已有探测或会话所有者，未重新初始化 WinUSB", "请使用已核实的活动身份或等待原所有者明确释放后重扫"));
            }
            var owner = new WinUsbPathOwnership(path);
            Paths.Add(path, owner);
            return new OperationResult<WinUsbPathOwnership>.Success(owner);
        }
    }
    internal void PublishVerified(Guid interfaceClass, ushort revision, DeviceIdentity identity)
    {
        lock (Sync)
        {
            if (released != 0 || !Paths.TryGetValue(path, out var owner) || !ReferenceEquals(owner, this)
                || interfaceClass == Guid.Empty || !StringComparer.OrdinalIgnoreCase.Equals(path, identity.InterfacePath))
            { throw new InvalidOperationException("Verified identity no longer belongs to the path owner."); }
            verified = new(this, interfaceClass, revision, identity);
        }
    }
    internal void RetainResourceOwner(object owner)
    {
        lock (Sync)
        {
            if (released != 0 || ResourceOwner is not null) { throw new InvalidOperationException("Path resource owner is already assigned or released."); }
            ResourceOwner = owner;
        }
    }
    internal static VerifiedIdentity? ReadVerified(string path, Guid interfaceClass)
    {
        lock (Sync)
        {
            return Paths.TryGetValue(path, out var owner) && owner.verified is { } value && value.InterfaceClass == interfaceClass
                ? value : null;
        }
    }
    internal static bool IsCurrent(VerifiedIdentity value)
    {
        lock (Sync) { return ReferenceEquals(value.Owner.verified, value) && value.Owner.released == 0; }
    }
    internal void RevokeVerified() { lock (Sync) { verified = null; } }
    internal static void RevokeRemovedPath(string? path)
    {
        lock (Sync)
        {
            if (path is null) { foreach (var owner in Paths.Values) { owner.verified = null; } }
            else if (Paths.TryGetValue(path, out var owner)) { owner.verified = null; }
        }
    }
    public void Dispose()
    {
        lock (Sync)
        {
            if (released != 0) { return; }
            released = 1; verified = null; ResourceOwner = null; Paths.Remove(path);
        }
    }
}
