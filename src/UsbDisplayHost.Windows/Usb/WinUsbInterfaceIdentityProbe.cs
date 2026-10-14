using System.Buffers.Binary;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Windows.Usb;

public sealed record WinUsbIdentityCompatibility(Guid InterfaceClass, ushort VendorId, ushort ProductId,
    ushort? DeviceRevision, string EvidenceReference, bool DisplayFunctionOnly = false);

public sealed class WinUsbInterfaceIdentityProbe : IDeviceInterfaceIdentityProbe
{
    private static readonly object OwnersSync = new();
    private static readonly HashSet<WinUsbInterfaceIdentityProbe> Owners = [];
    private readonly ImmutableArray<WinUsbIdentityCompatibility> compatibility;
    private readonly IErrorReporter reporter;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object closeSync = new();
    private Task<OperationResult<Unit>>? closing;
    private AppError? unconfirmed;
    private object? retainedOwner;
    private int stopping;
    private sealed record MetadataOwner(SafeUsbFileHandle? File, SafeWinUsbHandle? Usb, WinUsbPathOwnership? Path);
    private WinUsbInterfaceIdentityProbe(ImmutableArray<WinUsbIdentityCompatibility> compatibility, IErrorReporter reporter)
    { this.compatibility = compatibility; this.reporter = reporter; }

