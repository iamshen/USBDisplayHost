using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Windows.Usb;

namespace UsbDisplayHost.Desktop.Services;

public sealed partial class DeviceProtocolService
{
    public async Task<OperationResult<Unit>> SwitchModeAsync(DeviceMode target, CancellationToken token)
    {
        var key = coordinator.Current.DeviceKey;
        var result = await SwitchModeCoreAsync(target, token);
        return key is null ? result : RecordProtocolResult(result, key, "SwitchMode");
    }

    private async Task<OperationResult<Unit>> SwitchModeCoreAsync(DeviceMode target, CancellationToken token)
    {
        var before = coordinator.Current;
        if (active is not { } source || before.DeviceKey is not { } key || before.Mode == target
            || target is not (DeviceMode.Theme or DeviceMode.SecondaryDisplay) || pendingSave is not null)
        { return Failure("ModeUnavailable", "当前设备不能开始该模式切换"); }
        // 当前 usb_mode_switch.c 无条件调用 set_power(1)，主机无法在重枚举期间保证 Off。
        if (source.PowerIntent == PowerState.Off)
        { return Failure("FirmwareOffModeUnsupported", "当前固件切换会亮屏，请先明确唤醒再切换模式"); }
        OperationResult<Ad102V2ModeAcknowledgement>? acknowledgement = null;
        long started = TimeProvider.System.GetTimestamp();
        async Task<OperationResult<DeviceObservation>> SendAsync(OperationContext context, CancellationToken cancellation)
        {
            if (source.Transport is { } transport)
            {
                var drained = await transport.CancelAndDrainAsync(cancellation);
                if (drained is not OperationResult<Unit>.Success) { return Ad102V2DeviceSession.Convert<DeviceObservation, Unit>(drained); }
                await transport.DisposeAsync();
            }
            acknowledgement = await source.Controller.SwitchAsync(context, target, source.PowerIntent, cancellation);
            if (acknowledgement is OperationResult<Ad102V2ModeAcknowledgement>.Failure
                or OperationResult<Ad102V2ModeAcknowledgement>.Cancelled)
            { return Ad102V2DeviceSession.Convert<DeviceObservation, Ad102V2ModeAcknowledgement>(acknowledgement); }
            // 原 PID 的受理不是目标连接完成；旧会话结束后重新枚举同一序列号。
            return new OperationResult<DeviceObservation>.Unknown(AppError.Create("ModeTargetPending",
                "模式请求已处理，等待目标设备重新枚举", "核实同一设备的目标接口，不重复发送切换命令"));
        }
        var sent = before.Mode == DeviceMode.Theme
            ? await sessions.ExecuteProtocolAsync(DeviceAction.SwitchMode, SendAsync, false, false, token)
            : await coordinator.ExecuteAsync(key, before.SessionGeneration, DeviceAction.SwitchMode, 0,
                new ProtocolControlWork(SendAsync, source.DrainAsync), token);
        // 未进入模式发送时保留实际失败，例如在途传输未收敛，不覆盖成“模式请求未发送”。
        if (acknowledgement is null) { return Convert(sent); }
        coordinator.NotifyDisconnected(key, before.SessionGeneration);
        var closed = await sessions.CloseDisconnectedAsync(key, before.SessionGeneration);
        if (closed is not OperationResult<Unit>.Success) { return closed; }
        active = null;
        if (acknowledgement is OperationResult<Ad102V2ModeAcknowledgement>.Success rejected
            && rejected.Value.Result != Ad102V2HidResult.Accepted)
        { return Failure("ModeRejected", "固件拒绝切换请求，原会话已释放，请重新检测"); }
        if (acknowledgement is OperationResult<Ad102V2ModeAcknowledgement>.Failure
            or OperationResult<Ad102V2ModeAcknowledgement>.Cancelled)
        { return Convert(acknowledgement); }

        return await WaitForConnectionAsync(key, target, started, token);
    }

    public async Task<OperationResult<Unit>> ReconnectAsync(DeviceKey key, long oldGeneration, CancellationToken token)
    {
        var result = await ReconnectCoreAsync(key, oldGeneration, token);
        return RecordProtocolResult(result, key, "Reconnect");
    }

    private async Task<OperationResult<Unit>> ReconnectCoreAsync(DeviceKey key, long oldGeneration, CancellationToken token)
    {
        var closed = await sessions.CloseDisconnectedAsync(key, oldGeneration);
        if (closed is not OperationResult<Unit>.Success) { return closed; }
        if (pendingSave is not null) { return Failure("ControlSavePending", "上次控制设置尚待保存，请先处理本地结果"); }
        active = null;
        return await WaitForConnectionAsync(key, null, TimeProvider.System.GetTimestamp(), token);
    }

