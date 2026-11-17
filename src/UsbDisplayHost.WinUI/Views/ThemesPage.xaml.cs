using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Themes;
using UsbDisplayHost.Desktop.ViewModels;
using UsbDisplayHost.Windows.Storage;
using Windows.Foundation;
using Windows.System;

namespace UsbDisplayHost.WinUI.Views;

public sealed partial class ThemesPage : UserControl
{
    private sealed record GalleryItem(string Name, ThemeContent Content, Guid? SavedId, string? ImagePath);
    private sealed record EditorUndo(ThemeContent.Scene Scene, int Selection);
    private static readonly IReadOnlyList<GalleryItem> Presets = ThemePresets.All
        .Select(preset => new GalleryItem(preset.Name, preset.Content, null, preset.ImagePath)).ToArray();

    private ThemePageViewModel? viewModel;
    private FileProfileStore? library;
    private IErrorReporter? reporter;
    private IReadOnlyList<GalleryItem> galleryItems = [];
    private readonly List<(Button Button, Border Image)> cards = [];
    private readonly Stack<EditorUndo> undo = new();
    private ThemeContent.Scene? scene;
    private ThemeContent.Scene? editorBaseline;
    private GalleryItem? openedItem;
    private int galleryIndex;
    private int elementIndex = -1;
    private int filter;
    private int columnCount;
    private bool showLibrary;
    private bool editing;
    private bool pageVisible;
    private bool refreshing;
    private bool opening;
    private bool saving;
    private uint? dragPointer;
    private Point dragOrigin;
    private EditorUndo? dragStart;
    private bool dragMoved;

    public ThemesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    internal void InitializeView(ThemePageViewModel model, FileProfileStore profiles, IErrorReporter errors)
    {
        viewModel = model;
        library = profiles;
        reporter = errors;
        DataContext = model;
        if (IsLoaded) { model.PropertyChanged += OnModelChanged; }
        ShowPresets();
    }

    internal void SetPageVisible(bool visible)
    {
        pageVisible = visible;
        UpdatePreviewActivity();
        if (visible && viewModel is not null) { _ = viewModel.InitializeAsync(); }
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (viewModel is not null) { viewModel.PropertyChanged += OnModelChanged; }
        UpdatePreviewActivity();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (viewModel is null) { return; }
        viewModel.PropertyChanged -= OnModelChanged;
        viewModel.SetPageVisible(false);
    }

