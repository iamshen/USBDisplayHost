using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Desktop.Services;

namespace UsbDisplayHost.Desktop.ViewModels;

public sealed partial class DevicesPageViewModel
{
    private DeviceProtocolService? protocol;
    private Func<CancellationToken, Task<OperationResult<Unit>>>? beginDiscovery;
    private CancellationTokenSource? protocolCancellation;
    private Task? reconnecting;
    private Func<CancellationToken, Task<OperationResult<Unit>>>? confirmModeSwitch;
    public void AttachModeSwitchConfirmation(Func<CancellationToken, Task<OperationResult<Unit>>> confirm) => confirmModeSwitch = confirm;
    private CancellationToken ProtocolToken => protocolCancellation?.Token ?? lifetime.Token;
    [RelayCommand]
    private void CancelProtocol() => protocolCancellation?.Cancel();
    public Task ReconnectProtocolAsync(DeviceKey key, long generation) => protocol is null || stopping || IsProtocolBusy
        ? Task.CompletedTask : reconnecting = RunProtocolAsync(() => protocol.ReconnectAsync(key, generation, ProtocolToken), "原设备已重新连接");
    [ObservableProperty] private bool isProtocolBusy;
    partial void OnIsProtocolBusyChanged(bool value) { RefreshProtocolActions(); RefreshStreamingActions(); }
    [ObservableProperty] private string brightnessValue = "50";
    partial void OnBrightnessValueChanged(string value) => ApplyBrightnessCommand.NotifyCanExecuteChanged();
    [ObservableProperty] private string protocolText = string.Empty;
    [ObservableProperty] private string protocolErrorText = string.Empty;

    public void AttachProtocol(DeviceProtocolService value, Func<CancellationToken, Task<OperationResult<Unit>>> discover)
    { protocol = value; beginDiscovery = discover; RefreshProtocolActions(); }
    private bool CanConnectProtocol() => !stopping && !IsProtocolBusy && protocol is not null && SelectedAdmission is not null
        && !sessions.HasRetainedSession && !sessions.IsBusy && coordinator.Current.Connection == ConnectionState.Discovered;
    private bool CanControlProtocol() => !stopping && !IsProtocolBusy && !queryingStatus && protocol is not null && !sessions.IsBusy
        && !IsSelecting && !IsDisconnecting && !IsRetryingPendingSave && !IsChangingStreaming
        && coordinator.Current is { Connection: ConnectionState.Ready, PendingOperationId: null };
    private bool CanThemeControl() => CanControlProtocol() && coordinator.Current.Mode == DeviceMode.Theme;
    private bool CanApplyBrightness() => CanThemeControl() && int.TryParse(BrightnessValue, out int value) && value is >= 0 and <= 100;
    private bool CanRetryProtocolSave() => !stopping && !IsProtocolBusy && protocol?.HasPendingSave == true;
    private bool CanSwitchTheme() => CanControlProtocol() && coordinator.Current.Mode == DeviceMode.SecondaryDisplay;
    private bool CanSwitchScreen() => CanControlProtocol() && coordinator.Current.Mode == DeviceMode.Theme;

