using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Desktop.ViewModels;
using Windows.System;
using Windows.UI;

namespace UsbDisplayHost.WinUI.Views;

public sealed partial class ThemesPage : UserControl
{
    private const double SettingsWidth = 304;
    private const double EditorGap = 24;
    private const double StackWidth = 780;
    private const double PreviewHeaderAndTemplatesHeight = 172;
    private static readonly ThemeColorSwatch[] Presets =
    [
        new("黑色", "#000000"), new("白色", "#FFFFFF"), new("深蓝", "#152035"),
        new("蓝色", "#2563EB"), new("绿色", "#16A34A"), new("橙色", "#F59E0B")
    ];

    private ThemePageViewModel? viewModel;
    private IErrorReporter? reporter;
    private bool selected;
    private bool active;
    private bool synchronizing;

    public ThemesPage()
    {
        InitializeComponent();
        BackgroundPalette.ItemsSource = Presets;
        ForegroundPalette.ItemsSource = Presets;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    internal void InitializeView(ThemePageViewModel viewModel, IErrorReporter reporter)
    {
        if (this.viewModel is not null) { throw new InvalidOperationException("主题页已经初始化"); }
        this.viewModel = viewModel;
        this.reporter = reporter;
        FitBox.ItemsSource = viewModel.Fits.ToArray();
        DataContext = viewModel;
        if (IsLoaded)
        {
            viewModel.PropertyChanged += OnViewModelChanged;
            UpdateActivity();
        }
    }

    internal void SetPageVisible(bool visible)
    {
        if (selected == visible) { return; }
        selected = visible;
        UpdateActivity();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (viewModel is { } model)
        {
            model.PropertyChanged += OnViewModelChanged;
            RefreshPresentation(model);
        }
        UpdateEditorLayout();
        UpdateActivity();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        active = false;
        if (viewModel is { } model)
        {
            model.PropertyChanged -= OnViewModelChanged;
            model.SetPageVisible(false);
        }
    }

    private void UpdateActivity()
    {
        if (viewModel is not { } model) { return; }
        bool visible = selected && IsLoaded;
        if (active == visible) { return; }
        active = visible;
        model.SetPageVisible(visible);
        if (visible) { _ = ActivateAsync(model); }
    }

    private async Task ActivateAsync(ThemePageViewModel model)
    {
        try
        {
            await model.InitializeAsync();
            if (!active) { return; }
            model.RefreshDeviceActions();
            RefreshPresentation(model);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter!.Report(exception, "ThemePageLoaded");
            model.ErrorText = $"主题页加载未完成，请重试。错误编号：{report.ErrorId}";
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (viewModel is not { } model) { return; }
        if (args.PropertyName is nameof(ThemePageViewModel.SelectedTemplate)
            or nameof(ThemePageViewModel.Fit)
            or nameof(ThemePageViewModel.SelectedTemplatePreview)
            or nameof(ThemePageViewModel.ImageTemplatePreview)
            or nameof(ThemePageViewModel.BackgroundColor)
            or nameof(ThemePageViewModel.ForegroundColor)
            or nameof(ThemePageViewModel.ValidationText)
            or nameof(ThemePageViewModel.PreviewErrorText))
        {
            RefreshPresentation(model);
        }
    }

    private void RefreshPresentation(ThemePageViewModel model)
    {
        var current = model.SelectedTemplatePreview as ImageSource;
        PreviewImage.Source = current;
        PictureThumbnailImage.Source = model.IsImage ? current : model.ImageTemplatePreview as ImageSource;
        ClockThumbnailImage.Source = model.IsClock ? current : null;
        DashboardThumbnailImage.Source = model.IsDashboard ? current : null;
        PreviewStatus.Visibility = current is null ? Visibility.Visible : Visibility.Collapsed;
        PreviewStatus.Text = !string.IsNullOrEmpty(model.ValidationText) ? "修正输入后可预览"
            : !string.IsNullOrEmpty(model.PreviewErrorText) ? "本地预览未生成" : "正在生成本地预览…";
        ForegroundColorEntry.Visibility = model.IsImage ? Visibility.Collapsed : Visibility.Visible;

        synchronizing = true;
        try
        {
            int selectedIndex = (int)model.SelectedTemplate;
            TemplateSelector.SelectedIndex = selectedIndex;
            FieldsHeading.Text = model.Templates[selectedIndex].Name;
            FitBox.SelectedItem = model.Fits.FirstOrDefault(option => option.Value == model.Fit);
            BackgroundPalette.SelectedItem = FindPreset(model.BackgroundColor);
            ForegroundPalette.SelectedItem = FindPreset(model.ForegroundColor);
        }
        finally { synchronizing = false; }
    }

    private void OnTemplateChanged(object sender, SelectionChangedEventArgs args)
    {
        if (synchronizing || viewModel is not { CanEdit: true } model) { return; }
        int index = TemplateSelector.SelectedIndex;
        if (index >= 0 && index < model.Templates.Count) { model.SelectedTemplate = model.Templates[index].Value; }
    }

    private void OnFitChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!synchronizing && viewModel is { CanEdit: true } model && FitBox.SelectedItem is FitOption option)
        {
            model.Fit = option.Value;
        }
    }

