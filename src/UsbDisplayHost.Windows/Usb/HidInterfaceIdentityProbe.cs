using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Windows.Usb;

public sealed record HidCollectionCompatibility(ushort UsagePage, ushort Usage,
    ushort InputBytes, ushort OutputBytes, ushort FeatureBytes);
public sealed record HidIdentityCompatibility(ushort VendorId, ushort ProductId, ushort? DeviceRevision, string EvidenceReference,
    HidCollectionCompatibility? Collection = null);
public sealed record Ad102V2HidProbeResult(DeviceIdentity Identity, Ad102V2HidCodec Codec);

// 默认只读身份元数据；显式 V2 查询仅读取 Feature，不发送控制命令。
public sealed class HidInterfaceIdentityProbe : IDeviceInterfaceIdentityProbe
{
    private static readonly object OwnersSync = new();
    private static readonly HashSet<HidInterfaceIdentityProbe> Owners = [];
    private static readonly Dictionary<string, HidInterfaceIdentityProbe> PathOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly ImmutableArray<HidIdentityCompatibility> compatibility;
    private readonly IErrorReporter reporter;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object closeSync = new();
    private Task<OperationResult<Unit>>? closing;
    private AppError? unconfirmedClose;
    private SafeUsbFileHandle? retainedHandle;
    private SafeHidPreparsedDataHandle? retainedPreparsedData;
    private int stopping;
    private HidOpenedSession? activeSession;

    private HidInterfaceIdentityProbe(ImmutableArray<HidIdentityCompatibility> compatibility, IErrorReporter reporter)
    {
        this.compatibility = compatibility; this.reporter = reporter;
    }
    // 创建本身没有原生调用；清单由实际兼容记录提供，不内置设计 VID/PID。
    public static OperationResult<HidInterfaceIdentityProbe> Create(ImmutableArray<HidIdentityCompatibility> compatibility,
        IErrorReporter reporter)
    {
        if (compatibility.IsDefaultOrEmpty || compatibility.Length > 8
            || compatibility.Any(item => item is null || item.VendorId == 0
                || string.IsNullOrWhiteSpace(item.EvidenceReference) || item.EvidenceReference.Length > 256
                || item.Collection is { } collection && (collection.UsagePage == 0 || collection.Usage == 0
                    || collection.InputBytes is 0 or > 4096 || collection.OutputBytes is 0 or > 4096
                    || collection.FeatureBytes is 0 or > 4096))
            || compatibility.Select(item => (item.VendorId, item.ProductId, item.DeviceRevision)).Distinct().Count() != compatibility.Length)
        {
            return new OperationResult<HidInterfaceIdentityProbe>.Failure(AppError.Create("InvalidHidIdentityCompatibility",
                "HID 身份兼容记录缺失、重复或超出限额", "请提供实际 VID/PID、设备修订与证据来源"));
        }
        lock (OwnersSync)
        {
            if (Owners.Count == 8 || Owners.Any(owner => owner.compatibility.Any(existing =>
                compatibility.Any(candidate => candidate.VendorId == existing.VendorId && candidate.ProductId == existing.ProductId))))
            {
                return new OperationResult<HidInterfaceIdentityProbe>.Failure(AppError.Create("HidProbeOwnerLimit",
                    "目标 HID 身份已有探测所有者或达到限额", "请先确认现有探测器关闭；未知句柄不能用新实例替代"));
            }
            var owner = new HidInterfaceIdentityProbe(compatibility, reporter);
            Owners.Add(owner);
            return new OperationResult<HidInterfaceIdentityProbe>.Success(owner);
        }
    }

