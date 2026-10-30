using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Themes;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Core.Telemetry;
using UsbDisplayHost.Core.Transport;
using UsbDisplayHost.Desktop.Services;
using Wpf.Ui.Controls;

namespace UsbDisplayHost.Desktop.ViewModels;

public sealed partial class ThemePageViewModel : ObservableObject
{
    private readonly IAssetStore assets;
    private readonly IImagePicker imagePicker;
    private readonly DraftPreviewController preview;
    private readonly IErrorReporter errorReporter;
    private readonly DeviceCoordinator coordinator;
    private readonly DeviceSessionLifetime sessions;
    private readonly MetricSamplingService metrics;
    private readonly CancellationTokenSource lifetime = new();
    private Task? initialization;
    private ThemeDraft draft = new(null, 0, ThemeContent.Default);
    private string? assetId;
    private long revision;
    private ThemeContent appliedContent = ThemeContent.Default;
    private ActiveProfile? observedActive;
    private bool restoring;
    private bool pageVisible;
    private bool windowVisible = true;
    private bool stopping;
    private bool unsupportedVersion;
    private AssetReferenceLease? editorAsset;
    public ThemePageViewModel(IAssetStore assets, IImagePicker imagePicker,
        DraftPreviewController preview, IErrorReporter errorReporter, DeviceCoordinator coordinator,
        DeviceSessionLifetime sessions, MetricSamplingService metrics)
    {
        this.assets = assets;
        this.imagePicker = imagePicker;
        this.preview = preview;
        this.errorReporter = errorReporter;
        this.coordinator = coordinator; this.sessions = sessions; this.metrics = metrics;
        preview.Updated += OnPreviewUpdated;
    }