    private void OnPaletteSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (synchronizing || viewModel is not { CanEdit: true } model
            || args.AddedItems.FirstOrDefault() is not ThemeColorSwatch swatch) { return; }

        if (ReferenceEquals(sender, BackgroundPalette)) { model.BackgroundColor = swatch.Hex; }
        else if (ReferenceEquals(sender, ForegroundPalette)) { model.ForegroundColor = swatch.Hex; }
    }

    private static ThemeColorSwatch? FindPreset(string value) =>
        Array.Find(Presets, preset => string.Equals(preset.Hex, value, StringComparison.OrdinalIgnoreCase));

    private void OnPaletteContainerChanged(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is ThemeColorSwatch swatch)
        {
            AutomationProperties.SetName(args.ItemContainer, swatch.Label);
        }
    }

    private void OnColorPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape && sender is Expander { IsExpanded: true } expander)
        {
            expander.IsExpanded = false;
            expander.Focus(FocusState.Programmatic);
            args.Handled = true;
        }
    }

    private void OnEditorSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (IsLoaded) { UpdateEditorLayout(); }
    }

    private void UpdateEditorLayout()
    {
        double available = EditorLayout.ActualWidth;
        if (available <= 0) { return; }
        bool stacked = available < StackWidth;
        EditorLayout.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : SettingsWidth);
        EditorLayout.ColumnSpacing = stacked ? 0 : EditorGap;
        EditorLayout.RowSpacing = stacked ? EditorGap : 0;
        Grid.SetColumn(FieldsBoundary, stacked ? 0 : 1);
        Grid.SetRow(FieldsBoundary, stacked ? 1 : 0);
        FieldsBoundary.Padding = stacked ? new Thickness(0, 16, 0, 0) : new Thickness(EditorGap, 0, 0, 0);
        FieldsBoundary.BorderThickness = stacked ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);

        double previewWidth = stacked ? available : available - SettingsWidth - EditorGap;
        double viewportHeight = EditorScroll.ActualHeight;
        // 先为预览标题和一行模板留出空间，让短视口首屏显示完整的模板名称。
        double heightLimit = Math.Clamp(
            Math.Min(viewportHeight * 0.61, viewportHeight - PreviewHeaderAndTemplatesHeight), 150, 360);
        double frameWidth = Math.Min(previewWidth, heightLimit * 16 / 9);
        PreviewFrame.Width = frameWidth;
        PreviewFrame.Height = frameWidth * 9 / 16;

        int columns = frameWidth < 340 ? 1 : 3;
        double thumbWidth = Math.Clamp((frameWidth - columns * 40) / columns, 40, 156);
        TemplateSelector.MaxColumns = columns;
        TemplateSelector.MaxWidth = frameWidth;
        foreach (var thumbnail in new[] { PictureThumbnail, ClockThumbnail, DashboardThumbnail })
        {
            thumbnail.Width = thumbWidth;
            thumbnail.Height = thumbWidth * 9 / 16;
        }
    }
}

public sealed record ThemeColorSwatch(string Name, string Hex)
{
    public string Label => $"{Name} · {Hex}";
    public SolidColorBrush Brush { get; } = ThemeColorBrushConverter.Parse(Hex);
}

public sealed class ThemeColorBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        Parse(value as string ?? string.Empty);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    internal static SolidColorBrush Parse(string text) => ThemeFields.TryColor(text, out var color)
        ? new SolidColorBrush(Color.FromArgb(255, color.Red, color.Green, color.Blue))
        : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
}
