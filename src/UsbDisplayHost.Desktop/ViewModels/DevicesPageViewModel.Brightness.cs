using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;

namespace UsbDisplayHost.Desktop.ViewModels;

public sealed partial class DevicesPageViewModel
{
    private Task? brightnessSending;
    private int? pendingBrightness;
    private bool brightnessRunning, restoringBrightness;
    private DeviceKey? brightnessDevice;
    private long brightnessGeneration;
    // 离线初始目标与精确输入一致，不代表设备亮度读回。
    [ObservableProperty] private double brightnessTarget = 50;

    public bool CanAdjustBrightness => !stopping && protocol is { HasPendingSave: false }
        && !IsSelecting && !IsDisconnecting && !IsRetryingPendingSave && !IsChangingStreaming
        && coordinator.Current is { Connection: ConnectionState.Ready, Mode: DeviceMode.Theme, ObservedBrightness: not null }
        && (brightnessRunning ? coordinator.Current.DeviceKey == brightnessDevice
            && coordinator.Current.SessionGeneration == brightnessGeneration
            : !IsProtocolBusy && !queryingStatus && !sessions.IsBusy && coordinator.Current.PendingOperationId is null);

    partial void OnBrightnessTargetChanged(double value)
    {
        if (!restoringBrightness && CanAdjustBrightness && double.IsFinite(value) && value is >= 0 and <= 100)
        { QueueBrightness((int)Math.Round(value, MidpointRounding.AwayFromZero)); }
    }

    [RelayCommand(CanExecute = nameof(CanAdjustBrightness))]
    private void SetDefaultBrightness()
    {
        RestoreBrightnessTarget(80);
        QueueBrightness(80);
    }

    private Task QueueBrightness(int value)
    {
        if (!CanAdjustBrightness) { return Task.CompletedTask; }
        pendingBrightness = value;
        if (brightnessRunning) { return brightnessSending ?? Task.CompletedTask; }
        var current = coordinator.Current;
        brightnessDevice = current.DeviceKey;
        brightnessGeneration = current.SessionGeneration;
        brightnessRunning = true;
        return brightnessSending = SendBrightnessQueueAsync();
    }

    private async Task SendBrightnessQueueAsync()
    {
        try
        {
            await RunProtocolAsync(async () =>
            {
                while (pendingBrightness is not null)
                {
                    // 一个在途目标和一个最新待发目标；拖动不会形成无界写入队列。
                    await Task.Delay(100, ProtocolToken);
                    if (coordinator.Current.DeviceKey != brightnessDevice
                        || coordinator.Current.SessionGeneration != brightnessGeneration)
                    {
                        return new OperationResult<Unit>.Failure(AppError.Create("BrightnessSessionExpired",
                            "亮度目标所属设备会话已变化", "请在当前设备重新调整亮度"));
                    }
                    int value = pendingBrightness!.Value;
                    pendingBrightness = null;
                    var result = await protocol!.SetBrightnessAsync(value, ProtocolToken);
                    if (result is not OperationResult<Unit>.Success) { return result; }
                }
                return new OperationResult<Unit>.Success(new());
            }, "亮度已确认并保存");
        }
        finally
        {
            pendingBrightness = null;
            brightnessRunning = false;
            RefreshBrightness();
        }
    }

    private void RestoreBrightnessTarget(int value)
    {
        restoringBrightness = true;
        try { BrightnessTarget = value; }
        finally { restoringBrightness = false; }
    }

    private void RefreshBrightness()
    {
        if (!brightnessRunning && coordinator.Current.ObservedBrightness is { } actual)
        { RestoreBrightnessTarget(actual); }
        OnPropertyChanged(nameof(CanAdjustBrightness));
        SetDefaultBrightnessCommand.NotifyCanExecuteChanged();
    }
}