    private void UpdatePreviewActivity() => viewModel?.SetPageVisible(pageVisible && editing && IsLoaded);

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (viewModel is not { } model) { return; }
        if (args.PropertyName == nameof(ThemePageViewModel.SelectedTemplatePreview)
            && model.SelectedTemplatePreview is ImageSource image)
        {
            PreviewImage.Source = image;
        }
        if (args.PropertyName == nameof(ThemePageViewModel.PreviewErrorText) && model.PreviewErrorText.Length > 0)
        {
            PreviewImage.Source = null;
        }
        if (args.PropertyName == nameof(ThemePageViewModel.CanEdit)) { RefreshEditorEnabled(); }
    }

    private void OnPresetTabClick(object sender, RoutedEventArgs args) => ShowPresets();

    private async void OnMineTabClick(object sender, RoutedEventArgs args) => await ShowMineAsync();

    private void OnFilterClick(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleButton { Tag: string value } && int.TryParse(value, out int selected))
        {
            filter = selected;
            ShowPresets();
        }
    }

    private void ShowPresets()
    {
        showLibrary = false;
        LibraryStatus.Text = string.Empty;
        UpdateGallery(filter == 0 ? Presets : Presets.Where((_, index) => ThemePresets.All[index].Category == filter).ToArray());
    }

    private async Task ShowMineAsync(Guid? selectedId = null)
    {
        if (library is null) { return; }
        showLibrary = true;
        LibraryStatus.Text = "正在读取主题…";
        UpdateGallery([]);
        var result = await library.LoadThemesAsync(CancellationToken.None);
        if (!showLibrary || editing) { return; }
        if (result is OperationResult<SavedThemesResult>.Success loaded)
        {
            LibraryStatus.Text = loaded.Value.ReadWarning is { } warning ? ErrorText(warning) : string.Empty;
            var items = loaded.Value.Themes.Select(theme => new GalleryItem(theme.Name, theme.Content, theme.Id,
                theme.Content is ThemeContent.Scene composed
                    ? ThemePresets.All.FirstOrDefault(preset => preset.Content.Backdrop == composed.Backdrop)?.ImagePath : null)).ToList();
            if (viewModel is { EditorContent: var current } && current is not ThemeContent.Scene && current != ThemeContent.Default)
            {
                items.Insert(0, new GalleryItem("当前设备主题", current, null, null));
            }
            UpdateGallery(items, selectedId);
        }
        else
        {
            LibraryStatus.Text = ResultText(result, "主题读取未完成");
        }
    }

    private void UpdateGallery(IReadOnlyList<GalleryItem> items, Guid? selectedId = null)
    {
        galleryItems = items;
        galleryIndex = selectedId is { } id ? Math.Max(0, items.ToList().FindIndex(item => item.SavedId == id)) : 0;
        PresetTab.IsChecked = !showLibrary;
        MineTab.IsChecked = showLibrary;
        Filters.Visibility = VisibleWhen(!showLibrary);
        foreach (var button in Filters.Children.OfType<ToggleButton>()) { button.IsChecked = Equals(button.Tag, filter.ToString()); }
        OpenThemeButton.Visibility = VisibleWhen(items.Count != 0);
        EmptyThemes.Visibility = VisibleWhen(showLibrary && items.Count == 0
            && string.IsNullOrEmpty(LibraryStatus.Text));
        RenderCards();
    }

    private void OnGallerySizeChanged(object sender, SizeChangedEventArgs args)
    {
        int columns = args.NewSize.Width < 620 ? 2 : 4;
        if (columns != columnCount) { RenderCards(); }
        SizeCards();
    }

    private void RenderCards()
    {
        columnCount = GalleryGrid.ActualWidth is > 0 and < 620 ? 2 : 4;
        cards.Clear();
        GalleryGrid.Children.Clear();
        GalleryGrid.ColumnDefinitions.Clear();
        GalleryGrid.RowDefinitions.Clear();
        for (int column = 0; column < columnCount; column++)
        {
            GalleryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        for (int index = 0; index < galleryItems.Count; index++)
        {
            if (index % columnCount == 0) { GalleryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }
            var item = galleryItems[index];
            var image = new Border
            {
                Height = 160,
                Background = WorkspacePalette.GetBrush("WorkspacePreviewBrush"),
                CornerRadius = new CornerRadius(12),
                Child = item.ImagePath is { } path
                    ? new Image { Source = new BitmapImage(new Uri(path)), Stretch = Stretch.UniformToFill }
                    : new FontIcon { Glyph = "\uE7F4", FontSize = 36 }
            };
            var name = new TextBlock { Text = item.Name, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = Microsoft.UI.Text.FontWeights.Medium };
            var panel = new StackPanel { Spacing = 16, Children = { image, name } };
            var button = new Button
            {
                Content = panel, Tag = index,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0, 0, 0, 3), BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            AutomationProperties.SetName(button, item.Name);
            button.Click += OnCardClick;
            button.DoubleTapped += OnCardDoubleTapped;
            Grid.SetColumn(button, index % columnCount);
            Grid.SetRow(button, index / columnCount);
            GalleryGrid.Children.Add(button);
            cards.Add((button, image));
        }
        UpdateCardSelection();
        SizeCards();
    }

    private void SizeCards()
    {
        if (GalleryGrid.ActualWidth <= 0) { return; }
        double width = (GalleryGrid.ActualWidth - (columnCount - 1) * GalleryGrid.ColumnSpacing) / columnCount;
        foreach (var card in cards) { card.Image.Height = Math.Max(64, width * 173 / 266); }
    }

    private void OnCardClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: int index }) { return; }
        galleryIndex = index;
        UpdateCardSelection();
    }

    private async void OnCardDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (sender is Button { Tag: int index }) { await OpenAsync(index); }
    }

    private void UpdateCardSelection()
    {
        for (int index = 0; index < cards.Count; index++)
        {
            bool selected = galleryIndex == index;
            var (button, border) = cards[index];
            border.BorderThickness = new Thickness(selected ? 2 : 1);
            border.BorderBrush = WorkspacePalette.GetBrush(selected ? "WorkspaceActionBrush" : "WorkspaceDividerBrush");
            AutomationProperties.SetItemStatus(button, selected ? "已选中" : "");
        }
    }

    private async void OnOpenThemeClick(object sender, RoutedEventArgs args) => await OpenAsync(galleryIndex);

    private async Task OpenAsync(int index)
    {
        if (opening || editing || index < 0 || index >= galleryItems.Count || viewModel is not { CanEdit: true } model) { return; }
        opening = true;
        try
        {
            var item = galleryItems[index];
            if (openedItem == item && scene is not null)
            {
                SetEditorVisible(true);
                return;
            }
            if (scene is not null && scene != editorBaseline && model.HasUnsavedExitEdits)
            {
                var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark, Title = "当前主题尚未保存",
                    Content = "打开另一款主题会放弃当前编辑。", PrimaryButtonText = "放弃并打开", CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) { return; }
            }
            var next = ThemePresets.ToScene(item.Content);
            PreviewImage.Source = null;
            if (!model.SetEditorContent(next)) { LibraryStatus.Text = model.ErrorText; return; }
            scene = next;
            editorBaseline = next;
            openedItem = item;
            if (item.SavedId.HasValue) { model.MarkLocallySaved(next); }
            undo.Clear();
            EditStatus.Text = "";
            EditorTitle.Text = item.Name;
            SelectElement(next.Elements.IsEmpty ? -1 : 0, rebuild: true);
            SetEditorVisible(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LibraryStatus.Text = Report(exception, "ThemeLibraryOpen", "主题打开未完成");
        }
        finally { opening = false; }
    }

    private void SetEditorVisible(bool visible)
    {
        editing = visible;
        Editor.Visibility = VisibleWhen(visible);
        Gallery.Visibility = VisibleWhen(!visible);
        UpdatePreviewActivity();
        RefreshEditorEnabled();
        if (visible) { PreviewInput.Focus(FocusState.Programmatic); }
    }

    private void OnBackClick(object sender, RoutedEventArgs args)
    {
        if (saving || viewModel is not { CanEdit: true }) { return; }
        SetEditorVisible(false);
        OpenThemeButton.Focus(FocusState.Programmatic);
    }

    private async void OnSaveClick(object sender, RoutedEventArgs args)
    {
        if (saving || scene is not { } content || openedItem is not { } item || library is null
            || viewModel is not { CanEdit: true } model) { return; }
        saving = true;
        model.IsBusy = true;
        try
        {
            var input = new TextBox { Header = "主题名称", MaxLength = FileProfileStore.MaximumThemeNameLength,
                Text = item.SavedId.HasValue ? item.Name : $"我的{item.Name}" };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark, Title = "保存主题",
                Content = new StackPanel { Spacing = 16, Children = {
                    new TextBlock { Text = "将当前布局保存到「我的主题」。", TextWrapping = TextWrapping.Wrap }, input } },
                PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) { return; }
            string name = input.Text.Trim();
            Guid id = item.SavedId ?? Guid.NewGuid();
            var result = await library.SaveThemeAsync(new SavedTheme(id, name, content), CancellationToken.None);
            if (result is not OperationResult<Unit>.Success) { EditStatus.Text = ResultText(result, "主题保存未完成"); return; }

            model.MarkLocallySaved(content);
            openedItem = null;
            editorBaseline = content;
            SetEditorVisible(false);
            await ShowMineAsync(id);
            if (string.IsNullOrEmpty(LibraryStatus.Text)) { LibraryStatus.Text = $"已保存「{name}」"; }
            OpenThemeButton.Focus(FocusState.Programmatic);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            EditStatus.Text = Report(exception, "ThemeLibrarySave", "主题保存未完成");
        }
        finally
        {
            saving = false;
            model.IsBusy = false;
        }
    }

    private static string ResultText<T>(OperationResult<T> result, string fallback) => result switch
    {
        OperationResult<T>.Failure error => ErrorText(error.Error),
        OperationResult<T>.Unknown unknown => ErrorText(unknown.Error),
        _ => fallback
    };

    private static string ErrorText(AppError error) => $"{error.Reason}。{error.Action}（错误编号：{error.ErrorId}）";

    private string Report(Exception exception, string operation, string reason) =>
        $"{reason}。错误编号：{reporter!.Report(exception, operation).ErrorId}";

    private void OnEditorSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool stacked = args.NewSize.Width < 710;
        EditorLayout.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 260);
        EditorLayout.ColumnSpacing = stacked ? 0 : 28;
        EditorLayout.RowSpacing = stacked ? 20 : 0;
        Grid.SetColumn(PropertyColumn, stacked ? 0 : 1);
        Grid.SetRow(PropertyColumn, stacked ? 1 : 0);
        PropertyColumn.BorderThickness = stacked ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        PropertyColumn.Padding = stacked ? new Thickness(0, 12, 0, 0) : new Thickness(20, 0, 0, 0);
    }

    private static Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
