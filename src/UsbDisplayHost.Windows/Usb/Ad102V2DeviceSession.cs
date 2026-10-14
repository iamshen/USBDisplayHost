using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Core.Transport;

namespace UsbDisplayHost.Windows.Usb;

// 同一设备的 Display 与 HID 组合。调用方通过帧边界调度器串行执行控制。
public sealed class Ad102V2DeviceSession(Ad102V2ModeController controller, DeviceIdentity identity,
    DeviceCapabilities capabilities, WinUsbDisplayTransport? transport, DeviceCoordinator coordinator,
    IErrorReporter reporter) : IApplicationSessionState, IFrameParserRecovery
{
    public Ad102V2ModeController Controller => controller;
    public DeviceCapabilities Capabilities => capabilities;
    public WinUsbDisplayTransport? Transport => transport;
    public PowerState PowerIntent { get; set; } = PowerState.Unknown;
    public async Task<OperationResult<Unit>> PrepareConnectionAsync(OperationContext context, CancellationToken token)
    {
        if (PowerIntent != PowerState.Off) { return new OperationResult<Unit>.Success(new()); }
        return await SendCommandAsync(AfDisplayCommand.Power, 0, context, token).ConfigureAwait(false);
    }
    public ThemeDataPlaneInitializer CreateInitializer()
    {
        if (transport?.InterfaceNumber is not { } number) { throw new InvalidOperationException("Display is not open."); }
        var contract = (OperationResult<ThemeDataPlaneContract>.Success)Ad102V2SourceProtocol.CreateThemeContract(
            capabilities.FirmwareVersion, number);
        return new(transport, capabilities, contract.Value, IsCurrent, reporter);
    }

    private bool IsCurrent(OperationContext context) => coordinator.IsCurrent(context)
        || coordinator.CurrentActiveProfile is { } active && active.Context == context
            && coordinator.ResolveFrameOperation(new(context.DeviceKey, context.SessionGeneration, context.OperationId, active.PipelineRevision))
                is OperationResult<OperationContext>.Success;

    public async Task<OperationResult<DeviceObservation>> ObserveAsync(OperationContext context, CancellationToken token)
    {
        if (!IsCurrent(context)) { return Fail<DeviceObservation>("SessionExpired", "设备会话已变化"); }
        var queried = await controller.QueryStatusAsync(token).ConfigureAwait(false);
        if (queried is not OperationResult<Ad102V2Status>.Success status) { return Convert<DeviceObservation, Ad102V2Status>(queried); }
        var value = status.Value;
        DeviceMode mode = value.CurrentMode switch
        { Ad102V2Mode.Theme => DeviceMode.Theme, Ad102V2Mode.Screen => DeviceMode.SecondaryDisplay, _ => DeviceMode.Unknown };
        if (value.Switching || value.CurrentMode != value.TargetMode || mode == DeviceMode.Unknown || value.RawBacklight > 100)
        { return Fail<DeviceObservation>("DeviceStateUnstable", "设备模式或亮度状态尚未稳定"); }
        if (!IsCurrent(context)) { return Fail<DeviceObservation>("SessionExpired", "状态读取期间设备会话已变化"); }
        int? brightness = mode == DeviceMode.Theme ? value.RawBacklight : null;
        if (mode == DeviceMode.Theme && transport?.InterfaceNumber is { } number)
        {
            var read = await transport.ControlTransferAsync(Ad102V2SourceProtocol.CreateBacklightQuery(number),
                ReadOnlyMemory<byte>.Empty, 1, token).ConfigureAwait(false);
            if (read is not OperationResult<ReadOnlyMemory<byte>>.Success backlight)
            { return Convert<DeviceObservation, ReadOnlyMemory<byte>>(read); }
            if (backlight.Value.Length != 1 || backlight.Value.Span[0] > 100 || backlight.Value.Span[0] != value.RawBacklight)
            { return Fail<DeviceObservation>("BacklightReadbackMismatch", "EP0 与 HID 亮度读回不一致"); }
            brightness = backlight.Value.Span[0];
        }
        // SCREEN 的显示使能字段不表示 Windows 桌面路径；只连接 HID 控制，不宣称桌面输出就绪。
        var power = value.DisplayEnabled ? PowerState.On : PowerState.Off;
        var current = coordinator.Current;
        var streaming = mode != DeviceMode.Theme || power == PowerState.Off ? StreamingState.Stopped
            : current.Streaming is StreamingState.Running or StreamingState.Paused ? current.Streaming : StreamingState.Stopped;
        return new OperationResult<DeviceObservation>.Success(new(context.DeviceKey, identity, capabilities,
            mode, power, streaming, brightness, true, false));
    }

    public async Task<OperationResult<Unit>> SendCommandAsync(AfDisplayCommand command, byte value, OperationContext context, CancellationToken token)
    {
        if (transport is null || !IsCurrent(context)) { return Fail<Unit>("DisplayUnavailable", "当前没有可用的主题 Display 会话"); }
        byte[] bytes = new byte[4];
        var encoded = AfCommandEncoder.TryWrite(command, value, bytes);
        if (encoded is not OperationResult<int>.Success) { return Convert<Unit, int>(encoded); }
        var before = await controller.QueryStatusAsync(token).ConfigureAwait(false);
        if (before is not OperationResult<Ad102V2Status>.Success baseline) { return Convert<Unit, Ad102V2Status>(before); }
        if (baseline.Value.CurrentMode != Ad102V2Mode.Theme || baseline.Value.Switching || baseline.Value.MaintenanceBusy)
        { return Fail<Unit>("ThemeControlUnavailable", "当前模式或维护状态不允许主题控制"); }
        var write = await transport.WriteAsync(bytes, token).ConfigureAwait(false);
        if (!write.IsComplete || write.RequestedBytes != bytes.Length)
        { return new OperationResult<Unit>.Unknown(AppError.Create("DisplayCommandIncomplete", "显示命令写入未确认", "停流并核实设备，不重复发送")); }
        long start = TimeProvider.System.GetTimestamp();
        do
        {
            if (!IsCurrent(context)) { return Fail<Unit>("SessionExpired", "命令确认期间设备会话已变化"); }
            var read = await controller.QueryStatusAsync(token).ConfigureAwait(false);
            if (read is not OperationResult<Ad102V2Status>.Success observed) { return Convert<Unit, Ad102V2Status>(read); }
            var actual = observed.Value;
            if ((byte)command >= 0x30)
            {
                if (actual.DisplayCommandCount == unchecked((ushort)(baseline.Value.DisplayCommandCount + 1))
                    && actual.LastDisplayCommand == (byte)command)
                {
                    return actual.LastDisplayResult == 1 ? new OperationResult<Unit>.Success(new())
                        : Fail<Unit>("DisplayCommandRejected", "固件拒绝了显示命令");
                }
            }
            else if (command == AfDisplayCommand.Backlight && actual.RawBacklight == value)
            { return new OperationResult<Unit>.Success(new()); }
            await Task.Delay(50, token).ConfigureAwait(false);
        } while (TimeProvider.System.GetElapsedTime(start) < TimeSpan.FromSeconds(2));
        return new OperationResult<Unit>.Unknown(AppError.Create("DisplayCommandUnconfirmed", "显示命令执行结果待确认", "停流并重新核实设备状态"));
    }

    public async Task<OperationResult<FrameParserRecoveryReceipt>> RecoverAsync(OperationContext context, CancellationToken token)
    {
        var before = await ObserveAsync(context, token).ConfigureAwait(false);
        if (before is not OperationResult<DeviceObservation>.Success observed) { return Convert<FrameParserRecoveryReceipt, DeviceObservation>(before); }
        var cleared = await SendCommandAsync(AfDisplayCommand.ClearParser, 0, context, token).ConfigureAwait(false);
        if (cleared is not OperationResult<Unit>.Success) { return Convert<FrameParserRecoveryReceipt, Unit>(cleared); }
        // 内容已在主机旋转；清除固件上次会话留下的旋转，避免二次旋转。
        var rotated = await SendCommandAsync(AfDisplayCommand.Rotation, 0, context, token).ConfigureAwait(false);
        if (rotated is not OperationResult<Unit>.Success) { return Convert<FrameParserRecoveryReceipt, Unit>(rotated); }
        var receipt = new FrameParserRecoveryReceipt(context.DeviceKey, context.SessionGeneration, context.OperationId,
            capabilities.FirmwareVersion, Ad102V2SourceProtocol.Evidence, true, true);
        if (PowerIntent == PowerState.On)
        {
            var initialized = await CreateInitializer().InitializeAsync(context, PowerState.On,
                observed.Value.ObservedBrightness ?? 0, receipt, token).ConfigureAwait(false);
            if (initialized is not OperationResult<ThemeInitializationReceipt>.Success)
            { return Convert<FrameParserRecoveryReceipt, ThemeInitializationReceipt>(initialized); }
        }
        var power = await SendCommandAsync(AfDisplayCommand.Power, PowerIntent == PowerState.On ? (byte)1 : (byte)0, context, token).ConfigureAwait(false);
        if (power is not OperationResult<Unit>.Success) { return Convert<FrameParserRecoveryReceipt, Unit>(power); }
        var after = await ObserveAsync(context, token).ConfigureAwait(false);
        if (after is not OperationResult<DeviceObservation>.Success confirmed || confirmed.Value.Power != PowerIntent)
        { return Fail<FrameParserRecoveryReceipt>("RecoveryPowerUnconfirmed", "清理后的电源意图未确认"); }
        return new OperationResult<FrameParserRecoveryReceipt>.Success(receipt);
    }

    public Task<OperationResult<Unit>> DrainAsync() => transport is null
        ? Task.FromResult<OperationResult<Unit>>(new OperationResult<Unit>.Success(new()))
        : transport.CancelAndDrainAsync(CancellationToken.None);
    public Task<OperationResult<Unit>> CloseAsync() => controller.CloseAsync();
    public static OperationResult<T> Convert<T, U>(OperationResult<U> value) => value switch
    {
        OperationResult<U>.Failure failure => new OperationResult<T>.Failure(failure.Error),
        OperationResult<U>.Unknown unknown => new OperationResult<T>.Unknown(unknown.Error),
        _ => new OperationResult<T>.Cancelled()
    };
    private static OperationResult<T> Fail<T>(string code, string reason) =>
        new OperationResult<T>.Failure(AppError.Create(code, reason, "核对连接与诊断后重试"));
}
