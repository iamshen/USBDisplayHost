using System.Collections.Immutable;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Desktop.ViewModels;

namespace UsbDisplayHost.WinUI.Views;

public sealed partial class DevicesPage : Page
{
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DevicesPageViewModel? viewModel;
    private IErrorReporter? reporter;
    private ImmutableArray<DiscoveredDeviceItem> displayedCandidates = [];
    private bool uiOnly;
    private bool loaded;
    private bool pageVisible;
    private bool active;
    private EventHandler? themeRequested;

    public DevicesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        previewTimer.Tick += OnPreviewTick;
    }

    internal event EventHandler? DiagnosticsRequested;

    internal event EventHandler? ThemeRequested
    {
        add
        {
            themeRequested += value;
            ThemeButton.Visibility = VisibleWhen(themeRequested is not null);
        }
        remove
        {
            themeRequested -= value;
            ThemeButton.Visibility = VisibleWhen(themeRequested is not null);
        }
    }

    internal void InitializeView(DevicesPageViewModel viewModel, IErrorReporter reporter, bool uiOnly)
    {
        this.viewModel = viewModel;
        this.reporter = reporter;
        this.uiOnly = uiOnly;
        DataContext = viewModel;
        RefreshState(viewModel);
        UpdateActivation();
    }

    internal void SetPageVisible(bool visible)
    {
        pageVisible = visible;
        UpdateActivation();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        loaded = true;
        UpdateActivation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        loaded = false;
        UpdateActivation();
    }

    private void UpdateActivation()
    {
        if (viewModel is not { } model)
        {
            return;
        }

        bool shouldActivate = loaded && pageVisible;
        if (active == shouldActivate)
        {
            return;
        }

        active = shouldActivate;
        model.SetPreviewPageVisible(active);
        if (active)
        {
            model.PropertyChanged += OnViewModelPropertyChanged;
            RefreshState(model);
            if (!uiOnly)
            {
                previewTimer.Start();
                _ = RefreshPreviewAsync(model);
            }
        }
        else
        {
            previewTimer.Stop();
            model.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private async void OnPreviewTick(object? sender, object args)
    {
        if (active && viewModel is { } model)
        {
            await RefreshPreviewAsync(model);
        }
    }

    private async Task RefreshPreviewAsync(DevicesPageViewModel model)
    {
        try
        {
            await model.RefreshHostPreviewAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            previewTimer.Stop();
            var report = reporter!.Report(exception, "DevicePreviewRefresh");
            model.HostPreviewErrorText = $"主机预览刷新未完成，请检查诊断。错误编号：{report.ErrorId}";
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is DevicesPageViewModel model && args.PropertyName is null or ""
            or nameof(DevicesPageViewModel.Snapshot) or nameof(DevicesPageViewModel.DiscoveredDevices)
            or nameof(DevicesPageViewModel.HasRetainedLocalResult) or nameof(DevicesPageViewModel.IsProtocolBusy)
            or nameof(DevicesPageViewModel.DiscoveryStatusText) or nameof(DevicesPageViewModel.DiscoveryErrorText)
            or nameof(DevicesPageViewModel.ProtocolText) or nameof(DevicesPageViewModel.ProtocolErrorText)
            or nameof(DevicesPageViewModel.PendingSaveText) or nameof(DevicesPageViewModel.SubscriptionErrorText))
        {
            RefreshState(model);
        }
    }

    private void RefreshState(DevicesPageViewModel model)
    {
        var snapshot = model.Snapshot;
        bool hasCandidates = !model.DiscoveredDevices.IsEmpty;
        bool handoff = model.HasRetainedLocalResult;
        var occupied = new[] { model.ProtocolError, model.DiscoveryError, snapshot.Error }
            .FirstOrDefault(error => error?.Code == "UsbInterfaceInUse");
        bool connected = !handoff && snapshot.Connection == ConnectionState.Ready;
        bool poweredOff = connected && snapshot.Power == PowerState.Off;
        bool showWorkspace = connected && !poweredOff;
        bool detecting = model.DiscoveryStatusText == "正在检测设备";

        ConnectionHeading.Text = handoff ? "设备已断开"
            : occupied is not null ? "USB 接口被占用"
            : poweredOff ? "屏幕已关闭"
            : detecting ? "正在检测设备…"
            : snapshot.Connection switch
        {
            ConnectionState.Discovered => "检测到设备",
            ConnectionState.Opening => "正在连接设备",
            ConnectionState.Recovering => "正在恢复连接",
            ConnectionState.Faulted => "设备连接异常",
            _ => hasCandidates ? "检测到设备" : "未检测到设备"
        };
        HeroBadge.Text = model.RetainedLocalError is { } localError ? $"本地交接待完成 · {localError.ErrorId}"
            : occupied is not null ? $"连接失败 · {occupied.ErrorId}"
            : poweredOff ? "已连接" : string.Empty;
        HeroBadge.Visibility = VisibleWhen(HeroBadge.Text.Length > 0);
        ConnectionHeading.Margin = new Thickness(0, HeroBadge.Text.Length > 0 ? 0 : 42, 0, 0);
        StateHero.Visibility = VisibleWhen(!showWorkspace);
        ConnectedHeading.Visibility = CurrentWorkspace.Visibility = ConnectionDetails.Visibility = VisibleWhen(showWorkspace);
        DeviceNotices.Visibility = VisibleWhen(model.IsProtocolBusy || occupied is null
            && (model.ProtocolText.Length > 0 || model.ProtocolErrorText.Length > 0
                || model.DeviceErrorText.Length > 0 || model.SubscriptionErrorText.Length > 0));
        ConnectedStatus.Text = snapshot.Mode switch
        {
            DeviceMode.Theme => "已连接 · 主题模式",
            DeviceMode.SecondaryDisplay => "已连接 · 副屏模式",
            _ => "已连接"
        };
        DisconnectButton.Visibility = VisibleWhen(handoff || poweredOff
            || snapshot.Connection is ConnectionState.Recovering or ConnectionState.Faulted);
        HeroWakeButton.Visibility = VisibleWhen(poweredOff);
        RescanButton.Visibility = VisibleWhen(!handoff && !poweredOff);
        RescanButton.Content = detecting ? "正在检测" : "重新检测";
        ThemeButton.Visibility = VisibleWhen(themeRequested is not null && occupied is null && !handoff && !poweredOff);
        HeroDiagnostics.Visibility = VisibleWhen(occupied is not null || handoff || snapshot.Connection == ConnectionState.Faulted);
        HeroRetrySave.Visibility = VisibleWhen(handoff && model.PendingSaveText.Length > 0);
        RetryPendingSaveButton.Visibility = VisibleWhen(!handoff && model.PendingSaveText.Length > 0);
        NoDeviceHelp.Text = handoff ? model.PendingSaveText.Length > 0
                ? "先重试保存恢复配置，再完成本地交接。" : "完成本地交接后可重新连接。"
            : occupied is not null ? "在占用该设备的程序中断开连接，然后重新检测。"
            : poweredOff ? "USB Display 仍保持连接。"
            : detecting ? "请保持 USB 数据线连接。"
            : hasCandidates ? $"发现 {model.DiscoveredDevices.Length} 台 USB 显示设备。"
            : uiOnly ? "无设备试用模式，USB 检测已停用。"
            : "接通电源，插入 USB 数据线。";

        if (displayedCandidates != model.DiscoveredDevices)
        {
            displayedCandidates = model.DiscoveredDevices;
            CandidateList.ItemsSource = displayedCandidates.ToArray();
        }
        CandidatePanel.Visibility = VisibleWhen(hasCandidates);
        DiscoveryPanel.Visibility = VisibleWhen(!handoff && !poweredOff && occupied is null
            && (hasCandidates || !string.IsNullOrEmpty(model.DiscoveryErrorText)));

        ModeText.Text = snapshot.Mode switch
        {
            DeviceMode.Theme => "主题",
            DeviceMode.SecondaryDisplay => "副屏",
            _ => "未知"
        };
        PowerText.Text = snapshot.Power switch
        {
            PowerState.On => "已亮屏",
            PowerState.Off => "已熄屏",
            _ => "未知"
        };
        StreamingText.Text = snapshot.Streaming switch
        {
            StreamingState.Running => "发送中",
            StreamingState.Paused => "已暂停",
            StreamingState.Faulted => "故障",
            _ => "已停止"
        };
        SwitchThemeButton.Visibility = VisibleWhen(snapshot.Mode == DeviceMode.SecondaryDisplay);
        SwitchScreenButton.Visibility = VisibleWhen(snapshot.Mode == DeviceMode.Theme);
        SleepButton.Visibility = VisibleWhen(snapshot.Power == PowerState.On);
        PauseButton.Visibility = VisibleWhen(snapshot.Streaming == StreamingState.Running);
        ResumeButton.Visibility = VisibleWhen(snapshot.Streaming == StreamingState.Paused);
    }

    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool stacked = args.NewSize.Width < 820;
        CurrentWorkspace.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(SessionPanel, stacked ? 0 : 1);
        Grid.SetRow(SessionPanel, stacked ? 1 : 0);
        CurrentWorkspace.ColumnSpacing = stacked ? 0 : 52;
        SessionPanel.Margin = stacked ? new Thickness() : new Thickness(0, 30, 0, 0);
    }

    private void OnThemeClick(object sender, RoutedEventArgs args) => themeRequested?.Invoke(this, EventArgs.Empty);

    private void OnDiagnosticsClick(object sender, RoutedEventArgs args) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);

    private static Visibility VisibleWhen(bool condition) => condition ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class DeviceValueVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true || value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
