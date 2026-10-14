using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Core.Telemetry;
using UsbDisplayHost.Desktop.Services;

namespace UsbDisplayHost.Desktop.ViewModels;

public sealed record RotationOption(string Label, DisplayRotation Value);

public partial class DeviceSettingsPageViewModel(IErrorReporter reporter,
    DeviceCoordinator coordinator, DeviceSessionLifetime sessions, MetricSamplingService metrics,
    DevicesPageViewModel deviceControls) : ObservableObject
{
    private DeviceSettings appliedSettings = DeviceSettings.Default;
    private DeviceKey? editingDevice;
    private long editingGeneration, revision;
    private bool restoring, stopping;
    private Guid? applicationSaveId;
    private ThemeApplicationResult? lastApplication;

    public DevicesPageViewModel DeviceControls { get; } = deviceControls;
    public IReadOnlyList<RotationOption> Rotations { get; } = [new("0°", DisplayRotation.Degrees0),
        new("90°", DisplayRotation.Degrees90), new("180°", DisplayRotation.Degrees180), new("270°", DisplayRotation.Degrees270)];
    public IReadOnlyList<int> FrameRates { get; } = [1, 2, 3, 4, 5];

    [ObservableProperty] private DisplayRotation rotation;
    [ObservableProperty] private int framesPerSecond = 5;
    [ObservableProperty] private double jpegQuality = 80;
    [ObservableProperty] private bool isReady;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ApplyLabel))] private bool isApplying;
    [ObservableProperty] private string errorText = string.Empty;
    [ObservableProperty] private string validationText = string.Empty;
    [ObservableProperty] private string applicationText = string.Empty;

    public string ApplyLabel => IsApplying ? "正在应用…" : "应用设置";
    public bool HasApplicationSave => applicationSaveId is not null;
    public bool IsDirty => Rotation != appliedSettings.Rotation || FramesPerSecond != appliedSettings.FramesPerSecond
        || JpegQuality != appliedSettings.JpegQuality;
    public bool CanEdit => IsReady && UseDeviceApplication && !IsBusy && !stopping;
    public bool IsConnected => coordinator.Current.Connection == ConnectionState.Ready;
    public string DeviceText => coordinator.Current.DeviceKey is { } key ? $"设备 {key.Value}" : "未连接设备";
    public string AvailabilityText => coordinator.Current.Connection != ConnectionState.Ready ? "连接设备后可调整设置"
        : coordinator.Current.Mode != DeviceMode.Theme ? "切换到主题模式后可调整设置"
        : coordinator.Current.Power != PowerState.On ? "唤醒设备后可调整设置"
        : coordinator.Current.Streaming == StreamingState.Paused ? "恢复播放后可调整设置"
        : !UseDeviceApplication ? "设备暂不可用，请查看设备页" : string.Empty;
    public string StatusText => !string.IsNullOrEmpty(AvailabilityText) ? AvailabilityText
        : IsBusy ? "正在处理设置…" : IsDirty ? "有未应用的更改" : "设置与当前会话一致";
    public bool UseDeviceApplication
    {
        get
        {
            var state = coordinator.Current;
            var owner = sessions.Current;
            return state.DeviceKey is not null && state.Connection == ConnectionState.Ready && state.Mode == DeviceMode.Theme
                && state.Power == PowerState.On && state.Streaming is not (StreamingState.Paused or StreamingState.Faulted)
                && state.Capabilities is { IsProtocolMatched: true } && owner.Stage == SessionLifecycleStage.Active
                && owner.DeviceKey == state.DeviceKey && owner.Generation == state.SessionGeneration;
        }
    }

    public Task InitializeAsync()
    {
        IsReady = true;
        RefreshDeviceActions();
        return Task.CompletedTask;
    }

    public void RefreshDeviceActions()
    {
        var state = coordinator.Current;
        var active = coordinator.CurrentActiveProfile;
        var currentSettings = active is not null && active.Context.DeviceKey == state.DeviceKey
            && active.Context.SessionGeneration == state.SessionGeneration ? active.Settings : null;
        if (editingDevice != state.DeviceKey || editingGeneration != state.SessionGeneration)
        {
            // 参数只属于当前设备会话，切换设备后不携带上一台设备的未应用输入。
            editingDevice = state.DeviceKey;
            editingGeneration = state.SessionGeneration;
            appliedSettings = currentSettings ?? DeviceSettings.Default;
            Restore(appliedSettings);
            ErrorText = ValidationText = ApplicationText = string.Empty;
            applicationSaveId = null;
            lastApplication = null;
        }
        else if (currentSettings is not null && currentSettings != appliedSettings)
        {
            bool retainInput = IsDirty;
            appliedSettings = currentSettings;
            if (!retainInput) { Restore(appliedSettings); }
        }
        Refresh();
    }

    partial void OnRotationChanged(DisplayRotation value) => Edited();
    partial void OnFramesPerSecondChanged(int value) => Edited();
    partial void OnJpegQualityChanged(double value) => Edited();
    partial void OnIsBusyChanged(bool value) => Refresh();

    private void Edited()
    {
        if (restoring) { return; }
        if (revision < long.MaxValue) { revision++; }
        ValidationText = Capture() is OperationResult<DeviceSettings>.Failure failure ? failure.Error.Reason : string.Empty;
        Refresh();
    }

    private OperationResult<DeviceSettings> Capture() => double.IsFinite(JpegQuality) && JpegQuality == Math.Truncate(JpegQuality)
        && JpegQuality is >= 70 and <= 85
        ? DeviceSettings.Create(Rotation, FramesPerSecond, (int)JpegQuality)
        : new OperationResult<DeviceSettings>.Failure(AppError.Create("InvalidSettingsQuality", "画质必须为 70～85 的整数", "请检查画质输入"));

    private bool CanApply() => CanEdit && !sessions.IsBusy && string.IsNullOrEmpty(ValidationText)
        && editingDevice == coordinator.Current.DeviceKey && editingGeneration == coordinator.Current.SessionGeneration
        && coordinator.Current.PendingOperationId is null && sessions.PendingSave is null;

    [RelayCommand(CanExecute = nameof(CanApply), IncludeCancelCommand = true)]
    private async Task ApplyAsync(CancellationToken token)
    {
        if (!CanApply() || Capture() is not OperationResult<DeviceSettings>.Success captured) { return; }
        var state = coordinator.Current;
        if (state.DeviceKey is null) { return; }
        var request = new DeviceSettingsDraft(state.DeviceKey, revision, captured.Value);
        IsBusy = true;
        IsApplying = true;
        ErrorText = string.Empty;
        ApplicationText = "正在发送设置…";
        try
        {
            var completion = await sessions.ApplySettingsAsync(state.DeviceKey, state.SessionGeneration, request, metrics.GetSnapshot(), token);
            if (coordinator.Current.DeviceKey != state.DeviceKey || coordinator.Current.SessionGeneration != state.SessionGeneration) { return; }
            if (completion.Attempt?.Result is OperationResult<ThemeApplicationResult>.Success applied)
            {
                lastApplication = applied.Value;
                applicationSaveId = lastApplication.SaveRetryId;
                ApplicationText = FormatApplication(lastApplication);
                if (lastApplication.ActiveCommitted) { appliedSettings = captured.Value; }
                if (lastApplication.Error is not null) { ErrorText = Format(lastApplication.Error); }
            }
            else
            {
                ApplicationText = "设置未应用";
                if (completion.Attempt?.Result is OperationResult<ThemeApplicationResult>.Failure failure) { ErrorText = Format(failure.Error); }
                else if (completion.Attempt?.Result is OperationResult<ThemeApplicationResult>.Unknown unknown) { ErrorText = Format(unknown.Error); }
            }
            if (completion.Session is OperationResult<DeviceSnapshot>.Failure failed) { ErrorText = Format(failed.Error); }
            else if (completion.Session is OperationResult<DeviceSnapshot>.Unknown unknown) { ErrorText = Format(unknown.Error); }
            else if (completion.Session is OperationResult<DeviceSnapshot>.Cancelled) { ApplicationText += " · 已取消"; }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DeviceSettingsApplyCommand");
            ApplicationText = "应用结果待确认";
            ErrorText = $"请查看设备状态与诊断，避免重复应用。错误编号：{report.ErrorId}";
        }
        finally
        {
            IsApplying = false;
            IsBusy = false;
            RefreshDeviceActions();
        }
    }

    private bool CanRetryApplicationSave() => !IsBusy && !stopping && applicationSaveId is { } id
        && sessions.PendingSave is { CanVerifyProfile: true } pending && pending.RetryId == id && !sessions.IsBusy;

    [RelayCommand(CanExecute = nameof(CanRetryApplicationSave))]
    private async Task RetryApplicationSaveAsync()
    {
        if (!CanRetryApplicationSave() || applicationSaveId is not { } id) { return; }
        IsBusy = true;
        try
        {
            var result = await sessions.RetrySaveAsync(id);
            if (result is OperationResult<PendingThemeSave>.Success)
            {
                if (lastApplication is not null)
                {
                    lastApplication = lastApplication with { SaveState = ThemeSaveState.Saved, SaveRetryId = null, Error = null };
                    ApplicationText = FormatApplication(lastApplication);
                }
                applicationSaveId = null;
                ErrorText = string.Empty;
            }
            else if (result is OperationResult<PendingThemeSave>.Failure failure) { ErrorText = Format(failure.Error); }
            else if (result is OperationResult<PendingThemeSave>.Unknown unknown) { ErrorText = Format(unknown.Error); }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "SettingsApplicationSaveRetry");
            ErrorText = $"设置保存未完成。错误编号：{report.ErrorId}";
        }
        finally { IsBusy = false; RefreshDeviceActions(); }
    }

    private bool CanRestoreDefaults() => CanEdit;
    private bool CanUndo() => CanEdit && IsDirty;
    [RelayCommand(CanExecute = nameof(CanRestoreDefaults))]
    private void RestoreDefaults()
    {
        if (!CanRestoreDefaults()) { return; }
        Restore(DeviceSettings.Default);
        Edited();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (!CanUndo()) { return; }
        Restore(appliedSettings);
        Edited();
    }

    private void Restore(DeviceSettings settings)
    {
        restoring = true;
        try { Rotation = settings.Rotation; FramesPerSecond = settings.FramesPerSecond; JpegQuality = settings.JpegQuality; }
        finally { restoring = false; }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(DeviceText));
        OnPropertyChanged(nameof(UseDeviceApplication));
        OnPropertyChanged(nameof(AvailabilityText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasApplicationSave));
        ApplyCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RestoreDefaultsCommand.NotifyCanExecuteChanged();
        RetryApplicationSaveCommand.NotifyCanExecuteChanged();
    }

    private static string FormatApplication(ThemeApplicationResult result) => result.ActiveCommitted
        ? result.SaveState == ThemeSaveState.Saved ? "设置已发送并保存" : "设置已发送，本地保存未完成"
        : "应用结果待确认";
    private static string Format(AppError error) => $"{error.Reason}。{error.Action}。错误编号：{error.ErrorId}";

    public async Task StopAsync()
    {
        stopping = true;
        ApplyCommand.Cancel();
        Refresh();
        if (ApplyCommand.ExecutionTask is { } applyTask) { await applyTask; }
        if (RetryApplicationSaveCommand.ExecutionTask is { } retryTask) { await retryTask; }
    }
}
