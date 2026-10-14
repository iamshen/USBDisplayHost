using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Windows.Usb;

public sealed class WinUsbSessionFactory(IErrorReporter errorReporter)
{
    private int opening;
    private WinUsbOpenedSession? activeOwner;
    private object? unconfirmedOwner;
    private WinUsbPathOwnership? activePathOwner;
    private sealed record OpeningOwner(SafeUsbFileHandle? File, SafeWinUsbHandle? Primary, SafeWinUsbHandle? Associated, WinUsbPathOwnership? Path);
    private sealed class CleanupUnconfirmedException(Exception? cause) : Exception("WinUSB opening cleanup is unconfirmed.", cause);
    public bool RequiresProcessRestart => Volatile.Read(ref unconfirmedOwner) is not null;

    public async Task<OperationResult<WinUsbOpenedSession>> OpenAsync(DeviceSelectionAdmission admission, DeviceIdentity identity, WinUsbBinding binding, WinUsbIoPolicy policy,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref opening, 1, 0) != 0) { return Reject("WinUsbOpenBusy", "已有 WinUSB 打开操作在执行"); }
        try
        {
            if (RequiresProcessRestart)
            {
                return new OperationResult<WinUsbOpenedSession>.Unknown(AppError.Create("WinUsbCleanupUnconfirmed",
                    "上一次 WinUSB 释放未确认，拒绝重复打开", "请退出应用后重新连接设备"));
            }
            if (Volatile.Read(ref activeOwner) is not null) { return Reject("WinUsbSessionStillOwned", "原 WinUSB 会话尚未释放"); }
            // 取消不能提前结束等待并丢失后台成功打开的句柄。
            return await Task.Run(() => Open(admission, identity, binding, policy, cancellationToken), CancellationToken.None).ConfigureAwait(false);
        }
        catch (CleanupUnconfirmedException exception)
        {
            var report = errorReporter.Report(exception, "WinUsbOpenCleanup");
            return new OperationResult<WinUsbOpenedSession>.Unknown(new(report.ErrorId, "WinUsbOpenCleanupUnconfirmed",
                "WinUSB 打开后的清理未确认，保留句柄所有者", "请退出应用后重新连接设备"));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return Failure(exception); }
        finally { Volatile.Write(ref opening, 0); }
    }

    private OperationResult<WinUsbOpenedSession> Open(DeviceSelectionAdmission admission, DeviceIdentity identity, WinUsbBinding binding, WinUsbIoPolicy policy, CancellationToken token)
    {
        SafeUsbFileHandle? file = null;
        SafeWinUsbHandle? primary = null, associated = null;
        WinUsbPathOwnership? pathOwner = null;
        bool transferredOwnership = false;
        bool fileOpened = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!IsAdmissionCurrent(admission, identity)) { return Reject("WinUsbAdmissionStale", "选定接口或发现凭据已经失效"); }
            if (identity.VendorId != binding.VendorId || identity.ProductId != binding.ProductId
                || string.IsNullOrWhiteSpace(identity.SerialNumber)
                || identity.InterfacePath.Length > 32768 || !identity.InterfacePath.StartsWith(@"\\?\", StringComparison.Ordinal)
                || !identity.InterfacePath.EndsWith($"#{{{binding.InterfaceClass:D}}}", StringComparison.OrdinalIgnoreCase))
            {
                return Reject("WinUsbSelectionMismatch", "选定接口与冻结的 WinUSB 记录不匹配");
            }
            var claimed = WinUsbPathOwnership.Acquire(identity.InterfacePath);
            if (claimed is OperationResult<WinUsbPathOwnership>.Failure occupied) { return new OperationResult<WinUsbOpenedSession>.Failure(occupied.Error); }
            pathOwner = ((OperationResult<WinUsbPathOwnership>.Success)claimed).Value;
            pathOwner.RetainResourceOwner(this);
            if (!IsAdmissionCurrent(admission, identity)) { return Reject("WinUsbAdmissionChangedBeforeOpen", "路径预约后发现凭据已经变化"); }
            file = WinUsbNative.CreateFile(identity.InterfacePath, 0xC0000000, 3, 0, 3, 0x40000000, 0);
            if (file.IsInvalid) { throw NativeFailure(); }
            fileOpened = true;
            token.ThrowIfCancellationRequested();
            if (!IsAdmissionCurrent(admission, identity)) { return Reject("WinUsbAdmissionChangedBeforeInitialize", "文件打开后发现凭据已经变化"); }
            if (!WinUsbNative.WinUsb_Initialize(file, out nint raw)) { throw NativeFailure(); }
            primary = new(raw, file);
            token.ThrowIfCancellationRequested();
            byte[] device = ReadDescriptor(primary, 1, 0, 0, 18);
            if (device.Length != 18 || device[0] != 18 || device[1] != 1
                || Word(device, 8) != binding.VendorId || Word(device, 10) != binding.ProductId
                || !binding.DiscoverDisplayInterface && Word(device, 12) != binding.DeviceRelease || device[16] == 0)
            {
                return Reject("UsbDeviceDescriptorMismatch", "设备描述符与选定设备版本不匹配");
            }
            token.ThrowIfCancellationRequested();
            byte[] languages = ReadDescriptor(primary, 3, 0, 0, 255);
            if (languages.Length < 4 || languages[0] != languages.Length || languages[1] != 3 || languages.Length % 2 != 0)
            {
                return Reject("UsbLanguageDescriptorInvalid", "USB 语言描述符无效或截断");
            }
            ushort language = Word(languages, 2);
            if (language == 0) { return Reject("UsbLanguageUnavailable", "USB 序列号语言无效"); }
            byte[] serial = ReadDescriptor(primary, 3, device[16], language, 255);
            if (serial.Length < 4 || serial[0] != serial.Length || serial[1] != 3 || serial.Length % 2 != 0)
            {
                return Reject("UsbSerialDescriptorInvalid", "USB 序列号描述符无效或截断");
            }
            string actualSerial = new UnicodeEncoding(false, false, true).GetString(serial, 2, serial.Length - 2);
            if (!StringComparer.Ordinal.Equals(actualSerial, identity.SerialNumber)
                || DeviceKey.FromSerialNumber(binding.VendorId, actualSerial) is not OperationResult<DeviceKey>.Success)
            {
                return Reject("UsbSerialChanged", "USB 实际序列号不属于选定设备");
            }
            token.ThrowIfCancellationRequested();
            if (binding.AssociatedInterfaceIndex is { } index)
            {
                if (!WinUsbNative.WinUsb_GetAssociatedInterface(primary, index, out nint other)) { throw NativeFailure(); }
                associated = new(other, file);
            }
            var target = associated ?? primary;
            if (!WinUsbNative.WinUsb_GetCurrentAlternateSetting(target, out byte current)
                || !WinUsbNative.WinUsb_QueryInterfaceSettings(target, binding.AlternateSettingIndex, out var descriptor)) { throw NativeFailure(); }
            if (descriptor.Length != 9 || descriptor.DescriptorType != 4 || descriptor.EndpointCount is < 1 or > 30
                || !binding.DiscoverDisplayInterface && descriptor.InterfaceNumber != binding.InterfaceNumber || descriptor.AlternateSetting != binding.AlternateSetting
                || current != binding.AlternateSetting)
            {
                return Reject("UsbInterfaceMismatch", "当前接口或替代设置与兼容记录不匹配");
            }
            WinUsbNative.PipeInformation? output = null;
            if (binding.DiscoverDisplayInterface && (descriptor.InterfaceClass != 0xFF || descriptor.InterfaceSubClass != 0xFF))
            { return Reject("NotDisplayFunction", "所选 WinUSB function 不是 Display FF/FF，不能向维护通道推流"); }
            var pipeIds = new HashSet<byte>();
            for (byte pipeIndex = 0; pipeIndex < descriptor.EndpointCount; pipeIndex++)
            {
                token.ThrowIfCancellationRequested();
                if (!WinUsbNative.WinUsb_QueryPipe(target, binding.AlternateSettingIndex, pipeIndex, out var pipe)) { throw NativeFailure(); }
                if (pipe.PipeType is < 0 or > 3 || (pipe.PipeId & 0x70) != 0 || (pipe.PipeId & 15) == 0
                    || pipe.MaximumPacketSize == 0 || !pipeIds.Add(pipe.PipeId))
                {
                    return Reject("UsbPipeDescriptorInvalid", "USB 端点类型、地址或包长无效");
                }
                if (binding.DiscoverDisplayInterface ? pipe.PipeType == 2 && (pipe.PipeId & 0x80) == 0 : pipe.PipeId == binding.BulkOutPipe)
                {
                    if (output is not null) { return Reject("AmbiguousDisplayEndpoint", "Display 包含多个 Bulk OUT，无法选择发送端点"); }
                    if (pipe.PipeType != 2 || (pipe.PipeId & 0x80) != 0 || pipe.MaximumPacketSize > 1024)
                    {
                        return Reject("UsbBulkOutMismatch", "目标端点不是有效 Bulk OUT");
                    }
                    output = pipe;
                }
            }
            if (output is null) { return Reject("UsbBulkOutMissing", "没有找到兼容记录中的 Bulk OUT 端点"); }
            byte bulkOutPipe = output.Value.PipeId;
            uint valueLength = sizeof(uint);
            if (!WinUsbNative.WinUsb_GetPipePolicy(target, bulkOutPipe, 0x08, ref valueLength, out uint maximumTransfer)) { throw NativeFailure(); }
            if (valueLength != sizeof(uint) || maximumTransfer <= 11 || maximumTransfer > int.MaxValue)
            {
                return Reject("UsbTransferLimitInvalid", "WinUSB 最大传输长度无效");
            }
            token.ThrowIfCancellationRequested();
            if (!IsAdmissionCurrent(admission, identity)) { return Reject("WinUsbAdmissionChangedBeforePolicy", "端点核验后发现凭据已经变化，未设置策略"); }
            SetVerifiedPolicy(target, bulkOutPipe, 0x03, 4, (uint)policy.WriteTimeoutMilliseconds);
            SetVerifiedPolicy(target, bulkOutPipe, 0x01, 1, policy.ShortPacketTerminate ? 1u : 0u);
            token.ThrowIfCancellationRequested();
            var metadata = PnpInterfaceMetadataReader.Read(new(binding.InterfaceClass, identity.InterfacePath), errorReporter, token);
            if (metadata is OperationResult<PnpInterfaceMetadata>.Unknown unknown)
            { return new OperationResult<WinUsbOpenedSession>.Unknown(unknown.Error); }
            if (metadata is OperationResult<PnpInterfaceMetadata>.Failure metadataFailure)
            { return new OperationResult<WinUsbOpenedSession>.Failure(metadataFailure.Error); }
            if (metadata is not OperationResult<PnpInterfaceMetadata>.Success actualMetadata)
            { return new OperationResult<WinUsbOpenedSession>.Cancelled(); }
            if (actualMetadata.Value.ContainerId != identity.ContainerId || actualMetadata.Value.Location != identity.Location)
            { return Reject("UsbPnpIdentityChanged", "当前 PnP 归属与选定设备不一致"); }
            token.ThrowIfCancellationRequested();
            var session = new WinUsbOpenedSession(file, primary, associated, identity, descriptor.InterfaceNumber,
                current, bulkOutPipe, new(output.Value.MaximumPacketSize, (int)maximumTransfer), OnReleased,
                pathOwner.RevokeVerified, policy, errorReporter);
            if (!IsAdmissionCurrent(admission, identity)) { return Reject("WinUsbAdmissionChangedBeforeDelivery", "打开核验结束后发现凭据已经变化"); }
            pathOwner.PublishVerified(binding.InterfaceClass, Word(device, 12), identity);
            if (!IsAdmissionCurrent(admission, identity))
            {
                pathOwner.RevokeVerified();
                return Reject("WinUsbAdmissionChangedAtDelivery", "身份发布期间发现凭据已经变化，未交付会话");
            }
            Volatile.Write(ref activeOwner, session);
            Volatile.Write(ref activePathOwner, pathOwner);
            transferredOwnership = true;
            return new OperationResult<WinUsbOpenedSession>.Success(session);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new OperationResult<WinUsbOpenedSession>.Cancelled(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return Failure(exception); }
        finally
        {
            if (!transferredOwnership)
            {
                pathOwner?.RevokeVerified();
                associated?.Dispose();
                if (associated is { ReleaseConfirmed: false })
                {
                    Volatile.Write(ref unconfirmedOwner, new OpeningOwner(file, primary, associated, pathOwner));
                    throw new CleanupUnconfirmedException(associated.ReleaseError);
                }
                primary?.Dispose();
                if (primary is { ReleaseConfirmed: false })
                {
                    Volatile.Write(ref unconfirmedOwner, new OpeningOwner(file, primary, associated, pathOwner));
                    throw new CleanupUnconfirmedException(primary.ReleaseError);
                }
                file?.Dispose();
                if (fileOpened && file is { ReleaseConfirmed: false })
                {
                    Volatile.Write(ref unconfirmedOwner, new OpeningOwner(file, primary, associated, pathOwner));
                    throw new CleanupUnconfirmedException(file.ReleaseError);
                }
                pathOwner?.Dispose();
            }
        }
    }

    private static bool IsAdmissionCurrent(DeviceSelectionAdmission admission, DeviceIdentity identity) =>
        admission is not null && admission.Selection is { } selection && admission.Validity is { } validity
        && identity is not null && selection.Interfaces.Contains(identity) && validity.IsCurrent(selection);

    private void OnReleased(WinUsbOpenedSession owner, bool confirmed)
    {
        Volatile.Read(ref activePathOwner)?.RevokeVerified();
        if (!confirmed) { Volatile.Write(ref unconfirmedOwner, owner); }
        else
        {
            Interlocked.Exchange(ref activePathOwner, null)?.Dispose();
            Interlocked.CompareExchange(ref activeOwner, null, owner);
        }
    }

    private static byte[] ReadDescriptor(SafeWinUsbHandle handle, byte type, byte index, ushort language, int capacity)
    {
        byte[] bytes = new byte[capacity];
        if (!WinUsbNative.WinUsb_GetDescriptor(handle, type, index, language, bytes, (uint)capacity, out uint length)) { throw NativeFailure(); }
        if (length == 0 || length > capacity) { throw new InvalidDataException("Invalid USB descriptor length."); }
        return bytes.AsSpan(0, (int)length).ToArray();
    }

    private static ushort Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static void SetVerifiedPolicy(SafeWinUsbHandle target, byte pipe, uint type, uint size, uint value)
    {
        uint setting = value;
        if (!WinUsbNative.WinUsb_SetPipePolicy(target, pipe, type, size, ref setting)) { throw NativeFailure(); }
        uint length = size;
        if (!WinUsbNative.WinUsb_GetPipePolicy(target, pipe, type, ref length, out uint read)) { throw NativeFailure(); }
        if (length != size || read != value) { throw new InvalidDataException("USB pipe policy readback mismatch."); }
    }
    private static Win32Exception NativeFailure() => new(Marshal.GetLastPInvokeError());
    private static OperationResult<WinUsbOpenedSession> Reject(string code, string reason) =>
        new OperationResult<WinUsbOpenedSession>.Failure(AppError.Create(code, reason, "请重新选择设备并核对兼容记录"));
    private OperationResult<WinUsbOpenedSession> Failure(Exception exception)
    {
        var report = errorReporter.Report(exception, "WinUsbOpen");
        return new OperationResult<WinUsbOpenedSession>.Failure(new(report.ErrorId, "WinUsbOpenFailed",
            "WinUSB 接口打开或核验失败", "请检查所选设备连接、驱动绑定与访问权限"));
    }
}
