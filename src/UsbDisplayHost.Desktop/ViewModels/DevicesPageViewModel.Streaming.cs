using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Core.Transport;
using UsbDisplayHost.Desktop.Services;

namespace UsbDisplayHost.Desktop.ViewModels;

public sealed partial class DevicesPageViewModel
{
    [ObservableProperty] private string streamingResultText = string.Empty;
    [ObservableProperty] private string streamingErrorText = string.Empty;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PauseStreamingCommand), nameof(ResumeStreamingCommand),
        nameof(DisconnectCommand), nameof(RetryPendingSaveCommand), nameof(ConfirmSelectionCommand), nameof(RescanCommand))]
    private bool isChangingStreaming;
    private bool CanPauseStreaming() => CanChangeStreaming(false);
    private bool CanResumeStreaming() => CanChangeStreaming(true);
    private bool CanChangeStreaming(bool resume)
    {
        var state = coordinator.Current;
        var owner = sessions.Current;
        if (stopping || IsProtocolBusy || IsSelecting || IsDisconnecting || IsRetryingPendingSave || IsChangingStreaming || sessions.IsBusy
            || state.DeviceKey is null || owner.Stage != SessionLifecycleStage.Active
            || owner.DeviceKey != state.DeviceKey || owner.Generation != state.SessionGeneration
            || OperationAvailability.Evaluate(state, resume ? DeviceAction.Resume : DeviceAction.Pause) is not OperationResult<Unit>.Success)
        { return false; }
        var transfer = sessions.CaptureTransport();
        return transfer is not null && transfer.Transfer.State == FrameTransferState.Ready
            && !transfer.FrameRate.RequiresRecovery && (!resume || !transfer.Producer.RequiresReceiptRecovery);
    }
    public void RefreshStreamingActions()
    { PauseStreamingCommand.NotifyCanExecuteChanged(); ResumeStreamingCommand.NotifyCanExecuteChanged(); }
    partial void OnIsSelectingChanged(bool value) => RefreshStreamingActions();
    partial void OnIsDisconnectingChanged(bool value) => RefreshStreamingActions();
    partial void OnIsRetryingPendingSaveChanged(bool value) => RefreshStreamingActions();
    [RelayCommand(CanExecute = nameof(CanPauseStreaming))]
    private Task PauseStreamingAsync() => ChangeStreamingAsync(false);
    [RelayCommand(CanExecute = nameof(CanResumeStreaming))]
    private Task ResumeStreamingAsync() => ChangeStreamingAsync(true);
    private async Task ChangeStreamingAsync(bool resume)
    {
        if (!CanChangeStreaming(resume)) { return; }
        var state = coordinator.Current;
        if (state.DeviceKey is not { } key) { return; }
        IsChangingStreaming = true; StreamingErrorText = string.Empty;
        StreamingResultText = $"设备 {key.Value} · 正在{(resume ? "恢复" : "暂停")}推流";
        try
        {
            var result = await sessions.SetStreamingAsync(key, state.SessionGeneration, resume, lifetime.Token);
            if (result is OperationResult<DeviceSnapshot>.Success)
            { StreamingResultText = $"设备 {key.Value} · 已{(resume ? "恢复" : "暂停")}推流"; }
            else
            {
                StreamingResultText = $"设备 {key.Value} · 推流变更未完成";
                StreamingErrorText = result switch
                {
                    OperationResult<DeviceSnapshot>.Failure failure => Describe(failure.Error),
                    OperationResult<DeviceSnapshot>.Unknown unknown => Describe(unknown.Error),
                    _ => "操作已取消，请读取当前推流状态"
                };
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DeviceStreamingCommand");
            StreamingResultText = $"设备 {key.Value} · 推流变更未确认";
            StreamingErrorText = Describe(new(report.ErrorId, "DeviceStreamingUnconfirmed",
                "原会话推流变更未确认", "请保留原会话并检查诊断，不重复操作"));
        }
        finally { IsChangingStreaming = false; RefreshStreamingActions(); }
    }
}
