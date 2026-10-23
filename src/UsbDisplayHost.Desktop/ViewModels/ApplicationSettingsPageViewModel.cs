using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Desktop.ViewModels;

public sealed record CloseBehaviorOption(string Label, bool CloseToTray);

public partial class ApplicationSettingsPageViewModel(ApplicationSettingsService service, IErrorReporter errorReporter) : ObservableObject
{
    private readonly CancellationTokenSource lifetime = new();
    private Task? initialization;
    private ApplicationSettingsEdit saved = ToEdit(ApplicationSettings.Default);
    private bool restoring;
    private bool stopping;
    private bool canSave;
    private bool hasSavedFile;
    private bool unsupportedVersion;
    public AppUpdateViewModel? Updates { get; set; }
    public bool HasUnconfirmedSave => service.PendingSave is not null;
    public Guid? UnconfirmedSaveId => service.PendingSave?.ConfirmationId;

    public IReadOnlyList<CloseBehaviorOption> CloseBehaviors { get; } = [new("收至托盘", true), new("退出应用", false)];
    public ApplicationSettings SavedSettings => service.Current;
    public bool IsDirty => HasUnconfirmedSave || Capture() != saved;
    public bool LastSaveSucceeded { get; private set; }
    public bool CanEdit => IsReady && !unsupportedVersion && !stopping;
    public bool CanRevokeStartupMode => CanEdit && (SwitchToThemeOnStartup || saved.SwitchToThemeOnStartup || service.CanEnableAutomaticMode);
    public bool CanRevokeExitMode => CanEdit && (SwitchToSecondaryOnExit || saved.SwitchToSecondaryOnExit || service.CanEnableAutomaticMode);
    public void RefreshModeAvailability()
    {
        OnPropertyChanged(nameof(CanRevokeStartupMode));
        OnPropertyChanged(nameof(CanRevokeExitMode));
    }
    public string CurrentVersion => typeof(ApplicationSettingsPageViewModel).Assembly.GetName().Version?.ToString() ?? "未提供";

