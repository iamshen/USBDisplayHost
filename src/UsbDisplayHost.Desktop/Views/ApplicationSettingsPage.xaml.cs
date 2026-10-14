using System.Windows.Controls;
using System.Windows;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Desktop.ViewModels;

namespace UsbDisplayHost.Desktop.Views;

public partial class ApplicationSettingsPage : Page
{
    private readonly ApplicationSettingsPageViewModel viewModel;
    private readonly IErrorReporter errorReporter;

    public ApplicationSettingsPage(ApplicationSettingsPageViewModel viewModel, IErrorReporter errorReporter)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.errorReporter = errorReporter;
        DataContext = viewModel;
        Loaded += OnLoaded;
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

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { await viewModel.InitializeAsync(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "ApplicationSettingsPageLoad");
            viewModel.ErrorText = $"应用设置页面初始化未完成。错误编号：{report.ErrorId}";
        }
    }
}
