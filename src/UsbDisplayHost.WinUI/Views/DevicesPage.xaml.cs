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
        DiscoveryPanel.Visibility = VisibleWhen(!uiOnly);
        RescanButton.Visibility = VisibleWhen(!uiOnly);
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
            or nameof(DevicesPageViewModel.HasRetainedLocalResult))
        {
            RefreshState(model);
        }
    }

    private void RefreshState(DevicesPageViewModel model)
    {
        var snapshot = model.Snapshot;
        bool hasSessionStatus = snapshot.DeviceKey is not null
            && snapshot.Connection is ConnectionState.Opening or ConnectionState.Ready or ConnectionState.Recovering or ConnectionState.Faulted;
        bool hasCandidates = !model.DiscoveredDevices.IsEmpty;
        bool hasRetainedLocalResult = model.HasRetainedLocalResult;

        ConnectionHeading.Text = snapshot.Connection switch
        {
            ConnectionState.Discovered => "设备待连接",
            ConnectionState.Opening => "正在连接设备",
            ConnectionState.Ready => "设备已连接",
            ConnectionState.Recovering => "正在恢复连接",
            ConnectionState.Faulted => "设备连接异常",
            _ => "未连接设备"
        };
        CurrentDeviceText.Visibility = VisibleWhen(hasSessionStatus);
        EmptyDeviceIcon.Visibility = VisibleWhen(!hasSessionStatus && !hasCandidates);
        CurrentWorkspace.Visibility = VisibleWhen(hasSessionStatus);
        ConnectionDetails.Visibility = VisibleWhen(hasSessionStatus);
        DisconnectButton.Visibility = VisibleWhen(hasSessionStatus || hasRetainedLocalResult);
        ThemeButton.Content = hasSessionStatus ? "编辑主题" : "先预览主题";
        NoDeviceHelp.Visibility = VisibleWhen(!hasSessionStatus && !hasCandidates && !hasRetainedLocalResult);
        NoDeviceHelp.Text = uiOnly ? "当前以无设备模式运行。"
            : "为设备接通电源并使用支持数据传输的 USB 线缆，然后重新检测。";

        if (displayedCandidates != model.DiscoveredDevices)
        {
            displayedCandidates = model.DiscoveredDevices;
            CandidateList.ItemsSource = displayedCandidates.ToArray();
        }
        CandidatePanel.Visibility = VisibleWhen(hasCandidates);

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
        ModeUnavailableReason.Visibility = VisibleWhen(snapshot.Mode != DeviceMode.Unknown && snapshot.Power == PowerState.Off);
        SleepButton.Visibility = VisibleWhen(snapshot.Power == PowerState.On);
        WakeButton.Visibility = VisibleWhen(snapshot.Power == PowerState.Off);
        PauseButton.Visibility = VisibleWhen(snapshot.Streaming == StreamingState.Running);
        ResumeButton.Visibility = VisibleWhen(snapshot.Streaming == StreamingState.Paused);
    }

    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool stacked = args.NewSize.Width < 820;
        CurrentWorkspace.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(SessionPanel, stacked ? 0 : 1);
        Grid.SetRow(SessionPanel, stacked ? 1 : 0);
        CurrentWorkspace.ColumnSpacing = stacked ? 0 : 16;
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