    [ObservableProperty] private bool startWithWindows;
    [ObservableProperty] private bool switchToThemeOnStartup;
    [ObservableProperty] private bool switchToSecondaryOnExit;
    [ObservableProperty] private bool automaticallyCheckUpdates = true;
    [ObservableProperty] private bool closeToTray = true;
    [ObservableProperty] private NavigationLayout navigationLayout;
    public bool IsTopNavigation
    {
        get => NavigationLayout == NavigationLayout.Top;
        set { if (value) { NavigationLayout = NavigationLayout.Top; } }
    }
    public bool IsSideNavigation
    {
        get => NavigationLayout == NavigationLayout.Side;
        set { if (value) { NavigationLayout = NavigationLayout.Side; } }
    }
    partial void OnNavigationLayoutChanged(NavigationLayout value)
    {
        OnPropertyChanged(nameof(IsTopNavigation));
        OnPropertyChanged(nameof(IsSideNavigation));
        Edited();
    }
    [RelayCommand]
    private void UndoNavigationPreview() => NavigationLayout = saved.NavigationLayout;
    [RelayCommand(CanExecute = nameof(CanDiscardEdits))]
    public void DiscardEdits() { Restore(saved); Edited(); }
    private bool CanDiscardEdits() => !IsBusy && CanEdit && !HasUnconfirmedSave;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit))] private bool isReady;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(ReloadCommand), nameof(DiscardEditsCommand))] private bool isBusy;
    [ObservableProperty] private string statusText = "正在读取应用设置";
    [ObservableProperty] private string errorText = "";
    [ObservableProperty] private string startupStateText = "自启注册：正在读取";

    partial void OnStartWithWindowsChanged(bool value) => Edited();
    partial void OnSwitchToThemeOnStartupChanged(bool value) => Edited();
    partial void OnSwitchToSecondaryOnExitChanged(bool value) => Edited();
    partial void OnAutomaticallyCheckUpdatesChanged(bool value) => Edited();
    partial void OnCloseToTrayChanged(bool value) => Edited();

    private void Edited()
    {
        if (restoring) { return; }
        RefreshModeAvailability();
        OnPropertyChanged(nameof(IsDirty));
        StatusText = HasUnconfirmedSave ? "保存结果待确认，编辑已保留" : IsDirty ? "未保存更改" : hasSavedFile ? "已保存" : "默认设置（尚未保存）";
        SaveCommand.NotifyCanExecuteChanged();
    }

    public Task InitializeAsync() => initialization ??= LoadCoreAsync();

    private bool CanSave() => !IsBusy && !stopping && canSave && !HasUnconfirmedSave;
    private bool CanReload() => !IsBusy && !stopping;

    [RelayCommand(CanExecute = nameof(CanReload))]
    private Task ReloadAsync() => HasUnconfirmedSave ? VerifySaveAsync() : LoadCoreAsync();

    private bool CanVerifySave() => HasUnconfirmedSave && !IsBusy && !stopping;
    [RelayCommand(CanExecute = nameof(CanVerifySave))]
    private async Task VerifySaveAsync()
    {
        if (UnconfirmedSaveId is not { } id) { return; }
        IsBusy = true;
        try
        {
            var result = await service.VerifyPendingAsync(id, lifetime.Token);
            if (result is OperationResult<ApplicationSettings>.Success confirmed)
            {
                saved = ToEdit(confirmed.Value); hasSavedFile = true; LastSaveSucceeded = true; canSave = !unsupportedVersion;
                ErrorText = ""; Edited();
            }
            else
            {
                ErrorText = result switch
                {
                    OperationResult<ApplicationSettings>.Unknown unknown => Format(unknown.Error),
                    OperationResult<ApplicationSettings>.Failure failure => Format(failure.Error),
                    _ => "核实已取消，原保存结果仍待确认"
                };
                StatusText = "保存结果仍待确认";
            }
            await RefreshStartupAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Report(exception, "ApplicationSettingsVerification"); }
        finally { IsBusy = false; RefreshPending(); }
    }

    public async Task<bool> DiscardUnconfirmedSaveAsync(Guid expectedId)
    {
        if (IsBusy || stopping || UnconfirmedSaveId != expectedId) { return false; }
        IsBusy = true;
        try
        {
            var result = await service.DiscardPendingAsync(expectedId, lifetime.Token);
            if (result is not OperationResult<Unit>.Success) { return false; }
            canSave = false;
            StatusText = "已放弃待确认记录，文件与自启未回滚；请重新读取";
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Report(exception, "ApplicationSettingsDiscard"); return false; }
        finally { IsBusy = false; RefreshPending(); }
    }

    private void RefreshPending()
    {
        DiscardEditsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnconfirmedSave)); OnPropertyChanged(nameof(UnconfirmedSaveId)); OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanRevokeStartupMode)); OnPropertyChanged(nameof(CanRevokeExitMode));
        SaveCommand.NotifyCanExecuteChanged(); VerifySaveCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadCoreAsync()
    {
        IsBusy = true;
        bool preserveEdit = IsReady && IsDirty;
        try
        {
            var result = await service.LoadAsync(lifetime.Token);
            if (result is OperationResult<ApplicationSettingsReadResult>.Success success)
            {
                unsupportedVersion = success.Value.Recovery == ProfileRecovery.UnsupportedVersion;
                canSave = !unsupportedVersion;
                hasSavedFile = success.Value.Recovery is ProfileRecovery.Primary or ProfileRecovery.Backup;
                saved = ToEdit(success.Value.Settings);
                if (!preserveEdit) { Restore(saved); }
                ErrorText = success.Value.Error is null ? "" : Format(success.Value.Error);
                IsReady = true;
                OnPropertyChanged(nameof(CanEdit));
                OnPropertyChanged(nameof(CanRevokeStartupMode));
                OnPropertyChanged(nameof(CanRevokeExitMode));
                Edited();
            }
            else if (result is OperationResult<ApplicationSettingsReadResult>.Failure failure)
            {
                canSave = false;
                ErrorText = Format(failure.Error);
                StatusText = "设置读取未完成";
            }
            else if (result is OperationResult<ApplicationSettingsReadResult>.Unknown unknown)
            {
                canSave = false;
                ErrorText = Format(unknown.Error);
                StatusText = "设置读取结果待确认";
            }
            await RefreshStartupAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            canSave = false;
            Report(exception, "ApplicationSettingsLoad");
        }
        finally { IsBusy = false; RefreshPending(); }
    }

    [RelayCommand(CanExecute = nameof(CanSave), IncludeCancelCommand = true)]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        LastSaveSucceeded = false;
        var candidate = Capture();
        try
        {
            var result = await service.SaveAsync(candidate, cancellationToken);
            switch (result)
            {
                case OperationResult<ApplicationSettings>.Success success:
                    LastSaveSucceeded = true;
                    saved = ToEdit(success.Value);
                    hasSavedFile = true;
                    OnPropertyChanged(nameof(CanRevokeStartupMode));
                    OnPropertyChanged(nameof(CanRevokeExitMode));
                    ErrorText = "";
                    Edited();
                    break;
                case OperationResult<ApplicationSettings>.Failure failure:
                    ErrorText = Format(failure.Error);
                    StatusText = "设置未保存";
                    break;
                case OperationResult<ApplicationSettings>.Unknown unknown:
                    ErrorText = Format(unknown.Error);
                    StatusText = "保存结果待确认，请重新读取";
                    canSave = false;
                    break;
                default:
                    StatusText = "已取消保存";
                    break;
            }
            await RefreshStartupAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { canSave = false; Report(exception, "ApplicationSettingsSave"); }
        finally { IsBusy = false; RefreshPending(); }
    }

    private async Task RefreshStartupAsync()
    {
        var state = await service.QueryStartupAsync(lifetime.Token);
        StartupStateText = state switch
        {
            OperationResult<StartupRegistrationState>.Success { Value.IsManaged: false } => "自启注册：同名启动项与当前应用不匹配",
            OperationResult<StartupRegistrationState>.Success success =>
                $"自启注册：{(success.Value.IsEnabled ? "已启用" : "未启用")}{(success.Value.IsEnabled == service.Current.StartWithWindows ? "" : "（与已保存设置不一致）")}",
            OperationResult<StartupRegistrationState>.Failure failure => $"自启注册：读取未完成。错误编号：{failure.Error.ErrorId}",
            OperationResult<StartupRegistrationState>.Unknown unknown => $"自启注册：待确认。错误编号：{unknown.Error.ErrorId}",
            _ => "自启注册：读取已取消"
        };
    }

    private ApplicationSettingsEdit Capture() => new(StartWithWindows, SwitchToThemeOnStartup,
        SwitchToSecondaryOnExit, AutomaticallyCheckUpdates, CloseToTray, NavigationLayout);
    private static ApplicationSettingsEdit ToEdit(ApplicationSettings settings) => new(settings.StartWithWindows,
        settings.SwitchToThemeOnStartup, settings.SwitchToSecondaryOnExit, settings.AutomaticallyCheckUpdates, settings.CloseToTray, settings.NavigationLayout);
    private void Restore(ApplicationSettingsEdit edit)
    {
        restoring = true;
        try
        {
            StartWithWindows = edit.StartWithWindows;
            SwitchToThemeOnStartup = edit.SwitchToThemeOnStartup;
            SwitchToSecondaryOnExit = edit.SwitchToSecondaryOnExit;
            AutomaticallyCheckUpdates = edit.AutomaticallyCheckUpdates;
            CloseToTray = edit.CloseToTray;
            NavigationLayout = edit.NavigationLayout;
        }
        finally { restoring = false; }
    }
    private static string Format(AppError error) => $"{error.Reason}。{error.Action}。错误编号：{error.ErrorId}";
    private void Report(Exception exception, string stage)
    {
        var report = errorReporter.Report(exception, stage);
        ErrorText = $"应用设置操作未完成，请重试。错误编号：{report.ErrorId}";
    }

    public async Task StopAsync()
    {
        if (Updates is not null) { await Updates.StopAsync(); }
        stopping = true;
        lifetime.Cancel();
        SaveCommand.Cancel();
        SaveCommand.NotifyCanExecuteChanged();
        ReloadCommand.NotifyCanExecuteChanged();
        VerifySaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanRevokeStartupMode));
        OnPropertyChanged(nameof(CanRevokeExitMode));
        if (initialization is not null) { await initialization; }
        if (SaveCommand.ExecutionTask is { } save) { await save; }
        if (ReloadCommand.ExecutionTask is { } reload) { await reload; }
        if (VerifySaveCommand.ExecutionTask is { } verify) { await verify; }
    }
}