    [RelayCommand(CanExecute = nameof(CanConnectProtocol))]
    private Task ConnectProtocolAsync() => RunProtocolAsync(() => protocol!.ConnectAsync(SelectedAdmission!, ProtocolToken), "设备协议已连接");
    [RelayCommand(CanExecute = nameof(CanApplyBrightness))]
    private Task ApplyBrightnessAsync() => QueueBrightness(int.Parse(BrightnessValue));
    [RelayCommand(CanExecute = nameof(CanThemeControl))]
    private Task SleepDisplayAsync() => RunProtocolAsync(() => protocol!.SetPowerAsync(false, ProtocolToken), "已熄屏");
    [RelayCommand(CanExecute = nameof(CanThemeControl))]
    private Task WakeDisplayAsync() => RunProtocolAsync(() => protocol!.SetPowerAsync(true, ProtocolToken), "已唤醒并发送当前内容");
    [RelayCommand(CanExecute = nameof(CanSwitchTheme))]
    private Task SwitchThemeAsync() => RunProtocolAsync(async () =>
    {
        var before = coordinator.Current;
        var confirmed = confirmModeSwitch is null ? new OperationResult<Unit>.Cancelled()
            : await confirmModeSwitch(ProtocolToken);
        if (confirmed is not OperationResult<Unit>.Success) { return confirmed; }
        if (before.DeviceKey != coordinator.Current.DeviceKey || before.SessionGeneration != coordinator.Current.SessionGeneration)
        { return new OperationResult<Unit>.Failure(AppError.Create("ModeConfirmationExpired", "确认期间设备会话已变化", "请重新选择设备后切换")); }
        return await protocol!.SwitchModeAsync(DeviceMode.Theme, ProtocolToken);
    }, "主题模式已连接");
    [RelayCommand(CanExecute = nameof(CanSwitchScreen))]
    private Task SwitchScreenAsync() => RunProtocolAsync(() => protocol!.SwitchModeAsync(DeviceMode.SecondaryDisplay, ProtocolToken), "副屏模式已确认，桌面输出由显示驱动负责");
    [RelayCommand(CanExecute = nameof(CanRetryProtocolSave))]
    private Task RetryProtocolSaveAsync() => RunProtocolAsync(() => protocol!.RetrySaveAsync(ProtocolToken), "本地设置已保存");

    private async Task RunProtocolAsync(Func<Task<OperationResult<Unit>>> action, string success)
    {
        if (IsProtocolBusy || stopping) { return; }
        protocolCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        IsProtocolBusy = true; ProtocolText = "正在处理"; ProtocolErrorText = string.Empty; RefreshProtocolActions();
        try
        {
            var result = await action();
            ProtocolText = result is OperationResult<Unit>.Success ? success : "操作未完成";
            ProtocolErrorText = result switch
            {
                OperationResult<Unit>.Failure failure => Describe(failure.Error),
                OperationResult<Unit>.Unknown unknown => Describe(unknown.Error),
                OperationResult<Unit>.Cancelled => "操作已取消，请核实当前设备状态",
                _ => string.Empty
            };
            if (coordinator.Current.ObservedBrightness is { } brightness) { BrightnessValue = brightness.ToString(); }
        }
        catch (OperationCanceledException) when (ProtocolToken.IsCancellationRequested) { ProtocolText = "操作已取消，请核实设备状态"; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "ProtocolWorkspace");
            ProtocolText = "操作未完成"; ProtocolErrorText = $"请核对设备和诊断。错误编号：{report.ErrorId}";
        }
        finally { protocolCancellation.Dispose(); protocolCancellation = null; IsProtocolBusy = false; RefreshProtocolActions(); }
    }

    private void RefreshProtocolActions()
    {
        RefreshBrightness();
        ConnectProtocolCommand.NotifyCanExecuteChanged(); ApplyBrightnessCommand.NotifyCanExecuteChanged();
        SleepDisplayCommand.NotifyCanExecuteChanged(); WakeDisplayCommand.NotifyCanExecuteChanged();
        SwitchThemeCommand.NotifyCanExecuteChanged(); SwitchScreenCommand.NotifyCanExecuteChanged();
        RetryProtocolSaveCommand.NotifyCanExecuteChanged(); RescanCommand.NotifyCanExecuteChanged();
        ConfirmSelectionCommand.NotifyCanExecuteChanged(); DisconnectCommand.NotifyCanExecuteChanged();
    }

    private async Task StopProtocolAsync()
    {
        if (startupProtocol is { } startup) { await startup; }
        if (statusSampling is { } sampling) { await sampling; }
        if (brightnessSending is { } brightness) { await brightness; }
        if (reconnecting is { } reconnect) { await reconnect; }
        foreach (var command in new IAsyncRelayCommand[] { ConnectProtocolCommand, ApplyBrightnessCommand, SleepDisplayCommand,
            WakeDisplayCommand, SwitchThemeCommand, SwitchScreenCommand, RetryProtocolSaveCommand, RescanCommand })
        { if (command.ExecutionTask is { } task) { await task; } }
        if (protocol is not null)
        {
            var closed = await protocol.ClosePendingAsync();
            if (closed is not OperationResult<Unit>.Success)
            { throw new InvalidOperationException("Protocol connection resources are still retained."); }
        }
    }
}
