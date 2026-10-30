using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Core.Telemetry;
using UsbDisplayHost.Core.Themes;
using UsbDisplayHost.Core.Transport;
using UsbDisplayHost.Rendering.Images;
using UsbDisplayHost.Windows.Usb;

namespace UsbDisplayHost.Desktop.Services;

// 页面和托盘共用本服务，实际工作仍由唯一协调器和帧边界执行者受理。
public sealed partial class DeviceProtocolService(DeviceCoordinator coordinator, DeviceSessionLifetime sessions,
    DeviceDiscoveryLifetime discovery, IProfileStore profiles, IAssetStore assets, IFrameRenderer renderer,
    MetricSamplingService metrics, IErrorReporter reporter)
{
    private Ad102V2DeviceSession? active;
    private HidOpenedSession? pendingHid;
    private WinUsbDisplayTransport? pendingTransport;
    private Ad102V2ModeController? pendingController;
    private DeviceProfile? pendingSave;
    private bool saveUnconfirmed;
    private AssetReferenceLease? pendingAsset;
    public void DiscardPendingSave() { pendingSave = null; saveUnconfirmed = false; pendingAsset?.Dispose(); pendingAsset = null; }
    public bool HasPendingSave => pendingSave is not null;
    public bool HasPendingResources => pendingHid is not null || pendingTransport is not null || pendingController is not null;

    public async Task<OperationResult<Unit>> QueryStateAsync(CancellationToken token)
    {
        var current = coordinator.Current;
        if (active is not { } source || current.DeviceKey is not { } key
            || current.Connection != ConnectionState.Ready || current.PendingOperationId is not null
            || sessions.IsBusy || HasPendingResources)
        { return Failure("StateQueryUnavailable", "当前会话不能开始状态查询"); }
        var result = current.Mode == DeviceMode.Theme
            ? await sessions.ExecuteProtocolAsync(DeviceAction.QueryState, source.ObserveAsync,
                current.Streaming == StreamingState.Running, false, token)
            : await coordinator.ExecuteAsync(key, current.SessionGeneration, DeviceAction.QueryState, 0,
                new ProtocolControlWork(source.ObserveAsync, source.DrainAsync), token);
        return result is OperationResult<DeviceSnapshot>.Success ? new OperationResult<Unit>.Success(new()) : Convert(result);
    }

    public Task<OperationResult<Unit>> ConnectAsync(DeviceSelectionAdmission admission, CancellationToken token) =>
        ConnectWithPowerIntentAsync(admission, null, token);

    private async Task<OperationResult<Unit>> ConnectWithPowerIntentAsync(DeviceSelectionAdmission admission,
        PowerState? modePowerIntent, CancellationToken token)
    {
        var result = await ConnectCoreAsync(admission, modePowerIntent, token);
        return RecordProtocolResult(result, admission.Selection.Key, "Connect");
    }

    private async Task<OperationResult<Unit>> ConnectCoreAsync(DeviceSelectionAdmission admission,
        PowerState? modePowerIntent, CancellationToken token)
    {
        if (HasPendingResources || sessions.HasRetainedSession)
        { return Failure("ConnectionStillOwned", "原连接资源尚未释放"); }
        try
        {
            var selected = coordinator.Current;
            if (selected.DeviceKey != admission.Selection.Key || selected.Connection != ConnectionState.Discovered)
            { return Failure("SelectionChanged", "选定设备已变化"); }
            // 接口缺失时先返回具体原因，不先打开 HID 并发送状态探测。
            if (GetConnectionInterfaceError(admission.Selection) is { } bindingError)
            { return new OperationResult<Unit>.Failure(bindingError); }
            var probe = discovery.HidProbe ?? throw new InvalidOperationException("HID discovery is unavailable.");
            var hidIdentity = admission.Selection.Interfaces.Single(IsHidInterface);
            var opened = await probe.OpenAd102V2SessionAsync(admission,
                new(Ad102V2DiscoveryConfiguration.HidInterfaceClass, hidIdentity.InterfacePath), hidIdentity,
                TimeProvider.System, 2000, token);
            if (opened is not OperationResult<HidOpenedSession>.Success hid) { return Convert<HidOpenedSession>(opened); }
            pendingHid = hid.Value;
            var capability = Ad102V2SourceProtocol.CreateCapabilities(hid.Value.Codec.Feature);
            if (capability is not OperationResult<DeviceCapabilities>.Success caps) { return Convert<DeviceCapabilities>(capability); }
            var created = Ad102V2ModeController.Create(hid.Value, selected.DeviceKey, selected.SessionGeneration,
                caps.Value, TimeProvider.System, reporter);
            if (created is not OperationResult<Ad102V2ModeController>.Success controller) { return Convert<Ad102V2ModeController>(created); }
            pendingController = controller.Value;
            var statusResult = await controller.Value.QueryStatusAsync(token);
            if (statusResult is not OperationResult<Ad102V2Status>.Success status) { return Convert<Ad102V2Status>(statusResult); }
            bool theme = status.Value.CurrentMode == Ad102V2Mode.Theme;
            if (theme != (hidIdentity.ProductId == 0xADA0) || status.Value.Switching)
            { return Failure("PidModeMismatch", "设备 PID 与 HID 当前模式不一致"); }
            var loaded = await profiles.LoadAsync(selected.DeviceKey, token);
            if (loaded is not OperationResult<ProfileReadResult>.Success saved) { return Convert<ProfileReadResult>(loaded); }
            using var restore = saved.Value;
            if (restore.Error is { } error) { return new OperationResult<Unit>.Failure(error); }
            // 显式切换沿用原会话电源意图，不能用重枚举后的瞬时反馈覆盖它。
            var intent = modePowerIntent ?? (restore.Profile?.PowerIntent == PowerState.Off ? PowerState.Off
                : status.Value.DisplayEnabled ? PowerState.On : PowerState.Off);
            if (theme)
            {
                var data = admission.Selection.Interfaces.Where(identity => identity.ProductId == 0xADA0
                    && identity.InterfacePath != hidIdentity.InterfacePath).ToArray();
                string path = data[0].InterfacePath;
                if (!Guid.TryParse(path[(path.LastIndexOf('#') + 1)..], out var interfaceClass))
                { return Failure("DisplayInterfaceClassMissing", "无法读取 Display 接口类"); }
                var policy = (OperationResult<WinUsbIoPolicy>.Success)WinUsbIoPolicy.Create(2000, 2000, false, Ad102V2SourceProtocol.Evidence);
                pendingTransport = new(admission, WinUsbBinding.ForAd102V2(interfaceClass), policy.Value, reporter);
                var dataOpened = await pendingTransport.OpenAsync(data[0], token);
                if (dataOpened is not OperationResult<TransportLimits>.Success) { return Convert<TransportLimits>(dataOpened); }
                active = new(controller.Value, hidIdentity, caps.Value, pendingTransport, coordinator, reporter) { PowerIntent = intent };
                var work = new ThemeConnectionSessionWork(admission, data[0], selected.SessionGeneration, intent,
                    coordinator, pendingTransport, active, active, active.CreateInitializer(), reporter);
                var connected = await coordinator.ExecuteAsync(selected.DeviceKey, selected.SessionGeneration, DeviceAction.Connect, 0, work, token);
                if (connected is not OperationResult<DeviceSnapshot>.Success) { return Convert<DeviceSnapshot>(connected); }
                var jpeg = (OperationResult<FirmwareJpegProfile>.Success)Ad102V2SourceProtocol.CreateJpegProfile();
                var attached = await sessions.CreateAndAttachAsync(work, new(renderer, profiles, assets,
                    restore.Profile?.Settings ?? DeviceSettings.Default, jpeg.Value, new WpfJpegFrameValidator(reporter),
                    null, metrics.GetSnapshot, TimeProvider.System));
                if (attached is not OperationResult<Unit>.Success) { return attached; }
                pendingTransport = null;
            }
            else
            {
                active = new(controller.Value, hidIdentity, caps.Value, null, coordinator, reporter) { PowerIntent = intent };
                var work = new ProtocolControlWork(active.ObserveAsync, active.DrainAsync);
                var connected = await coordinator.ExecuteAsync(selected.DeviceKey, selected.SessionGeneration, DeviceAction.Connect, 0, work, token);
                if (connected is not OperationResult<DeviceSnapshot>.Success) { return Convert<DeviceSnapshot>(connected); }
            }
            var control = sessions.AttachControl(controller.Value);
            if (control is not OperationResult<Unit>.Success) { return control; }
            pendingHid = null; pendingController = null;
            if (theme && intent == PowerState.On)
            {
                var applied = await sessions.ApplyThemeAsync(selected.DeviceKey, selected.SessionGeneration,
                    new(selected.DeviceKey, 0, restore.Profile?.Content ?? ThemeContent.Default), metrics.GetSnapshot(), token);
                if (applied.Session is not OperationResult<DeviceSnapshot>.Success) { return Convert<DeviceSnapshot>(applied.Session); }
            }
            return new OperationResult<Unit>.Success(new());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new OperationResult<Unit>.Cancelled(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "SourceProtocolConnect");
            return new OperationResult<Unit>.Failure(new(report.ErrorId, "ProtocolConnectionFailed", "设备连接未完成", "查看诊断并重新检测设备"));
        }
        finally
        {
            if (HasPendingResources)
            {
                var closed = await ClosePendingAsync();
                if (closed is not OperationResult<Unit>.Success)
                { coordinator.NotifyDisconnected(admission.Selection.Key, coordinator.Current.SessionGeneration); }
            }
        }
    }

    public async Task<OperationResult<Unit>> SetBrightnessAsync(int value, CancellationToken token)
    {
        if (value is < 0 or > 100 || active is null || pendingSave is not null) { return Failure("BrightnessUnavailable", "亮度无效或上次保存尚待处理"); }
        bool resume = coordinator.Current.Streaming == StreamingState.Running;
        var result = await sessions.ExecuteProtocolAsync(DeviceAction.SetBrightness, async (context, cancellation) =>
        {
            var written = await active.SendCommandAsync(AfDisplayCommand.Backlight, (byte)value, context, cancellation);
            if (written is not OperationResult<Unit>.Success) { return Ad102V2DeviceSession.Convert<DeviceObservation, Unit>(written); }
            var observed = await active.ObserveAsync(context, cancellation);
            if (observed is OperationResult<DeviceObservation>.Success actual && actual.Value.ObservedBrightness != value)
            { return new OperationResult<DeviceObservation>.Failure(AppError.Create("BrightnessMismatch", "亮度读回与请求不同", "重新查询实际亮度")); }
            return observed;
        }, resume, false, token, value);
        if (result is not OperationResult<DeviceSnapshot>.Success confirmed) { return Convert<DeviceSnapshot>(result); }
        return await SaveControlStateAsync(confirmed.Value, token);
    }

    public async Task<OperationResult<Unit>> SetPowerAsync(bool on, CancellationToken token)
    {
        if (active is null || pendingSave is not null) { return Failure("PowerUnavailable", "会话不可用或上次保存尚待处理"); }
        var result = await sessions.ExecuteProtocolAsync(DeviceAction.SetPower, async (context, cancellation) =>
        {
            active.PowerIntent = on ? PowerState.On : PowerState.Off;
            var written = await active.SendCommandAsync(AfDisplayCommand.Power, on ? (byte)1 : (byte)0, context, cancellation);
            if (written is not OperationResult<Unit>.Success) { return Ad102V2DeviceSession.Convert<DeviceObservation, Unit>(written); }
            var observed = await active.ObserveAsync(context, cancellation);
            return observed is OperationResult<DeviceObservation>.Success actual && actual.Value.Power != active.PowerIntent
                ? new OperationResult<DeviceObservation>.Unknown(AppError.Create("PowerUnconfirmed", "电源状态未匹配请求", "重新核实状态")) : observed;
        }, on, on, token);
        if (result is not OperationResult<DeviceSnapshot>.Success confirmed) { return Convert<DeviceSnapshot>(result); }
        var saved = await SaveControlStateAsync(confirmed.Value, token);
        if (on && saved is OperationResult<Unit>.Success)
        {
            var current = coordinator.CurrentActiveProfile;
            var stored = await profiles.LoadAsync(confirmed.Value.DeviceKey!, token);
            if (stored is not OperationResult<ProfileReadResult>.Success restore) { return Convert<ProfileReadResult>(stored); }
            using var reference = restore.Value;
            var applied = await sessions.ApplyThemeAsync(confirmed.Value.DeviceKey!, confirmed.Value.SessionGeneration,
                new(confirmed.Value.DeviceKey, current?.Context.ContentRevision ?? 0, current?.Content ?? reference.Profile?.Content ?? ThemeContent.Default),
                metrics.GetSnapshot(), token);
            if (applied.Session is not OperationResult<DeviceSnapshot>.Success) { return Convert<DeviceSnapshot>(applied.Session); }
        }
        return saved;
    }

    private async Task<OperationResult<Unit>> SaveControlStateAsync(DeviceSnapshot snapshot, CancellationToken token)
    {
        var current = coordinator.CurrentActiveProfile;
        pendingSave = new(snapshot.DeviceKey!, current?.Content ?? ThemeContent.Default,
            current?.Settings ?? DeviceSettings.Default, snapshot.ObservedBrightness, snapshot.Power);
        if (pendingSave.Content is ThemeContent.Image { AssetId: { } currentAsset })
        {
            var retained = assets.AcquireReference(currentAsset, AssetReferenceOwner.PendingRestore);
            if (retained is not OperationResult<AssetReferenceLease>.Success lease) { return Convert(retained); }
            pendingAsset = lease.Value;
        }
        var loaded = await profiles.LoadAsync(snapshot.DeviceKey!, token);
        if (loaded is not OperationResult<ProfileReadResult>.Success stored) { return Convert<ProfileReadResult>(loaded); }
        using var reference = stored.Value;
        pendingSave = new(snapshot.DeviceKey!, current?.Content ?? reference.Profile?.Content ?? ThemeContent.Default,
            current?.Settings ?? reference.Profile?.Settings ?? DeviceSettings.Default, snapshot.ObservedBrightness, snapshot.Power);
        if (pendingAsset is null && pendingSave.Content is ThemeContent.Image { AssetId: { } assetId })
        {
            var retained = assets.AcquireReference(assetId, AssetReferenceOwner.PendingRestore);
            if (retained is not OperationResult<AssetReferenceLease>.Success lease) { return Convert(retained); }
            pendingAsset = lease.Value;
        }
        return await RetrySaveAsync(token);
    }

    public async Task<OperationResult<Unit>> RetrySaveAsync(CancellationToken token)
    {
        if (pendingSave is not { } candidate) { return new OperationResult<Unit>.Success(new()); }
        // 未知落盘先读回原候选；不重复操作设备。
        var loaded = await profiles.LoadAsync(candidate.DeviceKey, token);
        if (loaded is not OperationResult<ProfileReadResult>.Success stored) { return Convert<ProfileReadResult>(loaded); }
        using var reference = stored.Value;
        if (reference.Recovery == ProfileRecovery.Primary && reference.Profile is { } actual
            && actual.DeviceKey == candidate.DeviceKey && actual.ConfirmedBrightness == candidate.ConfirmedBrightness
            && actual.PowerIntent == candidate.PowerIntent)
        { DiscardPendingSave(); return new OperationResult<Unit>.Success(new()); }
        if (saveUnconfirmed)
        { return new OperationResult<Unit>.Unknown(AppError.Create("ControlSaveStillUnconfirmed",
            "主文件未确认本次设置", "继续核实或明确放弃本次保存结果，不重发设备命令")); }
        var attempted = await profiles.SaveControlAsync(candidate, token);
        if (attempted is not OperationResult<ProfileApplicationSaveAttempt>.Success receipt) { return Convert(attempted); }
        pendingSave = receipt.Value.Candidate;
        saveUnconfirmed = receipt.Value.Write is OperationResult<Unit>.Unknown;
        if (receipt.Value.Write is OperationResult<Unit>.Success) { DiscardPendingSave(); }
        return receipt.Value.Write;
    }

    public async Task<OperationResult<Unit>> ClosePendingAsync()
    {
        if (pendingController is not null)
        {
            var closed = await pendingController.CloseAsync();
            if (closed is not OperationResult<Unit>.Success) { return closed; }
            pendingController = null; pendingHid = null;
        }
        if (pendingHid is not null)
        {
            var closed = await pendingHid.CloseAsync();
            if (closed is not OperationResult<Unit>.Success) { return closed; }
            pendingHid = null;
        }
        if (pendingTransport is not null)
        {
            var drained = await pendingTransport.CancelAndDrainAsync(CancellationToken.None);
            if (drained is not OperationResult<Unit>.Success) { return drained; }
            await pendingTransport.DisposeAsync(); pendingTransport = null;
        }
        return new OperationResult<Unit>.Success(new());
    }

    private static OperationResult<Unit> Convert<T>(OperationResult<T> result) => Ad102V2DeviceSession.Convert<Unit, T>(result);

    private static bool IsHidInterface(DeviceIdentity identity) => identity.InterfacePath.EndsWith(
        $"#{{{Ad102V2DiscoveryConfiguration.HidInterfaceClass:D}}}", StringComparison.OrdinalIgnoreCase);

    private static AppError? GetConnectionInterfaceError(DeviceSelection selection)
    {
        var controls = selection.Interfaces.Where(IsHidInterface).ToArray();
        if (controls.Length != 1)
        {
            return AppError.Create(controls.Length == 0 ? "HidInterfaceMissing" : "HidInterfaceAmbiguous",
                controls.Length == 0 ? "未找到当前设备的 V2 HID 控制接口" : "同一设备发现多个 V2 HID 控制接口",
                "请等待设备枚举完成，核对 HID 驱动与接口归属后重新检测");
        }
        if (controls[0].ProductId != 0xADA0) { return null; }
        int count = selection.Interfaces.Count(identity => identity.ProductId == 0xADA0 && !IsHidInterface(identity));
        return count switch
        {
            1 => null,
            0 => AppError.Create("DisplayBindingMissing", "已发现主题设备，但未发现可用的 Display WinUSB 数据接口",
                "请在设备管理器确认主题设备的 Display 接口使用 WinUSB，且已注册 DeviceInterfaceGUID 或 DeviceInterfaceGUIDs，然后重新检测"),
            _ => AppError.Create("DisplayBindingAmbiguous", "同一主题设备发现多个 Display 数据接口，无法确定发送目标",
                "请核对 Display 接口的驱动绑定和接口 GUID，排除重复注册后重新检测")
        };
    }

    private OperationResult<Unit> RecordProtocolResult(OperationResult<Unit> result, DeviceKey key, string operation)
    {
        if (reporter is IDiagnosticService diagnostics)
        {
            AppError? error = result switch
            {
                OperationResult<Unit>.Failure failure => failure.Error,
                OperationResult<Unit>.Unknown unknown => unknown.Error,
                _ => null
            };
            var current = coordinator.Current;
            diagnostics.Write(error is null ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning,
                $"DeviceProtocol.{operation}", error?.Code ?? (result is OperationResult<Unit>.Success ? "Completed" : "Cancelled"),
                new(DiagnosticContext.Anonymize(key), current.SessionGeneration, current.PendingOperationId,
                    $"{current.Connection}.{current.Mode}.{current.Power}.{current.Streaming}"), error?.ErrorId);
        }
        return result;
    }

    private static OperationResult<Unit> Failure(string code, string reason) =>
        new OperationResult<Unit>.Failure(AppError.Create(code, reason, "核对设备状态和诊断后重试"));
}
