using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Desktop.Services;

namespace UsbDisplayHost.Desktop.ViewModels;

public sealed partial class DevicesPageViewModel
{
    [ObservableProperty] private string pendingSaveText = string.Empty;
    [ObservableProperty] private string pendingSaveErrorText = string.Empty;
    [ObservableProperty] private string localSaveResultText = string.Empty;
    [ObservableProperty] private string localSaveErrorText = string.Empty;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RetryPendingSaveCommand), nameof(DisconnectCommand),
        nameof(ConfirmSelectionCommand), nameof(RescanCommand))]
    private bool isRetryingPendingSave;

    private bool CanRetryPendingSave()
    {
        var current = sessions.Current;
        return !stopping && !IsSelecting && !IsDisconnecting && !IsRetryingPendingSave && !IsChangingStreaming && !sessions.IsBusy
            && sessions.PendingSave is { CanVerifyProfile: true }
            && (current.Stage == SessionLifecycleStage.Active
                || current.Stage == SessionLifecycleStage.RetainedLocalResult && current.NativeClosed);
    }

    public void RefreshPendingSave()
    {
        var pending = sessions.PendingSave;
        PendingSaveText = pending is null ? string.Empty
            : $"设备 {pending.Context.DeviceKey.Value} · 修订 {pending.Context.ContentRevision} · 恢复配置"
                + (pending.State == ThemeSaveState.Unknown ? "保存结果待核实" : "待本地保存");
        PendingSaveErrorText = pending?.Error is { } error ? Describe(error) : string.Empty;
        RetryPendingSaveCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRetryPendingSave))]
    private async Task RetryPendingSaveAsync()
    {
        if (!CanRetryPendingSave() || sessions.PendingSave is not { } pending) { return; }
        IsRetryingPendingSave = true;
        LocalSaveErrorText = string.Empty;
        LocalSaveResultText = $"设备 {pending.Context.DeviceKey.Value} · 修订 {pending.Context.ContentRevision} · 正在处理恢复配置";
        try
        {
            var result = await sessions.RetrySaveAsync(pending.RetryId);
            RefreshPendingSave();
            if (result is OperationResult<PendingThemeSave>.Success)
            {
                LocalSaveResultText = $"设备 {pending.Context.DeviceKey.Value} · 修订 {pending.Context.ContentRevision} · 恢复配置已保存或核实";
            }
            else
            {
                LocalSaveResultText = $"设备 {pending.Context.DeviceKey.Value} · 修订 {pending.Context.ContentRevision} · 本地处理未完成";
                LocalSaveErrorText = result switch
                {
                    OperationResult<PendingThemeSave>.Failure failure => Describe(failure.Error),
                    OperationResult<PendingThemeSave>.Unknown unknown => Describe(unknown.Error),
                    _ => "本地处理已取消，待保存结果保留"
                };
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DevicePendingSaveRetry");
            LocalSaveResultText = $"设备 {pending.Context.DeviceKey.Value} · 修订 {pending.Context.ContentRevision} · 本地处理未确认";
            LocalSaveErrorText = Describe(new(report.ErrorId, "DevicePendingSaveUnconfirmed",
                "本地保存处理未确认", "请保留原会话与候选并检查诊断，不重复设备操作"));
        }
        finally { IsRetryingPendingSave = false; RetryPendingSaveCommand.NotifyCanExecuteChanged(); }
    }
}
