using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Sessions;

public enum DeviceAction { ApplyContent, SetBrightness, SwitchMode, SetPower, SaveDraft, Connect, ApplySettings, Pause, Resume, Recover, QueryState }

public static class OperationAvailability
{
    public static OperationResult<Unit> Evaluate(DeviceSnapshot snapshot, DeviceAction action)
    {
        if (!Enum.IsDefined(action))
        {
            return Reject("InvalidAction", "操作无效", "请选择可用操作");
        }

        if (action == DeviceAction.SaveDraft)
        {
            return new OperationResult<Unit>.Success(default);
        }

        if (snapshot.PendingOperationId is not null)
        {
            return Reject("DeviceBusy", "设备正在处理另一项操作", "请等待当前操作完成");
        }

        if (action == DeviceAction.Connect)
        {
            return snapshot.Connection == ConnectionState.Discovered && snapshot.DeviceKey is not null
                ? new OperationResult<Unit>.Success(default)
                : Reject("DeviceNotDiscovered", "选定设备尚未进入可连接状态", "请先重新检测并选择设备");
        }
        if (action == DeviceAction.Recover)
        {
            return snapshot.Connection == ConnectionState.Faulted && snapshot.DeviceKey is not null
                ? new OperationResult<Unit>.Success(default)
                : Reject("RecoveryUnavailable", "设备当前不能启动恢复", "请检查连接或重新检测设备");
        }

        if (snapshot.Connection != ConnectionState.Ready || snapshot.DeviceKey is null)
        {
            return Reject("DeviceNotReady", "选定设备尚未就绪", "请检查连接与驱动后重试");
        }

        if (snapshot.Capabilities is not { IsProtocolMatched: true } capabilities)
        {
            return Reject("CapabilitiesUnverified", "设备能力尚未确认", "请核对固件和兼容记录");
        }

        return action switch
        {
            DeviceAction.ApplyContent or DeviceAction.ApplySettings when snapshot.Mode != DeviceMode.Theme
                || snapshot.Power != PowerState.On || snapshot.Streaming is StreamingState.Paused or StreamingState.Faulted =>
                Reject("ThemeUnavailable", "设备当前不能应用主题", "请先进入主题模式并唤醒、恢复推流，或保存草稿"),
            DeviceAction.ApplyContent or DeviceAction.ApplySettings when capabilities.Width != 1920 || capabilities.Height != 1080 =>
                Reject("ResolutionMismatch", "设备分辨率与兼容基线不一致", "请核对样机描述符，不能自动改分辨率"),
            DeviceAction.ApplyContent or DeviceAction.ApplySettings => new OperationResult<Unit>.Success(default),
            DeviceAction.Pause when snapshot.Mode == DeviceMode.Theme && snapshot.Streaming == StreamingState.Running =>
                new OperationResult<Unit>.Success(default),
            DeviceAction.Resume when snapshot.Mode == DeviceMode.Theme && snapshot.Power == PowerState.On
                && snapshot.Streaming == StreamingState.Paused => new OperationResult<Unit>.Success(default),
            DeviceAction.Pause or DeviceAction.Resume => Reject("StreamingActionUnavailable", "当前推流状态不允许该操作", "请核对模式、电源和推流状态"),
            DeviceAction.SetBrightness => RequireCommand(capabilities, DeviceCommand.SetBrightness),
            DeviceAction.SwitchMode => RequireCommand(capabilities, DeviceCommand.SwitchMode),
            DeviceAction.SetPower => RequireCommand(capabilities, DeviceCommand.SetPower),
            DeviceAction.QueryState => RequireCommand(capabilities, DeviceCommand.QueryState),
            _ => Reject("InvalidAction", "操作无效", "请选择可用操作")
        };
    }

    private static OperationResult<Unit> RequireCommand(DeviceCapabilities capabilities, DeviceCommand command) =>
        capabilities.Commands.Contains(command)
            ? new OperationResult<Unit>.Success(default)
            : Reject("UnsupportedCommand", "当前固件未确认支持此操作", "请使用已确认的操作或核对固件版本");

    private static OperationResult<Unit> Reject(string code, string reason, string action) =>
        new OperationResult<Unit>.Failure(AppError.Create(code, reason, action));
}
