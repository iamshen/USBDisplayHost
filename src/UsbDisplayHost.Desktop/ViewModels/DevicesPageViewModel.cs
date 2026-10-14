using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Immutable;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Windows.Usb;
using UsbDisplayHost.Desktop.Services;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Themes;
using UsbDisplayHost.Core.Transport;
using System.IO;
using System.Windows.Media.Imaging;

namespace UsbDisplayHost.Desktop.ViewModels;

public sealed record DiscoveredDeviceItem(DeviceKey Key, string Title, string InterfacesText, bool IsSelectable, string ErrorText);

public sealed partial class DevicesPageViewModel(DeviceCoordinator coordinator, DeviceSessionLifetime sessions, IErrorReporter reporter) : ObservableObject
{
    private readonly CancellationTokenSource lifetime = new();
    private bool stopping;
    private bool pageVisible, windowVisible = true;
    private long visibilityRevision;
    private Task? previewReading;
    private FrameContext? previewContext;
    private long previewFrameNumber = -1;
    private FrameContext? failedPreviewContext;
    private long failedPreviewNumber = -1;
    private string previewFailure = string.Empty;
    [ObservableProperty] private BitmapSource? hostPreview;
    [ObservableProperty] private string hostPreviewText = "主机预览：尚无已提交画面";
    [ObservableProperty] private string hostPreviewErrorText = string.Empty;
    public void SetPreviewPageVisible(bool value) { pageVisible = value; visibilityRevision++; }
    public void SetPreviewWindowVisible(bool value) { windowVisible = value; visibilityRevision++; }
    public Task RefreshHostPreviewAsync()
    {
        RefreshPendingSave();
        RefreshStreamingActions();
        if (stopping || !pageVisible || !windowVisible || previewReading is { IsCompleted: false }) { return Task.CompletedTask; }
        return previewReading = ReadHostPreviewAsync();
    }
    private async Task ReadHostPreviewAsync()
    {
        long visibility = visibilityRevision;
        CompletedHostFrame? attempted = null;
        try
        {
            if (coordinator.Current.DeviceKey is { } key && previewContext is { } previous && previous.DeviceKey != key)
            { HostPreview = null; previewContext = null; previewFrameNumber = -1; }
            var completed = sessions.CaptureActiveHostFrame();
            if (completed is null)
            {
                HostPreviewText = HostPreview is null ? "主机预览：尚无已提交画面"
                    : $"上次主机画面 · 设备 {previewContext?.DeviceKey?.Value}（当前活动画面未提供）";
                return;
            }
            if (completed.Frame.Context == failedPreviewContext && completed.Frame.FrameNumber == failedPreviewNumber)
            { HostPreviewErrorText = previewFailure; return; }
            if (completed.Frame.Context == previewContext && completed.Frame.FrameNumber == previewFrameNumber)
            { HostPreviewText = PreviewCaption(completed); return; }
            attempted = completed;
            var image = await Task.Run(() =>
            {
                using var stream = new MemoryStream(completed.Frame.Jpeg.ToArray(), writable: false);
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = Math.Min(completed.Frame.Width, 576); bitmap.StreamSource = stream;
                bitmap.EndInit(); bitmap.Freeze();
                return bitmap;
            });
            if (stopping || !pageVisible || !windowVisible || visibility != visibilityRevision
                || !ReferenceEquals(sessions.CaptureActiveHostFrame(), completed)) { return; }
            previewContext = completed.Frame.Context; previewFrameNumber = completed.Frame.FrameNumber;
            HostPreview = image; HostPreviewErrorText = string.Empty;
            HostPreviewText = PreviewCaption(completed);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "ActiveHostPreviewDecode");
            previewFailure = $"主机预览未完成，请重试预览或检查诊断。错误编号：{report.ErrorId}";
            if (attempted is not null) { failedPreviewContext = attempted.Frame.Context; failedPreviewNumber = attempted.Frame.FrameNumber; }
            if (!stopping && visibility == visibilityRevision)
            { HostPreviewErrorText = previewFailure; }
        }
    }
    private static string PreviewCaption(CompletedHostFrame completed) =>
        $"主机预览 · 设备 {completed.Frame.Context.DeviceKey?.Value} · 帧 {completed.Frame.FrameNumber}（呈现反馈未提供）";
    [RelayCommand]
    private Task RetryHostPreviewAsync()
    {
        if (stopping) { return Task.CompletedTask; }
        failedPreviewContext = null; failedPreviewNumber = -1;
        return RefreshHostPreviewAsync();
    }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ConfirmSelectionCommand), nameof(RescanCommand), nameof(DisconnectCommand), nameof(RetryPendingSaveCommand))]
    private bool isSelecting;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DisconnectCommand), nameof(ConfirmSelectionCommand), nameof(RescanCommand), nameof(RetryPendingSaveCommand))]
    private bool isDisconnecting;
    [ObservableProperty] private string disconnectText = string.Empty;
    [ObservableProperty] private string disconnectErrorText = string.Empty;
    public string DisconnectActionText => sessions.Current.NativeClosed ? "完成本地交接" : "断开设备";
    private bool CanDisconnect()
    {
        var current = sessions.Current;
        return !stopping && !IsProtocolBusy && !IsSelecting && !IsDisconnecting && !IsRetryingPendingSave && !IsChangingStreaming && !sessions.IsBusy
            && sessions.HasRetainedSession && current.DeviceKey is not null
            && (current.Stage == SessionLifecycleStage.Active
                || current.Stage == SessionLifecycleStage.RetainedLocalResult && current.NativeClosed);
    }
    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        if (!CanDisconnect()) { return; }
        var current = sessions.Current;
        if (current.DeviceKey is not { } key) { return; }
        IsDisconnecting = true; DisconnectErrorText = string.Empty;
        DisconnectText = current.NativeClosed ? "正在完成本地交接" : "正在断开设备";
        try
        {
            var result = await sessions.DisconnectAsync(key, current.Generation);
            if (result is OperationResult<Unit>.Success)
            {
                DisconnectText = "设备会话已释放";
                if (selectedDevice?.Key == key)
                {
                    selectedDevice = null; selectedAdmission = null;
                    SelectionText = "未选择发现设备";
                    OnPropertyChanged(nameof(SelectedDevice)); OnPropertyChanged(nameof(SelectedAdmission));
                }
            }
            else
            {
                DisconnectText = "断开未完成，原所有者已保留";
                DisconnectErrorText = result switch
                {
                    OperationResult<Unit>.Failure failure => Describe(failure.Error),
                    OperationResult<Unit>.Unknown unknown => Describe(unknown.Error),
                    _ => "断开未确认，请核实当前会话状态"
                };
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DeviceDisconnectCommand");
            DisconnectText = "断开结果未确认";
            DisconnectErrorText = Describe(new(report.ErrorId, "DeviceDisconnectUnconfirmed",
                "设备关闭结果未确认，原所有者已保留", "请检查诊断，不创建替代连接"));
        }
        finally { IsDisconnecting = false; DisconnectCommand.NotifyCanExecuteChanged(); }
    }
    private WindowsDeviceDiscoverySession? discovery;
    private long discoveryVersion = -1;
    private DeviceSelection? selectedDevice;
    private DeviceSelectionAdmission? selectedAdmission;
    public DeviceSelection? SelectedDevice => discovery?.IsSelectionCurrent(discoveryVersion) == true ? selectedDevice : null;
    public DeviceSelectionAdmission? SelectedAdmission => discovery?.IsSelectionCurrent(discoveryVersion) == true ? selectedAdmission : null;
    public ImmutableArray<DiscoveredDeviceItem> DiscoveredDevices { get; private set; } = [];
    [ObservableProperty] private string discoveryStatusText = "设备发现尚未启用";
    [ObservableProperty] private string discoveryErrorText = "";
    [ObservableProperty] private string selectionText = "未选择发现设备";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmSelectionCommand))]
    private DiscoveredDeviceItem? selectedCandidate;

    public void AttachDiscovery(WindowsDeviceDiscoverySession session)
    {
        discoveryVersion = -1;
        discovery = session;
        ApplyDiscovery(session.Current);
    }

    public void ApplyDiscovery(DeviceDiscoverySnapshot value)
    {
        if (value.Version <= discoveryVersion) { return; }
        discoveryVersion = value.Version;
        selectedDevice = null; selectedAdmission = null; SelectedCandidate = null; SelectionText = "未选择发现设备";
        DiscoveryErrorText = value.Error is { } error ? Describe(error) : "";
        DiscoveryStatusText = value.IsScanning ? "正在检测设备" : value.CanSelect
            ? value.Interfaces.IsEmpty ? "未发现设备" : "请选择设备" : value.Error is null ? "设备发现已停止" : "设备检测未完成";
        var groups = new Dictionary<DeviceKey, List<DeviceIdentity>>();
        foreach (var identity in value.Interfaces)
        {
            if (DeviceKey.FromSerialNumber(identity.VendorId, identity.SerialNumber) is not OperationResult<DeviceKey>.Success key)
            {
                DiscoveredDevices = [];
                DiscoveryErrorText = Describe(AppError.Create("InvalidDiscoveryPresentation", "发现身份不能形成永久设备键", "请重新检测设备"));
                NotifyDiscoveryChanged();
                return;
            }
            if (!groups.TryGetValue(key.Value, out var interfaces)) { interfaces = []; groups.Add(key.Value, interfaces); }
            interfaces.Add(identity);
        }
        DiscoveredDevices = groups.Select(group =>
        {
            var selection = DeviceSelectionResolver.Resolve(value.Interfaces, group.Key);
            string errorText = selection is OperationResult<DeviceSelection>.Failure failure ? Describe(failure.Error) : "";
            return new DiscoveredDeviceItem(group.Key, $"VID {group.Value[0].VendorId:X4} · {group.Value[0].SerialNumber}",
                string.Join(Environment.NewLine, group.Value.Select(identity => $"PID {identity.ProductId:X4} · 容器 {identity.ContainerId?.ToString("D") ?? "未确认"} · 位置 {identity.Location ?? "未提供"}")),
                value.CanSelect && selection is OperationResult<DeviceSelection>.Success, errorText);
        }).ToImmutableArray();
        NotifyDiscoveryChanged();
    }

    private void NotifyDiscoveryChanged()
    {
        OnPropertyChanged(nameof(DiscoveredDevices)); OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(SelectedAdmission));
        ConfirmSelectionCommand.NotifyCanExecuteChanged(); RescanCommand.NotifyCanExecuteChanged();
        ConnectProtocolCommand.NotifyCanExecuteChanged();
    }
    private bool CanRescan() => !stopping && !IsProtocolBusy && !IsSelecting && !IsDisconnecting && !IsRetryingPendingSave
        && !IsChangingStreaming && !sessions.HasRetainedSession && beginDiscovery is not null && discovery?.Current.IsScanning != true;
    [RelayCommand(CanExecute = nameof(CanRescan))]
    private async Task RescanAsync()
    {
        if (beginDiscovery is null) { return; }
        var current = coordinator.Current;
        if (current.Connection != ConnectionState.Disconnected && current.DeviceKey is { } key
            && current.PendingOperationId is null && !sessions.HasRetainedSession && !sessions.IsBusy)
        { coordinator.NotifyDisconnected(key, current.SessionGeneration); }
        await RunProtocolAsync(() => beginDiscovery(ProtocolToken), "设备检测已启动");
    }
    private bool CanConfirmSelection() => !stopping && !IsProtocolBusy && !IsSelecting && !IsDisconnecting && !IsRetryingPendingSave && !IsChangingStreaming && !sessions.HasRetainedSession && !sessions.IsBusy
        && coordinator.Current.Connection == ConnectionState.Disconnected
        && discovery is not null && SelectedCandidate is { IsSelectable: true } item
        && DiscoveredDevices.Contains(item) && discovery.IsSelectionCurrent(discoveryVersion);
    [RelayCommand(CanExecute = nameof(CanConfirmSelection))]
    private async Task ConfirmSelectionAsync()
    {
        if (!CanConfirmSelection() || discovery is null || SelectedCandidate is not { } item) { return; }
        IsSelecting = true;
        DeviceSnapshot? acceptedSelection = null;
        try
        {
            var selected = discovery.Select(discoveryVersion, item.Key);
            if (selected is not OperationResult<DeviceSelectionAdmission>.Success admission)
            {
                DiscoveryErrorText = selected is OperationResult<DeviceSelectionAdmission>.Failure failure ? Describe(failure.Error) : "设备选择未确认";
                return;
            }
            var result = await coordinator.SelectAsync(admission.Value, lifetime.Token);
            if (result is OperationResult<DeviceSnapshot>.Success accepted)
            {
                acceptedSelection = accepted.Value;
                if (stopping || !admission.Value.Validity.IsCurrent(admission.Value.Selection))
                {
                    var error = AppError.Create("DeviceSelectionExpiredAtPresentation", "选择完成时发现凭据已变化", "请重新检测并明确选择设备");
                    coordinator.NotifyDisconnected(item.Key, accepted.Value.SessionGeneration, error);
                    DiscoveryErrorText = Describe(error);
                    return;
                }
                selectedAdmission = admission.Value; selectedDevice = admission.Value.Selection;
                SelectionText = $"已选择：{item.Title}（未连接）"; DiscoveryErrorText = "";
            }
            else
            {
                DiscoveryErrorText = result switch
                {
                    OperationResult<DeviceSnapshot>.Failure failure => Describe(failure.Error),
                    OperationResult<DeviceSnapshot>.Unknown unknown => Describe(unknown.Error),
                    _ => "设备选择已取消"
                };
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DeviceSelectionCommand");
            var error = new AppError(report.ErrorId, "DeviceSelectionUnconfirmed", "设备选择未能确认", "请核实当前状态后重新检测");
            if (acceptedSelection?.DeviceKey is { } key)
            { coordinator.NotifyDisconnected(key, acceptedSelection.SessionGeneration, error); }
            DiscoveryErrorText = Describe(error);
        }
        finally { IsSelecting = false; NotifyDiscoveryChanged(); RefreshProtocolActions(); }
    }
    public async Task StopAsync()
    {
        stopping = true; ConfirmSelectionCommand.NotifyCanExecuteChanged(); RescanCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        RetryPendingSaveCommand.NotifyCanExecuteChanged();
        RefreshStreamingActions();
        await lifetime.CancelAsync();
        await StopProtocolAsync();
        if (ConfirmSelectionCommand.ExecutionTask is { } selecting) { await selecting; }
        if (DisconnectCommand.ExecutionTask is { } disconnecting) { await disconnecting; }
        if (RetryPendingSaveCommand.ExecutionTask is { } retrying) { await retrying; }
        if (PauseStreamingCommand.ExecutionTask is { } pausing) { await pausing; }
        if (ResumeStreamingCommand.ExecutionTask is { } resuming) { await resuming; }
        if (previewReading is { } reading) { await reading; }
    }
    private static string Describe(AppError error) => $"{error.Reason}。{error.Action}。错误编号：{error.ErrorId}";

    private DeviceSnapshot snapshot = DeviceSnapshot.Disconnected;
    private long appliedVersion = -1;
    public DeviceSnapshot Snapshot => snapshot;
    public string DeviceText => snapshot.Identity is { } identity
        ? $"VID {identity.VendorId:X4} · PID {identity.ProductId:X4} · {identity.SerialNumber ?? "序列号未确认"}" : "未选择设备";
    public string ResolutionText => snapshot.Capabilities is { IsProtocolMatched: true } verified
        ? $"{verified.Width} × {verified.Height}" : "尚未核实";
    public string FirmwareText => snapshot.Capabilities is { } capabilities
        ? capabilities.FirmwareVersion + (capabilities.IsVerified ? "" : "（源码协议基线，待联调）") : "尚未核实";
    public string BrightnessText => snapshot.ObservedBrightness is { } brightness ? $"{brightness}%" : "尚未读回";
    public string RequestedBrightnessText => snapshot.RequestedBrightness is { } brightness ? $"{brightness}%" : "无请求";
    public string OperationText => snapshot.PendingOperationId is null ? "无在途操作" : "正在处理";
    public string DeviceErrorText => snapshot.Error is { } error ? $"{error.Reason}。{error.Action}。错误编号：{error.ErrorId}" : "";
    [ObservableProperty] private string subscriptionErrorText = "";
    [ObservableProperty] private string lifecycleText = "";
    [ObservableProperty] private string lifecycleErrorText = "";
    private long lifecycleVersion = -1;

    public void ApplyLifecycle(SessionLifecycleSnapshot value)
    {
        RefreshPendingSave();
        RefreshStreamingActions();
        if (value.Version <= lifecycleVersion) { return; }
        lifecycleVersion = value.Version;
        ConfirmSelectionCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(DisconnectActionText));
        LifecycleText = value.Stage switch
        {
            SessionLifecycleStage.Closing => "正在收敛设备会话",
            SessionLifecycleStage.Finalizing => "设备资源已关闭，正在处理本地结果",
            SessionLifecycleStage.Released => "原设备会话已释放",
            SessionLifecycleStage.RetainedLocalResult => "设备资源已关闭，本地结果待处理",
            SessionLifecycleStage.Unknown => "设备会话关闭未确认，所有者已保留",
            _ => ""
        };
        LifecycleErrorText = value.Error is { } error ? Describe(error) : "";
    }

    public bool Apply(DeviceSnapshot value)
    {
        if (value.Version <= appliedVersion) { return false; }
        snapshot = value; appliedVersion = value.Version;
        RefreshProtocolActions();
        RefreshStreamingActions();
        ConfirmSelectionCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(string.Empty);
        return true;
    }
}
