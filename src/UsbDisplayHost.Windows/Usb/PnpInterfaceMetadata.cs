using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Windows.Usb;

internal sealed record PnpInterfaceMetadata(Guid? ContainerId, string? Location);

internal static class PnpInterfaceMetadataReader
{
    private static readonly object Sync = new();
    private static SafeDeviceInfoSet? retainedSet;
    private static AppError? closeError;
    internal static AppError? UnconfirmedClose => Volatile.Read(ref closeError);

    internal static OperationResult<PnpInterfaceMetadata> Read(DeviceInterfacePath path, IErrorReporter reporter, CancellationToken token)
    {
        // 单一后台元数据读取；未知释放时仅保留一个信息集并阻止继续分配。
        lock (Sync)
        {
            if (closeError is { } pending) { return new OperationResult<PnpInterfaceMetadata>.Unknown(pending); }
            SafeDeviceInfoSet? set = null;
            bool opened = false;
            OperationResult<PnpInterfaceMetadata> result;
            try
            {
                token.ThrowIfCancellationRequested();
                Guid interfaceClass = path.InterfaceClass;
                set = PnpMetadataNative.SetupDiGetClassDevsW(ref interfaceClass, null, nint.Zero, 0x12);
                if (set.IsInvalid) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
                opened = true;
                var deviceInterface = new PnpMetadataNative.InterfaceData { Size = (uint)Marshal.SizeOf<PnpMetadataNative.InterfaceData>() };
                if (!PnpMetadataNative.SetupDiOpenDeviceInterfaceW(set, path.Path, 1, ref deviceInterface))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                if (deviceInterface.InterfaceClass != path.InterfaceClass || (deviceInterface.Flags & 1) == 0 || (deviceInterface.Flags & 4) != 0)
                {
                    throw new InvalidDataException("PnP interface is not active or class ownership changed.");
                }
                var device = new PnpMetadataNative.DeviceData { Size = (uint)Marshal.SizeOf<PnpMetadataNative.DeviceData>() };
                // 官方允许不取路径缓冲：ERROR_INSUFFICIENT_BUFFER 时填充 DeviceInfoData。
                bool details = PnpMetadataNative.SetupDiGetDeviceInterfaceDetailW(set, ref deviceInterface, nint.Zero, 0, out _, ref device);
                if (details || Marshal.GetLastPInvokeError() != 122 || device.DeviceInstance == 0)
                {
                    throw new InvalidDataException("PnP interface-to-devnode detail was not confirmed.");
                }
                token.ThrowIfCancellationRequested();
                var containerKey = new PnpMetadataNative.PropertyKey { Format = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), Id = 2 };
                var locationKey = new PnpMetadataNative.PropertyKey { Format = new("a45c254e-df1c-4efd-8020-67d146a850e0"), Id = 15 };
                var container = ReadProperty(device.DeviceInstance, containerKey, 0xD, 16, token);
                Guid? containerId = container is null ? null : new Guid(container);
                if (containerId == Guid.Empty) { throw new InvalidDataException("PnP container identifier is empty."); }
                var location = ReadProperty(device.DeviceInstance, locationKey, 0x12, 2050, token);
                string? locationText = null;
                if (location is not null)
                {
                    if (location.Length < 2 || location.Length % 2 != 0 || location[^1] != 0 || location[^2] != 0)
                    {
                        throw new InvalidDataException("PnP location string is not terminated UTF-16.");
                    }
                    locationText = new UnicodeEncoding(false, false, true).GetString(location, 0, location.Length - 2);
                    if (locationText.Any(char.IsControl) || locationText.Length > 1024)
                    {
                        throw new InvalidDataException("PnP location string is invalid.");
                    }
                }
                token.ThrowIfCancellationRequested();
                result = new OperationResult<PnpInterfaceMetadata>.Success(new(containerId, locationText));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { result = new OperationResult<PnpInterfaceMetadata>.Cancelled(); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = reporter.Report(exception, "PnpInterfaceMetadata");
                result = new OperationResult<PnpInterfaceMetadata>.Failure(new(report.ErrorId, "PnpMetadataFailed",
                    "设备接口归属或系统属性读取未完成", "请重新检测并核实接口与驱动，不从路径推断容器归属"));
            }
            finally
            {
                set?.Dispose();
                if (opened && set is { ReleaseConfirmed: false })
                {
                    retainedSet = set;
                    var report = reporter.Report(set.ReleaseError ?? new IOException("PnP information set release unconfirmed."), "PnpInformationSetClose");
                    Volatile.Write(ref closeError, new(report.ErrorId, "PnpInformationSetCloseUnconfirmed",
                        "PnP 信息集释放未确认，后续身份探测已停止", "请保留诊断并重启应用，不能继续分配或丢弃原所有者"));
                }
            }
            GC.KeepAlive(retainedSet);
            return closeError is { } unknown ? new OperationResult<PnpInterfaceMetadata>.Unknown(unknown) : result;
        }
    }

    private static byte[]? ReadProperty(uint device, PnpMetadataNative.PropertyKey key, uint expectedType, int limit, CancellationToken token)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            uint length = 0;
            uint status = PnpMetadataNative.CM_Get_DevNode_PropertyW(device, ref key, out uint type, null, ref length, 0);
            if (status == 0x25) { return null; }
            if (status != 0x1A || type != expectedType || length == 0 || length > limit)
            {
                throw new InvalidDataException($"PnP property size/type query failed: CONFIGRET 0x{status:X8}.");
            }
            byte[] bytes = new byte[length];
            uint received = length;
            status = PnpMetadataNative.CM_Get_DevNode_PropertyW(device, ref key, out type, bytes, ref received, 0);
            if (status == 0x1A) { continue; }
            if (status != 0 || type != expectedType || received != length || expectedType == 0xD && received != 16)
            {
                throw new InvalidDataException($"PnP property value query failed: CONFIGRET 0x{status:X8}.");
            }
            return bytes;
        }
        throw new IOException("PnP property changed beyond bounded retries.");
    }
}

internal static class PnpMetadataNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct InterfaceData { internal uint Size; internal Guid InterfaceClass; internal uint Flags; internal nuint Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceData { internal uint Size; internal Guid DeviceClass; internal uint DeviceInstance; internal nuint Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey { internal Guid Format; internal uint Id; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern SafeDeviceInfoSet SetupDiGetClassDevsW(ref Guid interfaceClass, string? enumerator, nint window, uint flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiOpenDeviceInterfaceW(SafeDeviceInfoSet set, string path, uint flags, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInterfaceDetailW(SafeDeviceInfoSet set, ref InterfaceData data,
        nint detail, uint bytes, out uint required, ref DeviceData device);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint CM_Get_DevNode_PropertyW(uint device, ref PropertyKey key, out uint type,
        [Out] byte[]? bytes, ref uint length, uint flags);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiDestroyDeviceInfoList(nint set);
}

internal sealed class SafeDeviceInfoSet : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeDeviceInfoSet() : base(true) { }
    internal bool ReleaseConfirmed { get; private set; }
    internal Exception? ReleaseError { get; private set; }
    protected override bool ReleaseHandle()
    {
        try
        {
            ReleaseConfirmed = PnpMetadataNative.SetupDiDestroyDeviceInfoList(handle);
            if (!ReleaseConfirmed) { ReleaseError = new Win32Exception(Marshal.GetLastPInvokeError()); }
            return ReleaseConfirmed;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ReleaseError = exception; return false; }
    }
}
