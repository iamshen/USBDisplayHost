using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
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

    private void OnTemplatePreviewKeyDown(object sender, KeyEventArgs e)
    {
        int index = e.Key switch
        {
            Key.Left or Key.Up => Math.Max(0, TemplateSelector.SelectedIndex - 1),
            Key.Right or Key.Down => Math.Min(TemplateSelector.Items.Count - 1, TemplateSelector.SelectedIndex + 1),
            Key.Home => 0,
            Key.End => TemplateSelector.Items.Count - 1,
            _ => -1
        };
        if (index < 0) { return; }
        TemplateSelector.SetCurrentValue(ListBox.SelectedIndexProperty, index);
        if (TemplateSelector.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item) { item.Focus(); }
        e.Handled = true;
    }

    private void UpdateResponsiveLayout()
    {
        bool stacked = ContentGrid.ActualWidth < 780;
        bool compactHeight = EditorViewport.ActualHeight < 280;
        ContentGrid.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 320);
        Grid.SetColumn(FieldsPanel, stacked ? 0 : 1);
        Grid.SetRow(FieldsPanel, stacked ? 1 : 0);
        FieldsPanel.BorderThickness = stacked ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        FieldsPanel.Padding = stacked ? new Thickness(0, 24, 0, 0) : new Thickness(24, 8, 0, 0);
        PreviewPanel.Margin = stacked ? new Thickness(0, 0, 0, 24) : new Thickness(0, 0, 24, 0);
        double previewWidth = Math.Max(0, ContentGrid.ActualWidth - (stacked ? 0 : 344));
        double previewHeight;
        if (compactHeight)
        {
            // 短窗口让预览、来源说明和缩略图随主体滚动，底栏独立保留。
            previewHeight = Math.Clamp(EditorViewport.ActualHeight - 64, 112, 140);
        }
        else
        {
            // 主预览与三张等比缩略图共用列宽；按两者的比例一次计算，避免测量值相互反馈。
            double availableHeight = EditorViewport.ActualHeight - PageHeader.ActualHeight - PageHeader.Margin.Bottom - 84;
            previewHeight = Math.Clamp(availableHeight * 0.75, 180, stacked ? 240 : 360);
        }
        // 高度限制预览时收拢整个工作区，页头、画面、参数与底栏保持同一组对齐线。
        Workspace.MaxWidth = stacked || compactHeight ? 1120 : Math.Clamp(previewHeight * 16 / 9 + 356, 848, 1120);
        double frameWidth = Math.Min(previewWidth, previewHeight * 16 / 9);
        PreviewFrame.Width = frameWidth;
        PreviewFrame.Height = frameWidth * 9 / 16;
        TemplateSelector.MaxWidth = compactHeight || stacked ? previewWidth + 12 : frameWidth + 12;
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
