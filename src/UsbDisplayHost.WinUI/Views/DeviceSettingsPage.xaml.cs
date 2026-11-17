using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Desktop.ViewModels;

namespace UsbDisplayHost.WinUI.Views;

public sealed partial class DeviceSettingsPage : Page
{
    private DeviceSettingsPageViewModel? viewModel;
    private bool subscribed;
    private bool synchronizingQuality;
    private bool synchronizingBrightness;

    public DeviceSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    internal void InitializeView(DeviceSettingsPageViewModel viewModel)
    {
        this.viewModel = viewModel;
        RotationBox.ItemsSource = viewModel.Rotations.ToArray();
        FrameRateBox.ItemsSource = viewModel.FrameRates.ToArray();
        DataContext = viewModel;
        RefreshRotation(viewModel);
        RefreshFrameRate(viewModel);
        RefreshJpegQuality(viewModel);
        RefreshBrightnessHint(viewModel);
        ApplyOutputButton.Content = viewModel.IsApplying ? "正在应用…" : "应用图像输出";
        if (IsLoaded)
        {
            Subscribe(viewModel);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (viewModel is { } model)
        {
            Subscribe(model);
            RefreshRotation(model);
            RefreshFrameRate(model);
            RefreshJpegQuality(model);
            RefreshBrightnessHint(model);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (subscribed && viewModel is { } model)
        {
            model.PropertyChanged -= OnSettingsChanged;
            model.DeviceControls.PropertyChanged -= OnDeviceControlsChanged;
            subscribed = false;
        }
    }

    private void Subscribe(DeviceSettingsPageViewModel model)
    {
        if (!subscribed)
        {
            model.PropertyChanged += OnSettingsChanged;
            model.DeviceControls.PropertyChanged += OnDeviceControlsChanged;
            subscribed = true;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is DeviceSettingsPageViewModel model)
        {
            if (args.PropertyName is null or "" or nameof(DeviceSettingsPageViewModel.Rotation))
            {
                RefreshRotation(model);
            }
            if (args.PropertyName is null or "" or nameof(DeviceSettingsPageViewModel.JpegQuality))
            {
                RefreshJpegQuality(model);
            }
            if (args.PropertyName is null or "" or nameof(DeviceSettingsPageViewModel.IsConnected))
            {
                RefreshRotation(model);
                RefreshFrameRate(model);
                RefreshJpegQuality(model);
                RefreshBrightnessHint(model);
            }
            if (args.PropertyName == nameof(DeviceSettingsPageViewModel.FramesPerSecond)) { RefreshFrameRate(model); }
            if (args.PropertyName is null or "" or nameof(DeviceSettingsPageViewModel.IsApplying))
            {
                ApplyOutputButton.Content = model.IsApplying ? "正在应用…" : "应用图像输出";
            }
        }
    }

    private void RefreshRotation(DeviceSettingsPageViewModel model) =>
        RotationBox.SelectedItem = model.IsConnected ? model.Rotations.FirstOrDefault(option => option.Value == model.Rotation) : null;

    private void RefreshFrameRate(DeviceSettingsPageViewModel model) =>
        FrameRateBox.SelectedItem = model.IsConnected ? model.FramesPerSecond : null;

    private void OnFrameRateChanged(object sender, SelectionChangedEventArgs args)
    {
        if (viewModel is { CanEdit: true } model && FrameRateBox.SelectedItem is int value)
        {
            model.FramesPerSecond = value;
        }
    }

    private void OnRotationChanged(object sender, SelectionChangedEventArgs args)
    {
        if (viewModel is { CanEdit: true } model && RotationBox.SelectedItem is RotationOption option)
        {
            model.Rotation = option.Value;
        }
    }

    private void OnJpegQualityChanged(object sender, TextChangedEventArgs args)
    {
        if (!synchronizingQuality && viewModel is { CanEdit: true } model)
        {
            synchronizingQuality = true;
            try
            {
                model.JpegQuality = int.TryParse(JpegQualityInput.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int value)
                    ? value
                    : double.NaN;
            }
            finally
            {
                synchronizingQuality = false;
            }
        }
    }

    private void RefreshJpegQuality(DeviceSettingsPageViewModel model)
    {
        if (synchronizingQuality || double.IsNaN(model.JpegQuality))
        {
            return;
        }

        synchronizingQuality = true;
        try
        {
            JpegQualityInput.Text = model.IsConnected ? model.JpegQuality.ToString(CultureInfo.CurrentCulture) : string.Empty;
        }
        finally
        {
            synchronizingQuality = false;
        }
    }

    private void OnDeviceControlsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (viewModel is { } model && args.PropertyName is null or ""
            or nameof(DevicesPageViewModel.CanAdjustBrightness) or nameof(DevicesPageViewModel.BrightnessValue))
        {
            RefreshBrightnessHint(model);
        }
        if (viewModel is { } current && args.PropertyName == nameof(DevicesPageViewModel.BrightnessTarget))
        {
            RefreshBrightnessTarget(current);
        }
    }

    private void RefreshBrightnessHint(DeviceSettingsPageViewModel model)
    {
        var controls = model.DeviceControls;
        string brightness = model.IsConnected ? controls.BrightnessValue : string.Empty;
        synchronizingBrightness = true;
        try
        {
            if (BrightnessInput.Text != brightness) { BrightnessInput.Text = brightness; }
        }
        finally { synchronizingBrightness = false; }
        OfflineReason.Text = model.IsConnected ? string.Empty : "连接设备后可调整图像输出和屏幕亮度。";
        OfflineReason.Visibility = model.IsConnected ? Visibility.Collapsed : Visibility.Visible;
        FooterStatus.Visibility = BrightnessSlider.Visibility = model.IsConnected ? Visibility.Visible : Visibility.Collapsed;
        OfflineBrightnessTrack.Visibility = model.IsConnected ? Visibility.Collapsed : Visibility.Visible;
        RefreshBrightnessTarget(model);
        BrightnessHint.Text = controls.CanAdjustBrightness
            ? int.TryParse(controls.BrightnessValue, out int value) && value is >= 0 and <= 100
                ? "拖动滑块即时应用。"
                : "精确亮度请输入 0 到 100 的整数。"
            : controls.Snapshot switch
            {
                { Connection: not ConnectionState.Ready } => "连接设备后可调整亮度。",
                { Mode: not DeviceMode.Theme } => "切换到主题模式后可调整亮度。",
                { ObservedBrightness: null } => "等待读取设备亮度。",
                _ when controls.RetryProtocolSaveCommand.CanExecute(null) => "请先完成上次设备设置的本地保存。",
                _ => "设备操作结束后可调整亮度。"
            };
        BrightnessHint.Visibility = model.IsConnected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshBrightnessTarget(DeviceSettingsPageViewModel model) =>
        BrightnessTargetValue.Text = model.IsConnected && model.DeviceControls.Snapshot.ObservedBrightness is not null
            ? $"{model.DeviceControls.BrightnessTarget:0}%" : "—";

    private void OnBrightnessInputChanged(object sender, TextChangedEventArgs args)
    {
        if (!synchronizingBrightness && viewModel?.DeviceControls is { CanAdjustBrightness: true } controls)
        {
            controls.BrightnessValue = BrightnessInput.Text;
        }
    }

    private void OnOutputSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool stacked = args.NewSize.Width < 710;
        OutputSections.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1.09, GridUnitType.Star);
        OutputSections.ColumnSpacing = stacked ? 0 : 34;
        OutputSections.RowSpacing = stacked ? 28 : 0;
        Grid.SetColumn(BrightnessAndInfo, stacked ? 0 : 1);
        Grid.SetRow(BrightnessAndInfo, stacked ? 1 : 0);
        BrightnessAndInfo.BorderThickness = stacked ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        BrightnessAndInfo.Padding = stacked ? new Thickness(0, 18, 0, 0) : new Thickness(33, 0, 0, 0);
    }
}