    public async Task<OperationResult<DeviceIdentity?>> ProbeAsync(DeviceInterfacePath path, CancellationToken token)
    {
        bool entered = false;
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false); entered = true;
            if ((unconfirmedClose ?? PnpInterfaceMetadataReader.UnconfirmedClose) is { } unknown) { return new OperationResult<DeviceIdentity?>.Unknown(unknown); }
            if (Volatile.Read(ref activeSession) is { } active && active.IsCurrent
                && StringComparer.OrdinalIgnoreCase.Equals(active.Identity.InterfacePath, path.Path))
            { return new OperationResult<DeviceIdentity?>.Success(active.Identity); }
            if (Volatile.Read(ref stopping) != 0)
            { return Failed("HidProbeStopped", "HID 探测器已停止或控制会话仍持有接口", "请先确认原所有者关闭"); }
            // 不取消等待中的 Task；同步 HID 元数据调用返回后才检查取消并关闭句柄。
            return await Task.Run(() => ProbeCore(path, token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new OperationResult<DeviceIdentity?>.Cancelled(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return ReportFailure(exception); }
        finally { if (entered) { gate.Release(); } }
    }

    public async Task<OperationResult<Ad102V2HidProbeResult?>> ProbeAd102V2Async(
        DeviceInterfacePath path, CancellationToken token)
    {
        // 禁止以纯身份兼容记录绕过当前 collection 核验。
        if (compatibility.Any(item => item.Collection is not { } collection
            || collection.UsagePage != Ad102V2HidCodec.UsagePage || collection.Usage != Ad102V2HidCodec.Usage
            || collection.InputBytes != Ad102V2HidCodec.ReportBytes
            || collection.OutputBytes != Ad102V2HidCodec.ReportBytes
            || collection.FeatureBytes != Ad102V2HidCodec.ReportBytes))
        {
            return new OperationResult<Ad102V2HidProbeResult?>.Failure(AppError.Create("V2CollectionContractRequired",
                "V2 查询缺少完整的目标 collection 契约", "请先提供实际身份及 FF00／1、64 字节报告的兼容证据"));
        }
        bool entered = false;
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false); entered = true;
            if ((unconfirmedClose ?? PnpInterfaceMetadataReader.UnconfirmedClose) is { } unknown)
            { return new OperationResult<Ad102V2HidProbeResult?>.Unknown(unknown); }
            if (Volatile.Read(ref stopping) != 0 || Volatile.Read(ref activeSession) is not null)
            {
                return new OperationResult<Ad102V2HidProbeResult?>.Failure(AppError.Create("HidProbeStopped",
                    "HID 身份探测器已停止", "请先确认原所有者关闭"));
            }
            // 同步调用与关闭都完成后才释放门；取消不能遗弃驱动中的查询。
            return await Task.Run<OperationResult<Ad102V2HidProbeResult?>>(() =>
            {
                var result = ProbeCore(path, token, true, out var codec);
                return result switch
                {
                    OperationResult<DeviceIdentity?>.Success { Value: null } =>
                        new OperationResult<Ad102V2HidProbeResult?>.Success(null),
                    OperationResult<DeviceIdentity?>.Success actual when codec is not null =>
                        new OperationResult<Ad102V2HidProbeResult?>.Success(new(actual.Value!, codec)),
                    OperationResult<DeviceIdentity?>.Failure failure =>
                        new OperationResult<Ad102V2HidProbeResult?>.Failure(failure.Error),
                    OperationResult<DeviceIdentity?>.Unknown uncertain =>
                        new OperationResult<Ad102V2HidProbeResult?>.Unknown(uncertain.Error),
                    OperationResult<DeviceIdentity?>.Cancelled => new OperationResult<Ad102V2HidProbeResult?>.Cancelled(),
                    _ => new OperationResult<Ad102V2HidProbeResult?>.Failure(AppError.Create("V2FeatureUnavailable",
                        "HID 协议查询没有有效结果", "请核对当前固件并重新检测"))
                };
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new OperationResult<Ad102V2HidProbeResult?>.Cancelled(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "HidV2FeatureProbe");
            return new OperationResult<Ad102V2HidProbeResult?>.Failure(new(report.ErrorId, "V2FeatureProbeFailed",
                "HID 协议查询失败，未启用控制能力", "请检查设备和接口权限，保留诊断编号"));
        }
        finally { if (entered) { gate.Release(); } }
    }

    private OperationResult<DeviceIdentity?> ProbeCore(DeviceInterfacePath path, CancellationToken token)
        => ProbeCore(path, token, false, out _);

    private OperationResult<DeviceIdentity?> ProbeCore(DeviceInterfacePath path, CancellationToken token,
        bool readFeature, out Ad102V2HidCodec? codec)
        => ProbeCore(path, token, readFeature, out codec, false, out _);

    private OperationResult<DeviceIdentity?> ProbeCore(DeviceInterfacePath path, CancellationToken token,
        bool readFeature, out Ad102V2HidCodec? codec, bool retainVerifiedFile, out SafeUsbFileHandle? verifiedFile,
        Func<bool>? canOpen = null, DeviceIdentity? expected = null)
    {
        codec = null;
        verifiedFile = null;
        SafeUsbFileHandle? file = null;
        bool opened = false;
        bool claimed = false;
        OperationResult<DeviceIdentity?> result;
        try
        {
            token.ThrowIfCancellationRequested();
            if (path is null || string.IsNullOrWhiteSpace(path.Path) || path.Path.Length > 32767
                || !path.Path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return Failed("InvalidHidIdentityPath", "HID 身份探测路径无效", "请使用当前接口枚举结果");
            }
            lock (OwnersSync)
            {
                if (PathOwners.Count >= 32 || PathOwners.ContainsKey(path.Path))
                { return Failed("HidPathStillOwned", "HID 路径仍由原探测或会话持有", "先确认原所有者关闭，不重复打开接口"); }
                PathOwners.Add(path.Path, this);
                claimed = true;
            }
            HidIdentityNative.HidD_GetHidGuid(out Guid hidClass);
            if (hidClass == Guid.Empty || path.InterfaceClass != hidClass)
            {
                return Failed("HidIdentityClassMismatch", "接口不属于实际 HID 类，未打开设备", "请为数据面提供相应身份探测适配器");
            }
            token.ThrowIfCancellationRequested();
            if (canOpen is not null && !canOpen())
            { return Failed("HidAdmissionChangedBeforeOpen", "文件打开前发现凭据已经变化", "重新检测并明确选择设备"); }
            file = HidIdentityNative.CreateFileW(path.Path, retainVerifiedFile ? 0xC0000000u : 0,
                3, nint.Zero, 3, retainVerifiedFile ? 0x40000000u : 0, nint.Zero);
            if (file.IsInvalid) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            opened = true;
            result = ReadIdentity(file, path, token);
            if (expected is not null && result is OperationResult<DeviceIdentity?>.Success actualIdentity
                && actualIdentity.Value != expected)
            { result = Failed("HidIdentityChangedBeforeFeature", "实际文件身份与所选设备不一致，未查询 Feature", "重新检测并明确选择设备"); }
            if (readFeature && result is OperationResult<DeviceIdentity?>.Success { Value: not null })
            {
                token.ThrowIfCancellationRequested();
                // 非零填充使未写回的保留字段不能被误当成合法零值。
                byte[] report = new byte[Ad102V2HidCodec.ReportBytes];
                Array.Fill(report, (byte)0xA5);
                report[0] = 3;
                if (!HidIdentityNative.HidD_GetFeature(file, report, (uint)report.Length))
                { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
                token.ThrowIfCancellationRequested();
                var parsed = Ad102V2HidCodec.Create(report);
                if (parsed is OperationResult<Ad102V2HidCodec>.Success valid) { codec = valid.Value; }
                else if (parsed is OperationResult<Ad102V2HidCodec>.Failure invalid)
                { result = new OperationResult<DeviceIdentity?>.Failure(invalid.Error); }
            }
            token.ThrowIfCancellationRequested();
            if (retainVerifiedFile && unconfirmedClose is null && codec is not null
                && result is OperationResult<DeviceIdentity?>.Success { Value: not null })
            {
                if (canOpen is not null && !canOpen())
                { return Failed("HidAdmissionChangedBeforeFlush", "读取队列清理前发现凭据已经变化", "重新检测并明确选择设备"); }
                if (!HidIdentityNative.HidD_FlushQueue(file)) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
                token.ThrowIfCancellationRequested();
                verifiedFile = file;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { result = new OperationResult<DeviceIdentity?>.Cancelled(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { result = ReportFailure(exception, readFeature); }
        finally
        {
            if (retainedPreparsedData is not null) { retainedHandle = file; }
            else if (verifiedFile is null) { file?.Dispose(); }
            if (opened && verifiedFile is null && retainedPreparsedData is null && file is { ReleaseConfirmed: false })
            {
                retainedHandle = file;
                var report = reporter.Report(file.ReleaseError ?? new IOException("HID metadata handle close unconfirmed."), "HidIdentityHandleClose");
                unconfirmedClose = new(report.ErrorId, "HidIdentityCloseUnconfirmed", "HID 身份句柄关闭未确认，探测已停止",
                    "请保留诊断并重新启动应用，不能继续探测或释放原所有者");
            }
            if (claimed && verifiedFile is null && unconfirmedClose is null) { ReleasePath(path.Path); }
        }
        return unconfirmedClose is { } unknown ? new OperationResult<DeviceIdentity?>.Unknown(unknown) : result;
    }

    public async Task<OperationResult<HidOpenedSession>> OpenAd102V2SessionAsync(DeviceSelectionAdmission admission,
        DeviceInterfacePath path, DeviceIdentity expectedIdentity, TimeProvider clock, int timeoutMilliseconds, CancellationToken token)
    {
        var result = await OpenSessionCoreAsync(admission, path, expectedIdentity, clock, timeoutMilliseconds, token).ConfigureAwait(false);
        return unconfirmedClose is { } unknown ? new OperationResult<HidOpenedSession>.Unknown(unknown) : result;
    }

    private async Task<OperationResult<HidOpenedSession>> OpenSessionCoreAsync(DeviceSelectionAdmission admission, DeviceInterfacePath path,
        DeviceIdentity expectedIdentity, TimeProvider clock, int timeoutMilliseconds, CancellationToken token)
    {
        if (expectedIdentity is null || path is null || clock is null || timeoutMilliseconds is < 1 or > 60000
            || compatibility.Any(item => item.Collection is not { } caps
                || caps.UsagePage != Ad102V2HidCodec.UsagePage || caps.Usage != Ad102V2HidCodec.Usage
                || caps.InputBytes != Ad102V2HidCodec.ReportBytes || caps.OutputBytes != Ad102V2HidCodec.ReportBytes
                || caps.FeatureBytes != Ad102V2HidCodec.ReportBytes))
        { return SessionFailure("InvalidHidSessionContract", "控制会话缺少有效身份、时钟、等待限额或完整 V2 collection 契约"); }
        bool entered = false;
        SafeUsbFileHandle? file = null;
        bool delivered = false;
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false); entered = true;
            if ((unconfirmedClose ?? PnpInterfaceMetadataReader.UnconfirmedClose) is { } unknown)
            { return new OperationResult<HidOpenedSession>.Unknown(unknown); }
            if (Volatile.Read(ref stopping) != 0 || Volatile.Read(ref activeSession) is not null)
            { return SessionFailure("HidSessionStillOwned", "原探测器停止或控制会话尚未关闭"); }
            if (!IsAdmissionCurrent(admission, expectedIdentity) || path.Path != expectedIdentity.InterfacePath)
            { return SessionFailure("HidAdmissionStale", "选定接口或发现凭据已经失效"); }
            // 不取消驱动中的同步核验任务；返回后才处理取消与资源交接。
            var verified = await Task.Run(() =>
            {
                var result = ProbeCore(path, token, true, out var codec, true, out var openedFile,
                    () => IsAdmissionCurrent(admission, expectedIdentity) && Volatile.Read(ref stopping) == 0, expectedIdentity);
                return (Result: result, Codec: codec, File: openedFile);
            }).ConfigureAwait(false);
            file = verified.File;
            if (verified.Result is OperationResult<DeviceIdentity?>.Unknown uncertain)
            { return new OperationResult<HidOpenedSession>.Unknown(uncertain.Error); }
            if (verified.Result is OperationResult<DeviceIdentity?>.Failure invalid)
            { return new OperationResult<HidOpenedSession>.Failure(invalid.Error); }
            if (verified.Result is OperationResult<DeviceIdentity?>.Cancelled)
            { return new OperationResult<HidOpenedSession>.Cancelled(); }
            if (verified.Result is not OperationResult<DeviceIdentity?>.Success { Value: not null } actual
                || actual.Value != expectedIdentity || file is null || verified.Codec is null)
            { return SessionFailure("HidSessionIdentityChanged", "当前文件身份与选定设备不一致，未交付控制能力"); }
            token.ThrowIfCancellationRequested();
            if (!IsAdmissionCurrent(admission, expectedIdentity) || Volatile.Read(ref stopping) != 0)
            { return SessionFailure("HidAdmissionChanged", "核验后发现凭据已变化或探测器已停止"); }
            var session = new HidOpenedSession(file, actual.Value, verified.Codec, clock, timeoutMilliseconds, reporter,
                () => { ReleasePath(path.Path); Volatile.Write(ref activeSession, null); },
                () => IsAdmissionCurrent(admission, expectedIdentity) && Volatile.Read(ref stopping) == 0);
            Volatile.Write(ref activeSession, session);
            delivered = true;
            return new OperationResult<HidOpenedSession>.Success(session);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new OperationResult<HidOpenedSession>.Cancelled(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "HidSessionOpen");
            return new OperationResult<HidOpenedSession>.Failure(new(report.ErrorId, "HidSessionOpenFailed",
                "HID 控制会话打开失败", "检查接口权限和兼容记录，保留诊断编号"));
        }
        finally
        {
            if (!delivered && file is not null)
            {
                file.Dispose();
                if (file.ReleaseConfirmed) { ReleasePath(path.Path); }
                else
                {
                    retainedHandle = file;
                    var report = reporter.Report(file.ReleaseError ?? new IOException("HID session opening close unconfirmed."), "HidSessionOpenClose");
                    unconfirmedClose = new(report.ErrorId, "HidSessionOpenCloseUnconfirmed",
                        "HID 打开后的关闭未确认，保留路径所有者", "保留诊断，不创建替代探测器或控制会话");
                }
            }
            if (entered) { gate.Release(); }
        }
    }

    private void ReleasePath(string path)
    {
        lock (OwnersSync)
        {
            if (PathOwners.TryGetValue(path, out var owner) && ReferenceEquals(owner, this)) { PathOwners.Remove(path); }
        }
    }

    private static bool IsAdmissionCurrent(DeviceSelectionAdmission admission, DeviceIdentity identity) =>
        admission is not null && admission.Selection is { } selection && admission.Validity is { } validity
        && selection.Interfaces.Contains(identity) && validity.IsCurrent(selection);

    private static OperationResult<HidOpenedSession> SessionFailure(string code, string reason) =>
        new OperationResult<HidOpenedSession>.Failure(AppError.Create(code, reason,
            "核对当前接口和固件契约，不自动重试控制命令"));

    private OperationResult<DeviceIdentity?> ReadIdentity(SafeUsbFileHandle file, DeviceInterfacePath path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var attributes = new HidIdentityNative.Attributes { Size = (uint)Marshal.SizeOf<HidIdentityNative.Attributes>() };
        if (!HidIdentityNative.HidD_GetAttributes(file, ref attributes)) { throw new IOException("HID attributes query failed."); }
        token.ThrowIfCancellationRequested();
        if (!compatibility.Any(item => item.VendorId == attributes.VendorId && item.ProductId == attributes.ProductId))
        {
            return new OperationResult<DeviceIdentity?>.Success(null);
        }
        if (!compatibility.Any(item => item.VendorId == attributes.VendorId && item.ProductId == attributes.ProductId
            && (item.DeviceRevision is null || item.DeviceRevision == attributes.Version)))
        {
            return Failed("HidDeviceRevisionUnverified", "目标设备修订尚未列入兼容记录", "请核实实际设备修订，不将它当作已验证控制能力");
        }
        var matched = compatibility.Single(item => item.VendorId == attributes.VendorId
            && item.ProductId == attributes.ProductId && (item.DeviceRevision is null || item.DeviceRevision == attributes.Version));
        if (matched.Collection is { } expected)
        {
            var collection = ReadCollection(file, token);
            if (unconfirmedClose is { } retained) { return new OperationResult<DeviceIdentity?>.Unknown(retained); }
            if (collection.UsagePage != expected.UsagePage || collection.Usage != expected.Usage)
            { return new OperationResult<DeviceIdentity?>.Success(null); }
            if (collection.InputBytes != expected.InputBytes || collection.OutputBytes != expected.OutputBytes
                || collection.FeatureBytes != expected.FeatureBytes)
            {
                return Failed("HidReportLengthMismatch", "目标 HID collection 的实际报告长度与契约不匹配",
                    "请核对当前固件报告描述符，不使用固定长度继续读写");
            }
        }
        byte[] serialBytes = new byte[512];
        if (!HidIdentityNative.HidD_GetSerialNumberString(file, serialBytes, (uint)serialBytes.Length))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        token.ThrowIfCancellationRequested();
        int end = 0;
        while (end < serialBytes.Length && (serialBytes[end] != 0 || serialBytes[end + 1] != 0)) { end += 2; }
        if (end == serialBytes.Length) { return Failed("HidSerialUnterminated", "设备序列号未完整终止", "请核实 HID 字符串描述符"); }
        string serial = new UnicodeEncoding(false, false, true).GetString(serialBytes, 0, end);
        if (DeviceKey.FromSerialNumber(attributes.VendorId, serial) is not OperationResult<DeviceKey>.Success)
        {
            return Failed("HidSerialUnavailable", "设备没有可用的真实序列号", "请核实设备字符串描述符，不从接口路径推断序列号");
        }
        var metadata = PnpInterfaceMetadataReader.Read(path, reporter, token);
        return metadata switch
        {
            OperationResult<PnpInterfaceMetadata>.Success actual => new OperationResult<DeviceIdentity?>.Success(
                new(attributes.VendorId, attributes.ProductId, serial, actual.Value.ContainerId, actual.Value.Location, path.Path)),
            OperationResult<PnpInterfaceMetadata>.Failure failure => new OperationResult<DeviceIdentity?>.Failure(failure.Error),
            OperationResult<PnpInterfaceMetadata>.Unknown unknown => new OperationResult<DeviceIdentity?>.Unknown(unknown.Error),
            _ => new OperationResult<DeviceIdentity?>.Cancelled()
        };
    }

    private HidIdentityNative.Caps ReadCollection(SafeUsbFileHandle file, CancellationToken token)
    {
        SafeHidPreparsedDataHandle? data = null;
        bool allocated = false;
        try
        {
            token.ThrowIfCancellationRequested();
            bool obtained = HidIdentityNative.HidD_GetPreparsedData(file, out data);
            allocated = data is { IsInvalid: false };
            if (!obtained || !allocated) { throw new IOException("HID preparsed data query failed."); }
            token.ThrowIfCancellationRequested();
            int status = HidIdentityNative.HidP_GetCaps(data!, out var caps);
            if (status != 0x00110000) { throw new ExternalException("HID collection caps query failed.", status); }
            token.ThrowIfCancellationRequested();
            return caps;
        }
        finally
        {
            data?.Dispose();
            if (allocated && data is { ReleaseConfirmed: false })
            {
                retainedPreparsedData = data;
                var report = reporter.Report(data.ReleaseError ?? new IOException("HID caps release unconfirmed."),
                    "HidPreparsedDataClose");
                unconfirmedClose = new(report.ErrorId, "HidPreparsedDataCloseUnconfirmed",
                    "HID collection 元数据释放未确认，探测已停止", "保留原句柄和所有者，不重复读取或创建替代探测器");
            }
        }
    }

    public async Task<OperationResult<Unit>> CancelAndDrainAsync(CancellationToken token)
    {
        bool entered = false;
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false); entered = true;
            // 串行门只在同步元数据调用返回及句柄关闭处理之后释放，不能中断驱动里的同步调用。
            // 这里只等待身份探测；已交付的控制会话由其输入／输出所有者收敛。
            return (unconfirmedClose ?? PnpInterfaceMetadataReader.UnconfirmedClose) is { } unknown
                ? new OperationResult<Unit>.Unknown(unknown) : new OperationResult<Unit>.Success(new());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new OperationResult<Unit>.Cancelled(); }
        finally { if (entered) { gate.Release(); } }
    }
    public Task<OperationResult<Unit>> CloseAsync()
    {
        lock (closeSync)
        {
            Volatile.Write(ref stopping, 1);
            return closing ??= CloseCoreAsync();
        }
    }
    private async Task<OperationResult<Unit>> CloseCoreAsync()
    {
        var drained = await CancelAndDrainAsync(CancellationToken.None).ConfigureAwait(false);
        if (drained is not OperationResult<Unit>.Success) { return drained; }
        if (Volatile.Read(ref activeSession) is { } session)
        {
            var closed = await session.CloseAsync().ConfigureAwait(false);
            if (closed is not OperationResult<Unit>.Success) { return closed; }
        }
        lock (OwnersSync) { Owners.Remove(this); }
        return new OperationResult<Unit>.Success(new());
    }
    public async ValueTask DisposeAsync()
    {
        _ = await CloseAsync().ConfigureAwait(false);
        GC.KeepAlive(retainedHandle);
    }
    private OperationResult<DeviceIdentity?> ReportFailure(Exception exception, bool readFeature = false)
    {
        var report = reporter.Report(exception, readFeature ? "HidV2FeatureProbe" : "HidIdentityProbe");
        return new OperationResult<DeviceIdentity?>.Failure(new(report.ErrorId, "HidIdentityProbeFailed",
            readFeature ? "HID 身份或协议查询失败，未启用控制能力" : "HID 身份读取失败，未把接口作为非目标设备跳过",
            "请检查设备、接口权限与兼容记录后重新检测"));
    }
    private static OperationResult<DeviceIdentity?> Failed(string code, string reason, string action) =>
        new OperationResult<DeviceIdentity?>.Failure(AppError.Create(code, reason, action));
}