    private async Task<OperationResult<Unit>> WaitForConnectionAsync(DeviceKey key, DeviceMode? target, long started, CancellationToken token)
    {
        bool originalPresent = false;
        AppError? interfaceError = null;
        TimeSpan budget = TimeSpan.FromSeconds(target is null ? 30 : 8);
        while (TimeProvider.System.GetElapsedTime(started) < budget)
        {
            token.ThrowIfCancellationRequested();
            var restarted = await discovery.StartAsync(token);
            if (restarted is not OperationResult<Unit>.Success) { return restarted; }
            var catalog = discovery.Session!;
            while ((catalog.Current.Version == 0 || catalog.Current.IsScanning) && TimeProvider.System.GetElapsedTime(started) < budget)
            { await Task.Delay(100, token); }
            var inventory = catalog.Current;
            originalPresent = false;
            interfaceError = null;
            if (inventory.CanSelect && catalog.Select(inventory.Version, key) is OperationResult<DeviceSelectionAdmission>.Success selected)
            {
                ushort expectedPid = target is null ? selected.Value.Selection.Interfaces[0].ProductId
                    : target == DeviceMode.Theme ? (ushort)0xADA0 : (ushort)0xAD11;
                var identities = selected.Value.Selection.Interfaces;
                bool isTarget = identities.All(identity => identity.ProductId == expectedPid);
                originalPresent = !isTarget;
                interfaceError = GetConnectionInterfaceError(selected.Value.Selection);
                if (isTarget && interfaceError is null)
                {
                    var admitted = await coordinator.SelectAsync(selected.Value, token);
                    if (admitted is not OperationResult<DeviceSnapshot>.Success) { return Convert(admitted); }
                    var connected = await ConnectAsync(selected.Value, token);
                    if (connected is not OperationResult<Unit>.Success) { return connected; }
                    return target is null || coordinator.Current.Mode == target ? connected : Failure("ModeTargetMismatch", "目标连接后实际模式不一致");
                }
            }
            await Task.Delay(250, token);
        }
        if (target is { } expected) { return await RequeryModeOutcomeAsync(key, expected, token); }
        if (interfaceError is not null) { return new OperationResult<Unit>.Unknown(interfaceError); }
        return new OperationResult<Unit>.Unknown(AppError.Create("ModeTargetTimeout",
            originalPresent ? "仍发现原模式，未确认目标模式完成" : "目标接口或驱动尚未就绪，切换结果待确认",
            "重新检测同一设备，不盲目重发或反向切换"));
    }

    private async Task<OperationResult<Unit>> RequeryModeOutcomeAsync(DeviceKey key, DeviceMode expected, CancellationToken token)
    {
        // 等待期结束后仅重新枚举和核实，不重发模式命令，也不切回原模式。
        var restarted = await discovery.StartAsync(token);
        if (restarted is not OperationResult<Unit>.Success) { return restarted; }
        var catalog = discovery.Session!;
        long started = TimeProvider.System.GetTimestamp();
        while (catalog.Current.Version == 0 || catalog.Current.IsScanning)
        {
            if (TimeProvider.System.GetElapsedTime(started) >= TimeSpan.FromSeconds(2))
            { return ModeUnknown("超时后的设备检测尚未完成"); }
            await Task.Delay(100, token);
        }
        var inventory = catalog.Current;
        if (!inventory.CanSelect || catalog.Select(inventory.Version, key) is not OperationResult<DeviceSelectionAdmission>.Success selected)
        { return ModeUnknown("未能无歧义关联同一设备"); }
        var pids = selected.Value.Selection.Interfaces.Select(identity => identity.ProductId).Distinct().ToArray();
        if (pids.Length != 1 || pids[0] is not (0xADA0 or 0xAD11))
        { return ModeUnknown("设备模式接口尚未稳定"); }
        // 目标 PID 出现不等于主题数据面可用；不要先选中残缺接口再打开 HID。
        if (GetConnectionInterfaceError(selected.Value.Selection) is { } bindingError)
        { return new OperationResult<Unit>.Unknown(bindingError); }
        var admitted = await coordinator.SelectAsync(selected.Value, token);
        if (admitted is not OperationResult<DeviceSnapshot>.Success) { return Convert(admitted); }
        var connected = await ConnectAsync(selected.Value, token);
        if (connected is not OperationResult<Unit>.Success)
        {
            AppError? cause = connected switch
            {
                OperationResult<Unit>.Failure failure => failure.Error,
                OperationResult<Unit>.Unknown unknown => unknown.Error,
                _ => null
            };
            return cause is null ? new OperationResult<Unit>.Cancelled() : new OperationResult<Unit>.Unknown(cause);
        }
        return coordinator.Current.Mode == expected ? connected
            : Failure("ModeNotCompleted", "重新连接并核实原模式仍可用，切换未完成");
    }

    private static OperationResult<Unit> ModeUnknown(string reason) => new OperationResult<Unit>.Unknown(
        AppError.Create("ModeOutcomeUnconfirmed", reason, "重新检测同一设备，不盲目重发或反向切换"));
}
