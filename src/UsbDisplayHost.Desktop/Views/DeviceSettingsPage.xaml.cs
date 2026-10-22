using System.Windows;
using System.Windows.Controls;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Desktop.ViewModels;

namespace UsbDisplayHost.Desktop.Views;

public partial class DeviceSettingsPage : Page
{
    private readonly DeviceSettingsPageViewModel viewModel;
    private readonly IErrorReporter reporter;

    public DeviceSettingsPage(DeviceSettingsPageViewModel viewModel, IErrorReporter reporter)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.reporter = reporter;
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { await viewModel.InitializeAsync(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DeviceSettingsPageLoad");
            viewModel.ErrorText = $"设备设置读取未完成。错误编号：{report.ErrorId}";
        }
    }

    private void OnOutputLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool stacked = e.NewSize.Width < 640;
        OutputLayout.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(OutputParameters, stacked ? 0 : 1);
        Grid.SetRow(OutputParameters, stacked ? 1 : 0);
        DirectionSection.Margin = stacked ? new Thickness(0, 0, 0, 24) : new Thickness(0, 0, 32, 0);
    }

    private void OnBrightnessLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool stacked = e.NewSize.Width < 440;
        Grid.SetColumnSpan(BrightnessSliderArea, stacked ? 2 : 1);
        Grid.SetColumn(BrightnessInput, stacked ? 0 : 1);
        Grid.SetRow(BrightnessInput, stacked ? 1 : 0);
        BrightnessSliderArea.Margin = stacked ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 24, 0);
    }

    private void OnActionBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool stacked = e.NewSize.Width < 560;
        Grid.SetColumnSpan(ActionStatus, stacked ? 2 : 1);
        Grid.SetColumn(PrimaryActions, stacked ? 0 : 1);
        Grid.SetColumnSpan(PrimaryActions, stacked ? 2 : 1);
        Grid.SetRow(PrimaryActions, stacked ? 1 : 0);
        ActionStatus.Margin = stacked ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 16, 0);
    }
}
