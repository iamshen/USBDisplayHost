using System.Windows.Controls;
using System.Windows;
using UsbDisplayHost.Desktop.ViewModels;
using UsbDisplayHost.Core.Diagnostics;

namespace UsbDisplayHost.Desktop.Views;

public partial class ThemesPage : Page
{
    private readonly ThemePageViewModel viewModel;
    private readonly IErrorReporter errorReporter;

    public ThemesPage(ThemePageViewModel viewModel, IErrorReporter errorReporter)
    {
        this.viewModel = viewModel;
        this.errorReporter = errorReporter;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            viewModel.SetPageVisible(true);
            await viewModel.InitializeAsync();
            viewModel.RefreshDeviceActions();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "ThemePageLoaded");
            viewModel.ErrorText = $"主题页加载未完成，请重试。错误编号：{report.ErrorId}";
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => viewModel.SetPageVisible(false);
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => viewModel.SetPageVisible(IsVisible);

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();
    private void OnEditorViewportSizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();

    private void UpdateResponsiveLayout()
    {
        bool stacked = ContentGrid.ActualWidth < 780;
        bool compactHeight = EditorViewport.ActualHeight < 280;
        ContentGrid.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 320);
        Grid.SetColumn(FieldsPanel, stacked ? 0 : 1);
        Grid.SetRow(FieldsPanel, stacked ? 1 : 0);
        PreviewPanel.Margin = stacked ? new Thickness(0, 0, 0, 24) : new Thickness(0, 0, 24, 0);
        if (compactHeight)
        {
            // 短窗口保留预览和来源说明的滚动空间。
            PreviewViewport.MaxHeight = Math.Clamp(EditorViewport.ActualHeight - 64, 112, 140);
        }
        else
        {
            double availablePreviewHeight = EditorViewport.ActualHeight - PageHeader.ActualHeight - PageHeader.Margin.Bottom
                - TemplateSelector.ActualHeight - TemplateSelector.Margin.Bottom - 64;
            PreviewViewport.MaxHeight = Math.Clamp(availablePreviewHeight, 180, stacked ? 240 : 360);
        }
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
