using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Desktop.ViewModels;

namespace UsbDisplayHost.WinUI.Views;

public sealed partial class DeviceSettingsPage : Page
{
    private DeviceSettingsPageViewModel? viewModel;
    private bool subscribed;
    private bool synchronizingQuality;

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
        RefreshJpegQuality(viewModel);
        RefreshBrightnessHint(viewModel);
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
                RefreshBrightnessHint(model);
            }
        }
    }

    private void RefreshRotation(DeviceSettingsPageViewModel model) =>
        RotationBox.SelectedItem = model.Rotations.FirstOrDefault(option => option.Value == model.Rotation);

    private void OnRotationChanged(object sender, SelectionChangedEventArgs args)
    {
        if (viewModel is { CanEdit: true } model && RotationBox.SelectedItem is RotationOption option)
        {
            model.Rotation = option.Value;
        }
    }

    private void OnJpegQualityChanged(object sender, TextChangedEventArgs args)
    {
        if (!synchronizingQuality && viewModel is { } model)
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
            JpegQualityInput.Text = model.JpegQuality.ToString(CultureInfo.CurrentCulture);
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
    }

    private void RefreshBrightnessHint(DeviceSettingsPageViewModel model)
    {
        var controls = model.DeviceControls;
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
    }
}

public sealed class DisplayRotationAngleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is DisplayRotation rotation ? (double)(int)rotation : 0d;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