    public IReadOnlyList<ThemeOption> Templates { get; } = [
        new("图片", ThemeKind.Image, SymbolRegular.Image24), new("时钟", ThemeKind.Clock, SymbolRegular.Clock24),
        new("仪表盘", ThemeKind.Dashboard, SymbolRegular.DataHistogram24)];
    public IReadOnlyList<FitOption> Fits { get; } = [new("完整显示", ImageFit.Contain), new("填充裁剪", ImageFit.Cover)];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsImage), nameof(IsClock), nameof(IsDashboard))]
    private ThemeKind selectedTemplate;
    [ObservableProperty] private ImageFit fit = ImageFit.Contain;
    [ObservableProperty] private string backgroundColor = ThemeFields.ColorText(((ThemeContent.Image)ThemeContent.Default).Background);
    [ObservableProperty] private string foregroundColor = ThemeFields.ColorText(((ThemeContent.Clock)ThemeContent.DefaultClock).Foreground);
    [ObservableProperty] private bool use24HourClock = true;
    [ObservableProperty] private bool showDate = true;
    [ObservableProperty] private bool showCpuUsage = true;
    [ObservableProperty] private bool showMemoryUsage = true;
    [ObservableProperty] private bool showHostTemperature;
    [ObservableProperty] private bool showDeviceTemperature;
    [ObservableProperty] private string imageDescription = "内置背景";
    [ObservableProperty] private ImageSource? previewImage;
    [ObservableProperty] private ImageSource? selectedTemplatePreview;
    [ObservableProperty] private ImageSource? imageTemplatePreview;
    private ThemeContent.Image? imageTemplatePreviewContent;
    [ObservableProperty] private string errorText = string.Empty;
    [ObservableProperty] private string previewErrorText = string.Empty;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApplyCommand))] private string validationText = string.Empty;
    [ObservableProperty] private string applicationText = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ApplyLabel))] private bool isApplying;
    private Guid? applicationSaveId;
    private ThemeApplicationResult? lastApplication;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RetryApplicationSaveCommand))] private bool hasApplicationSave;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SelectImageCommand), nameof(ApplyCommand), nameof(RetryApplicationSaveCommand), nameof(RestoreDefaultsCommand))]
    private bool isBusy;
    public bool IsImage => SelectedTemplate == ThemeKind.Image;
    public bool IsClock => SelectedTemplate == ThemeKind.Clock;
    public bool IsDashboard => SelectedTemplate == ThemeKind.Dashboard;
    public bool IsDirty => !string.IsNullOrEmpty(ValidationText) || draft.Content != appliedContent;
    public bool CanEdit => CanOperate();
    public DiagnosticConfigurationSummary CaptureDiagnosticSummary() => new("ThemeEditor",
        SelectedTemplate switch { ThemeKind.Image => "Image", ThemeKind.Clock => "Clock", ThemeKind.Dashboard => "Dashboard", _ => "Unknown" },
        IsDirty, string.IsNullOrEmpty(ValidationText), assetId is not null);
    private bool CanOperate() => !IsBusy && !stopping;
    private bool CanApplyContent() => CanOperate() && !unsupportedVersion && string.IsNullOrEmpty(ValidationText);
    private bool CanRestoreDefaults() => CanOperate() && !unsupportedVersion;

    [RelayCommand(CanExecute = nameof(CanRestoreDefaults))]
    private void RestoreDefaults()
    {
        if (!CanRestoreDefaults()) { return; }
        try
        {
            if (!ReplaceEditorAsset(null)) { return; }
            restoring = true;
            try
            {
                // 仅恢复表单；已冻结应用候选与恢复配置仍由原所有者持有。
                var image = (ThemeContent.Image)ThemeContent.Default;
                var clock = (ThemeContent.Clock)ThemeContent.DefaultClock;
                var dashboard = (ThemeContent.Dashboard)ThemeContent.DefaultDashboard;
                Fit = image.Fit; ImageDescription = "内置背景";
                BackgroundColor = ThemeFields.ColorText(image.Background); ForegroundColor = ThemeFields.ColorText(clock.Foreground);
                Use24HourClock = clock.Use24HourClock; ShowDate = clock.ShowDate;
                ShowCpuUsage = dashboard.ShowCpuUsage; ShowMemoryUsage = dashboard.ShowMemoryUsage;
                ShowHostTemperature = dashboard.ShowHostTemperature; ShowDeviceTemperature = dashboard.ShowDeviceTemperature;
            }
            finally { restoring = false; }
            UpdateDraft();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Report(exception, "ThemeDraftDefaults"); }
    }
    public string ApplyLabel => IsApplying ? "正在应用" : "应用到设备";
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
    private bool CanApply() => CanApplyContent() && UseDeviceApplication && !sessions.IsBusy
        && coordinator.Current.PendingOperationId is null && sessions.PendingSave is null;
    private bool CanRetryApplicationSave() => CanOperate() && applicationSaveId is { } id
        && sessions.PendingSave is { CanVerifyProfile: true } pending && pending.RetryId == id && !sessions.IsBusy;
    public void RefreshDeviceActions()
    {
        SynchronizeActiveContent();
        OnPropertyChanged(nameof(SessionDeviceText));
        OnPropertyChanged(nameof(EditorStatus));
        OnPropertyChanged(nameof(UseDeviceApplication));
        ApplyCommand.NotifyCanExecuteChanged();
        RetryApplicationSaveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanApply), IncludeCancelCommand = true)]
    private async Task ApplyAsync(CancellationToken token)
    {
        if (!CanApply()) { return; }
        var state = coordinator.Current;
        if (state.DeviceKey is null) { return; }
        // 应用时固定内容与设备代次，预览编辑不会直接触发发送。
        var candidate = draft with { DeviceKey = state.DeviceKey };
        AssetReferenceLease? candidateAsset = null;
        bool submitted = false;
        IsBusy = true; IsApplying = true;
        try
        {
            if (candidate.Content is ThemeContent.Image { AssetId: not null } image
                && !TryAcquireReference(image.AssetId, AssetReferenceOwner.Candidate, out candidateAsset)) { return; }
            ApplicationText = "正在发送主题…";
            var samples = metrics.GetSnapshot();
            ErrorText = string.Empty;
            submitted = true;
            var result = await sessions.ApplyThemeAsync(state.DeviceKey, state.SessionGeneration, candidate, samples, token);
            if (coordinator.Current.DeviceKey != state.DeviceKey || coordinator.Current.SessionGeneration != state.SessionGeneration) { return; }
            if (result.Attempt?.Result is OperationResult<ThemeApplicationResult>.Success applied)
            {
                var value = applied.Value;
                lastApplication = value;
                if (value.ActiveCommitted)
                {
                    appliedContent = candidate.Content;
                    OnPropertyChanged(nameof(IsDirty));
                    OnPropertyChanged(nameof(EditorStatus));
                }
                ApplicationText = FormatApplication(value);
                applicationSaveId = value.SaveRetryId;
                HasApplicationSave = applicationSaveId is not null;
                if (value.Error is not null) { ShowError(value.Error); }
            }
            else
            {
                ApplicationText = "主题未应用，编辑已保留";
                var error = result.Attempt?.Result switch
                {
                    OperationResult<ThemeApplicationResult>.Failure failure => failure.Error,
                    OperationResult<ThemeApplicationResult>.Unknown unknown => unknown.Error,
                    _ => null
                };
                if (error is not null) { ShowError(error); }
            }
            if (result.Session is OperationResult<DeviceSnapshot>.Failure failed) { ShowError(failed.Error); }
            else if (result.Session is OperationResult<DeviceSnapshot>.Unknown unknown) { ShowError(unknown.Error); }
            else if (result.Session is OperationResult<DeviceSnapshot>.Cancelled) { ApplicationText += "。已取消"; }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "ThemeApplyCommand");
            ApplicationText = submitted ? "应用结果未确认，编辑已保留" : "未受理应用，编辑已保留";
            ShowError(new(report.ErrorId, submitted ? "ThemeApplyUnconfirmed" : "ThemeApplyNotAccepted",
                ApplicationText, submitted ? "请核实发送及保存结果，不直接重复应用" : "请检查输入与诊断"));
        }
        finally { candidateAsset?.Dispose(); IsApplying = false; IsBusy = false; RefreshDeviceActions(); }
    }

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
                applicationSaveId = null; HasApplicationSave = false; ErrorText = string.Empty;
            }
            else if (result is OperationResult<PendingThemeSave>.Failure failure) { ShowError(failure.Error); }
            else if (result is OperationResult<PendingThemeSave>.Unknown unknown) { ShowError(unknown.Error); }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Report(exception, "ThemeApplicationSaveRetry"); }
        finally { IsBusy = false; RefreshDeviceActions(); }
    }

    private static string FormatApplication(ThemeApplicationResult value) => value.ActiveCommitted
        ? value.SaveState == ThemeSaveState.Saved ? "主题已发送并保存" : "主题已发送，本地保存未完成"
        : "应用结果待确认";

    partial void OnSelectedTemplateChanged(ThemeKind value)
    {
        if (!restoring) { SelectImageCommand.Cancel(); }
        UpdateDraft();
    }
    partial void OnFitChanged(ImageFit value) => UpdateDraft();
    partial void OnBackgroundColorChanged(string value) => UpdateDraft();
    partial void OnForegroundColorChanged(string value) => UpdateDraft();
    partial void OnUse24HourClockChanged(bool value) => UpdateDraft();
    partial void OnShowDateChanged(bool value) => UpdateDraft();
    partial void OnShowCpuUsageChanged(bool value) => UpdateDraft();
    partial void OnShowMemoryUsageChanged(bool value) => UpdateDraft();
    partial void OnShowHostTemperatureChanged(bool value) => UpdateDraft();
    partial void OnShowDeviceTemperatureChanged(bool value) => UpdateDraft();
    partial void OnValidationTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(EditorStatus));
        UndoCommand.NotifyCanExecuteChanged();
    }

    private void UpdateDraft()
    {
        if (restoring || stopping) { return; }
        InvalidateTemplatePreviews();
        if (revision == long.MaxValue)
        {
            unsupportedVersion = true;
            preview.Invalidate();
            ShowError(AppError.Create("ThemeRevisionExhausted", "编辑次数已达到限额", "请重新打开应用"));
            ApplyCommand.NotifyCanExecuteChanged(); RestoreDefaultsCommand.NotifyCanExecuteChanged();
            return;
        }
        revision++;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(EditorStatus));
        if (lastApplication is not null) { ApplicationText = FormatApplication(lastApplication); }
        RgbColor foreground = default;
        if (!ThemeFields.TryColor(BackgroundColor, out var background)
            || (!IsImage && !ThemeFields.TryColor(ForegroundColor, out foreground)))
        {
            ValidationText = "颜色请输入 #RRGGBB，例如 #FFFFFF";
            preview.Invalidate();
            return;
        }
        ThemeContent content = SelectedTemplate switch
        {
            ThemeKind.Image => new ThemeContent.Image(assetId, Fit, background),
            ThemeKind.Clock => new ThemeContent.Clock(Use24HourClock, ShowDate, background, foreground),
            ThemeKind.Dashboard => new ThemeContent.Dashboard(ShowCpuUsage, ShowMemoryUsage,
                ShowHostTemperature, ShowDeviceTemperature, background, foreground),
            _ => ThemeContent.Default
        };
        var validation = ThemeValidation.Validate(content);
        ValidationText = validation is OperationResult<Unit>.Failure failure ? failure.Error.Reason : string.Empty;
        if (string.IsNullOrEmpty(ValidationText))
        {
            draft = new ThemeDraft(draft.DeviceKey, revision, content);
            preview.Request(draft);
        }
        else { preview.Invalidate(); }
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(EditorStatus));
        UndoCommand.NotifyCanExecuteChanged();
    }

    public Task InitializeAsync()
    {
        if (initialization is not null) { return initialization; }
        SynchronizeActiveContent();
        preview.Request(draft);
        return initialization = Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanOperate), IncludeCancelCommand = true)]
    private async Task SelectImageAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            string? path = imagePicker.PickImage();
            if (path is null) { return; }
            var result = await assets.ImportAsync(path, cancellationToken);
            if (result is OperationResult<ImportedAsset>.Success imported)
            {
                using var importedReference = imported.Value;
                if (!IsImage || stopping || cancellationToken.IsCancellationRequested) { return; }
                if (!ReplaceEditorAsset(imported.Value.AssetId)) { return; }
                ImageDescription = System.IO.Path.GetFileName(path);
                ErrorText = string.Empty;
                UpdateDraft();
            }
            else if (result is OperationResult<ImportedAsset>.Failure failure) { ShowError(failure.Error); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Report(exception, "ImageSelection"); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void RetryPreview()
    {
        if (string.IsNullOrEmpty(ValidationText)) { preview.Request(draft, force: true); }
    }

    public void SetPageVisible(bool value)
    {
        pageVisible = value;
        if (!value) { SelectedTemplatePreview = null; }
        preview.SetVisible(pageVisible && windowVisible);
    }
    public void SetWindowVisible(bool value)
    {
        windowVisible = value;
        if (!value) { SelectedTemplatePreview = null; }
        preview.SetVisible(pageVisible && windowVisible);
    }
    private void OnPreviewUpdated(PreviewUpdate update)
    {
        if (update.DeviceKey != draft.DeviceKey || update.Revision != revision || stopping) { return; }
        if (update.Error is not null)
        {
            SelectedTemplatePreview = null;
            PreviewErrorText = $"{update.Error.Reason}。{update.Error.Action}（错误编号：{update.Error.ErrorId}）";
        }
        else
        {
            PreviewImage = update.Image;
            SelectedTemplatePreview = update.Image;
            if (draft.Content is ThemeContent.Image image)
            {
                imageTemplatePreviewContent = image;
                ImageTemplatePreview = update.Image;
            }
            PreviewErrorText = string.Empty;
        }
    }

    private void InvalidateTemplatePreviews()
    {
        // 只复用通过现有修订检查的位图，不读取素材文件或持有额外素材租约。
        // 时钟／遥测仅显示当前预览；图片只有在素材、适配和背景仍一致时才能复用。
        SelectedTemplatePreview = null;
        if (!ThemeFields.TryColor(BackgroundColor, out var background)
            || imageTemplatePreviewContent != new ThemeContent.Image(assetId, Fit, background))
        {
            imageTemplatePreviewContent = null;
            ImageTemplatePreview = null;
        }
    }
    private void ShowError(AppError error) => ErrorText = $"{error.Reason}。{error.Action}（错误编号：{error.ErrorId}）";
    private void Report(Exception exception, string stage)
    {
        var report = errorReporter.Report(exception, stage);
        ShowError(new AppError(report.ErrorId, "ThemeOperationFailed", "主题操作未完成", "请检查输入后重试"));
    }

    private bool TryAcquireReference(string id, AssetReferenceOwner owner, out AssetReferenceLease? lease)
    {
        lease = null;
        try
        {
            var acquired = assets.AcquireReference(id, owner);
            if (acquired is OperationResult<AssetReferenceLease>.Success success) { lease = success.Value; return true; }
            ShowError(acquired is OperationResult<AssetReferenceLease>.Failure failure ? failure.Error
                : AppError.Create("DraftAssetReferenceUnconfirmed", "图片素材不可用", "请重新选择素材"));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Report(exception, "DraftAssetReference"); }
        return false;
    }

    private bool ReplaceEditorAsset(string? id)
    {
        AssetReferenceLease? next = null;
        if (id is not null && !TryAcquireReference(id, AssetReferenceOwner.DraftEditor, out next)) { return false; }
        var previous = editorAsset;
        editorAsset = next;
        assetId = id;
        previous?.Dispose();
        return true;
    }

    public async Task StopAsync()
    {
        stopping = true;
        RestoreDefaultsCommand.NotifyCanExecuteChanged();
        lifetime.Cancel();
        ApplyCommand.Cancel();
        SelectImageCommand.Cancel();
        preview.Updated -= OnPreviewUpdated;
        await Task.WhenAll(new[] { initialization, SelectImageCommand.ExecutionTask, ApplyCommand.ExecutionTask, RetryApplicationSaveCommand.ExecutionTask }
            .OfType<Task>());
        editorAsset?.Dispose();
        editorAsset = null;
    }
}