    public static OperationResult<WinUsbInterfaceIdentityProbe> Create(ImmutableArray<WinUsbIdentityCompatibility> compatibility,
        IErrorReporter reporter)
    {
        if (compatibility.IsDefaultOrEmpty || compatibility.Length > 8 || compatibility.Any(item => item is null
            || item.InterfaceClass == Guid.Empty || item.VendorId == 0 || string.IsNullOrWhiteSpace(item.EvidenceReference)
            || item.EvidenceReference.Length > 256)
            || compatibility.Select(item => (item.InterfaceClass, item.VendorId, item.ProductId, item.DeviceRevision)).Distinct().Count() != compatibility.Length)
        {
            return new OperationResult<WinUsbInterfaceIdentityProbe>.Failure(AppError.Create("InvalidWinUsbIdentityCompatibility",
                "WinUSB 身份兼容记录缺失、重复或超过限额", "请提供实际接口 GUID、VID/PID、修订与证据来源"));
        }
        lock (OwnersSync)
        {
            if (Owners.Count == 8 || Owners.Any(owner => owner.compatibility.Any(existing => compatibility.Any(
                candidate => candidate.InterfaceClass == existing.InterfaceClass))))
            {
                return new OperationResult<WinUsbInterfaceIdentityProbe>.Failure(AppError.Create("WinUsbIdentityOwnerOccupied",
                    "接口类已有身份探测所有者或达到限额", "请先确认原探测器关闭；未知释放不能用新实例替代"));
            }
            var owner = new WinUsbInterfaceIdentityProbe(compatibility, reporter); Owners.Add(owner);
            return new OperationResult<WinUsbInterfaceIdentityProbe>.Success(owner);
        }
    }
    public async Task<OperationResult<DeviceIdentity?>> ProbeAsync(DeviceInterfacePath path, CancellationToken token)
    {
        bool entered = false;
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false); entered = true;
            if ((unconfirmed ?? PnpInterfaceMetadataReader.UnconfirmedClose) is { } pending) { return new OperationResult<DeviceIdentity?>.Unknown(pending); }
            if (Volatile.Read(ref stopping) != 0) { return Failed("WinUsbIdentityProbeStopped", "WinUSB 身份探测已停止"); }
            return await Task.Run(() => ProbeCore(path, token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new OperationResult<DeviceIdentity?>.Cancelled(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return ReportFailure(exception); }
        finally { if (entered) { gate.Release(); } }
    }
    private OperationResult<DeviceIdentity?> ProbeCore(DeviceInterfacePath path, CancellationToken token)
    {
        SafeUsbFileHandle? file = null;
        SafeWinUsbHandle? usb = null;
        WinUsbPathOwnership? pathOwner = null;
        bool opened = false;
        OperationResult<DeviceIdentity?> result;
        try
        {
            token.ThrowIfCancellationRequested();
            if (path is null || string.IsNullOrWhiteSpace(path.Path) || path.Path.Length > 32767
                || !path.Path.StartsWith(@"\\?\", StringComparison.Ordinal) || !compatibility.Any(item => item.InterfaceClass == path.InterfaceClass))
            {
                return Failed("WinUsbIdentityInterfaceMismatch", "当前路径或接口类不属于身份兼容记录");
            }
            if (WinUsbPathOwnership.ReadVerified(path.Path, path.InterfaceClass) is { } activeIdentity)
            { return ReuseIdentity(activeIdentity, path, token); }
            var claimed = WinUsbPathOwnership.Acquire(path.Path);
            if (claimed is OperationResult<WinUsbPathOwnership>.Failure occupied) { return new OperationResult<DeviceIdentity?>.Failure(occupied.Error); }
            pathOwner = ((OperationResult<WinUsbPathOwnership>.Success)claimed).Value;
            file = WinUsbNative.CreateFile(path.Path, 0xC0000000, 3, nint.Zero, 3, 0x40000000, nint.Zero);
            if (file.IsInvalid) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            opened = true; token.ThrowIfCancellationRequested();
            if (!WinUsbNative.WinUsb_Initialize(file, out nint raw)) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            usb = new(raw, file);
            result = ReadIdentity(usb, path, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { result = new OperationResult<DeviceIdentity?>.Cancelled(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { result = ReportFailure(exception); }
        finally
        {
            usb?.Dispose();
            if (usb is { ReleaseConfirmed: false })
            {
                retainedOwner = new MetadataOwner(file, usb, pathOwner);
                var report = reporter.Report(usb.ReleaseError ?? new IOException("WinUSB metadata interface release unconfirmed."), "WinUsbIdentityFree");
                unconfirmed = new(report.ErrorId, "WinUsbIdentityFreeUnconfirmed", "WinUSB 身份探测释放未确认，所有者已保留",
                    "请保留诊断并重启应用，不能重复探测或提前关闭文件");
            }
            else
            {
                file?.Dispose();
                if (opened && file is { ReleaseConfirmed: false })
                {
                    retainedOwner = new MetadataOwner(file, usb, pathOwner);
                    var report = reporter.Report(file.ReleaseError ?? new IOException("WinUSB metadata file close unconfirmed."), "WinUsbIdentityFileClose");
                    unconfirmed = new(report.ErrorId, "WinUsbIdentityFileCloseUnconfirmed", "WinUSB 身份文件关闭未确认，探测已停止",
                        "请保留诊断并重启应用，不能释放原所有者");
                }
                else { pathOwner?.Dispose(); }
            }
        }
        return unconfirmed is { } pending ? new OperationResult<DeviceIdentity?>.Unknown(pending) : result;
    }
    private OperationResult<DeviceIdentity?> ReuseIdentity(WinUsbPathOwnership.VerifiedIdentity value,
        DeviceInterfacePath path, CancellationToken token)
    {
        var identity = value.Identity;
        if (!compatibility.Any(item => item.InterfaceClass == path.InterfaceClass && item.VendorId == identity.VendorId
            && item.ProductId == identity.ProductId && (item.DeviceRevision is null || item.DeviceRevision == value.Revision)))
        { return Failed("ActiveWinUsbIdentityUnverified", "活动会话身份不在当前探测兼容记录中"); }
        var metadata = PnpInterfaceMetadataReader.Read(path, reporter, token);
        token.ThrowIfCancellationRequested();
        if (!WinUsbPathOwnership.IsCurrent(value))
        { return Failed("ActiveWinUsbIdentityWithdrawn", "重扫期间活动身份已撤回，未返回旧身份"); }
        if (metadata is OperationResult<PnpInterfaceMetadata>.Success observed
            && (observed.Value.ContainerId != identity.ContainerId || observed.Value.Location != identity.Location))
        {
            value.Owner.RevokeVerified();
            return Failed("ActiveWinUsbPnpChanged", "活动接口 PnP 归属已变化，旧身份已撤回");
        }
        return metadata switch
        {
            OperationResult<PnpInterfaceMetadata>.Success actual when actual.Value.ContainerId == identity.ContainerId
                && actual.Value.Location == identity.Location => new OperationResult<DeviceIdentity?>.Success(identity),
            OperationResult<PnpInterfaceMetadata>.Unknown unknown => new OperationResult<DeviceIdentity?>.Unknown(unknown.Error),
            OperationResult<PnpInterfaceMetadata>.Failure failure => new OperationResult<DeviceIdentity?>.Failure(failure.Error),
            OperationResult<PnpInterfaceMetadata>.Cancelled => new OperationResult<DeviceIdentity?>.Cancelled(),
            _ => Failed("ActiveWinUsbPnpChanged", "活动接口 PnP 归属已变化，未复用旧身份")
        };
    }
    private OperationResult<DeviceIdentity?> ReadIdentity(SafeWinUsbHandle usb, DeviceInterfacePath path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        byte[] device = ReadDescriptor(usb, 1, 0, 0, 18);
        if (device.Length != 18 || device[0] != 18 || device[1] != 1 || device[17] == 0)
        { return Failed("WinUsbIdentityDescriptorInvalid", "USB 设备描述符无效或截断"); }
        ushort vendor = Word(device, 8), product = Word(device, 10), revision = Word(device, 12);
        if (!compatibility.Any(item => item.InterfaceClass == path.InterfaceClass && item.VendorId == vendor && item.ProductId == product))
        { return new OperationResult<DeviceIdentity?>.Success(null); }
        if (!compatibility.Any(item => item.InterfaceClass == path.InterfaceClass && item.VendorId == vendor && item.ProductId == product && (item.DeviceRevision is null || item.DeviceRevision == revision)))
        { return Failed("WinUsbIdentityRevisionUnverified", "目标 USB 设备修订尚未列入身份兼容记录"); }
        if (device[16] == 0) { return Failed("WinUsbIdentitySerialMissing", "USB 设备没有序列号字符串描述符"); }
        if (compatibility.Any(item => item.InterfaceClass == path.InterfaceClass && item.DisplayFunctionOnly))
        {
            if (!WinUsbNative.WinUsb_QueryInterfaceSettings(usb, 0, out var descriptor))
            { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            if (descriptor.InterfaceClass != 0xFF || descriptor.InterfaceSubClass != 0xFF)
            { return new OperationResult<DeviceIdentity?>.Success(null); }
        }
        token.ThrowIfCancellationRequested();
        byte[] languages = ReadDescriptor(usb, 3, 0, 0, 255);
        if (languages.Length < 4 || languages[0] != languages.Length || languages[1] != 3 || languages.Length % 2 != 0 || Word(languages, 2) == 0)
        { return Failed("WinUsbIdentityLanguageInvalid", "USB 语言描述符无效或截断"); }
        token.ThrowIfCancellationRequested();
        byte[] serialBytes = ReadDescriptor(usb, 3, device[16], Word(languages, 2), 255);
        if (serialBytes.Length < 4 || serialBytes[0] != serialBytes.Length || serialBytes[1] != 3 || serialBytes.Length % 2 != 0)
        { return Failed("WinUsbIdentitySerialInvalid", "USB 序列号描述符无效或截断"); }
        string serial = new UnicodeEncoding(false, false, true).GetString(serialBytes, 2, serialBytes.Length - 2);
        if (DeviceKey.FromSerialNumber(vendor, serial) is not OperationResult<DeviceKey>.Success)
        { return Failed("WinUsbIdentitySerialInvalid", "USB 真实序列号不能形成永久身份"); }
        token.ThrowIfCancellationRequested();
        return PnpInterfaceMetadataReader.Read(path, reporter, token) switch
        {
            OperationResult<PnpInterfaceMetadata>.Success metadata => new OperationResult<DeviceIdentity?>.Success(
                new(vendor, product, serial, metadata.Value.ContainerId, metadata.Value.Location, path.Path)),
            OperationResult<PnpInterfaceMetadata>.Failure failure => new OperationResult<DeviceIdentity?>.Failure(failure.Error),
            OperationResult<PnpInterfaceMetadata>.Unknown unknown => new OperationResult<DeviceIdentity?>.Unknown(unknown.Error),
            _ => new OperationResult<DeviceIdentity?>.Cancelled()
        };
    }
    private static byte[] ReadDescriptor(SafeWinUsbHandle usb, byte type, byte index, ushort language, int capacity)
    {
        byte[] bytes = new byte[capacity];
        if (!WinUsbNative.WinUsb_GetDescriptor(usb, type, index, language, bytes, (uint)capacity, out uint length))
        { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        if (length == 0 || length > capacity) { throw new InvalidDataException("USB descriptor returned invalid length."); }
        return bytes.AsSpan(0, (int)length).ToArray();
    }
    private static ushort Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    public async Task<OperationResult<Unit>> CancelAndDrainAsync(CancellationToken token)
    {
        bool entered = false;
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false); entered = true;
            return (unconfirmed ?? PnpInterfaceMetadataReader.UnconfirmedClose) is { } pending
                ? new OperationResult<Unit>.Unknown(pending) : new OperationResult<Unit>.Success(new());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new OperationResult<Unit>.Cancelled(); }
        finally { if (entered) { gate.Release(); } }
    }
    public Task<OperationResult<Unit>> CloseAsync()
    {
        lock (closeSync) { Volatile.Write(ref stopping, 1); return closing ??= CloseCoreAsync(); }
    }
    private async Task<OperationResult<Unit>> CloseCoreAsync()
    {
        var drained = await CancelAndDrainAsync(CancellationToken.None).ConfigureAwait(false);
        if (drained is not OperationResult<Unit>.Success) { return drained; }
        lock (OwnersSync) { Owners.Remove(this); }
        return new OperationResult<Unit>.Success(new());
    }
    public async ValueTask DisposeAsync()
    {
        if (await CloseAsync().ConfigureAwait(false) is not OperationResult<Unit>.Success)
        { throw new InvalidOperationException("WinUSB identity owner retained after unconfirmed release."); }
        GC.KeepAlive(retainedOwner);
    }
    private OperationResult<DeviceIdentity?> ReportFailure(Exception exception)
    {
        var report = reporter.Report(exception, "WinUsbIdentityProbe");
        return new OperationResult<DeviceIdentity?>.Failure(new(report.ErrorId, "WinUsbIdentityProbeFailed",
            "WinUSB 身份读取未完成，未将失败接口跳过", "请检查设备、驱动、权限与身份兼容记录"));
    }
    private static OperationResult<DeviceIdentity?> Failed(string code, string reason) =>
        new OperationResult<DeviceIdentity?>.Failure(AppError.Create(code, reason, "请核实实际接口与 USB 描述符后重新检测"));
}
