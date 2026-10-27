using CommunityToolkit.Mvvm.Input;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Sessions;

namespace UsbDisplayHost.Desktop.ViewModels;

public sealed partial class ThemePageViewModel
{
    public string SessionDeviceText => coordinator.Current.DeviceKey is { } key
        ? $"当前设备：{key.Value}" : "未连接设备 · 可本地编辑";

    public string EditorStatus => IsApplying ? "正在发送主题…"
        : coordinator.Current.Connection != ConnectionState.Ready ? "连接设备后可应用主题"
        : coordinator.Current.Mode != DeviceMode.Theme ? "切换到主题模式后可应用"
        : coordinator.Current.Power != PowerState.On ? "唤醒设备后可应用"
        : coordinator.Current.Streaming == StreamingState.Paused ? "恢复播放后可应用"
        : !UseDeviceApplication ? "设备暂不可用"
        : IsDirty ? "有未应用的更改" : "与当前主题一致";

    private void SynchronizeActiveContent()
    {
        var state = coordinator.Current;
        var active = coordinator.CurrentActiveProfile;
        if (IsBusy || IsDirty || active is null || active == observedActive || revision == long.MaxValue
            || active.Context.DeviceKey != state.DeviceKey || active.Context.SessionGeneration != state.SessionGeneration) { return; }
        if (Restore(new ThemeDraft(null, revision + 1, active.Content)))
        {
            observedActive = active;
            appliedContent = active.Content;
            preview.Request(draft);
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    private bool CanUndo() => CanOperate() && IsDirty && revision < long.MaxValue;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (!CanUndo() || !Restore(new ThemeDraft(null, revision + 1, appliedContent))) { return; }
        ValidationText = string.Empty;
        ErrorText = string.Empty;
        preview.Request(draft);
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(EditorStatus));
        UndoCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(EditorStatus));
        UndoCommand.NotifyCanExecuteChanged();
    }
}
