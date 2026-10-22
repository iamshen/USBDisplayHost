using UsbDisplayHost.Desktop.ViewModels;
using UsbDisplayHost.Desktop.Views;
using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MessageBoxButton = System.Windows.MessageBoxButton;
using System.ComponentModel;
using System.Windows.Controls;
using UsbDisplayHost.Core.Configuration;

namespace UsbDisplayHost.Desktop;

public partial class MainWindow : FluentWindow
{
    private sealed record PageLink(string Label, SymbolRegular Icon, Type PageType, string Description);
    private readonly ApplicationSettingsPageViewModel settings;
    public DevicesPageViewModel DeviceSelector { get; }
    private readonly PageLink[] pages =
    [
        new("设备", SymbolRegular.Desktop24, typeof(DevicesPage), "连接设备，查看画面与运行状态"),
        new("主题", SymbolRegular.Image24, typeof(ThemesPage), "编辑、预览并应用屏幕内容"),
        new("设备设置", SymbolRegular.Settings24, typeof(DeviceSettingsPage), "调整屏幕方向、画质与亮度"),
        new("诊断", SymbolRegular.Pulse24, typeof(DiagnosticsPage), "查看运行信息与事件日志"),
        new("应用设置", SymbolRegular.AppGeneric24, typeof(ApplicationSettingsPage), "管理窗口、启动与设备模式偏好")
    ];
    private bool compactMenuOpen;
    private bool synchronizingNavigation;
    private bool showingPartialNavigation;
    private readonly PageLink[] primaryPages;
    private readonly SnackbarService notifications = new();
    private readonly ContentDialogService dialogs = new();
    private bool isDialogOpen;
    public MainWindow(WorkspaceViewModel viewModel, INavigationService navigationService,
        INavigationViewPageProvider pageProvider, ApplicationSettingsPageViewModel settings, DevicesPageViewModel devices)
    {
        this.settings = settings;
        DeviceSelector = devices;
        primaryPages = pages.Take(3).ToArray();
        InitializeComponent();
        notifications.SetSnackbarPresenter(RootSnackbarPresenter);
        dialogs.SetDialogHost(RootDialogHost);
        DataContext = viewModel;
        RootNavigationView.SetPageProviderService(pageProvider);
        navigationService.SetNavigationControl(RootNavigationView);
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        RootNavigationView.SelectionChanged += OnNavigationSelectionChanged;
        PageNavigation.ItemsSource = pages;
        settings.PropertyChanged += OnSettingsChanged;
        Closed += (_, _) => settings.PropertyChanged -= OnSettingsChanged;
    }

    public void ShowNotice(string text, bool failed)
    {
        if (!IsVisible || WindowState == WindowState.Minimized) { return; }
        string message = text.Length > 200 ? text[..200] + "…" : text;
        notifications.Show("USB Display", message, failed ? ControlAppearance.Caution : ControlAppearance.Secondary,
            new SymbolIcon { Symbol = SymbolRegular.Info24 }, TimeSpan.FromSeconds(3));
    }

    public void NavigateToThemes() => RootNavigationView.Navigate(typeof(ThemesPage));

    public async Task<MessageBoxResult> ShowDecisionAsync(string message, MessageBoxButton buttons,
        CancellationToken cancellationToken)
    {
        Dispatcher.VerifyAccess();
        if (isDialogOpen || cancellationToken.IsCancellationRequested) { return MessageBoxResult.Cancel; }
        isDialogOpen = true;
        try
        {
            var result = await dialogs.ShowAsync(new ContentDialog
            {
                Title = "退出 USB Display",
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 440
                },
                PrimaryButtonText = buttons == MessageBoxButton.OK ? "" : buttons == MessageBoxButton.YesNo ? "退出" : "保存后退出",
                SecondaryButtonText = buttons == MessageBoxButton.YesNoCancel ? "放弃并退出" : "",
                CloseButtonText = buttons == MessageBoxButton.OK ? "知道了" : buttons == MessageBoxButton.YesNo ? "保持窗口" : "取消",
                DefaultButton = ContentDialogButton.Close
            }, cancellationToken);
            if (cancellationToken.IsCancellationRequested) { return MessageBoxResult.Cancel; }
            return result switch
            {
                ContentDialogResult.Primary => MessageBoxResult.Yes,
                ContentDialogResult.Secondary => MessageBoxResult.No,
                _ => MessageBoxResult.Cancel
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return MessageBoxResult.Cancel; }
        finally { isDialogOpen = false; }
    }

    public async Task<(bool Confirmed, bool Suppress)> ConfirmModeSwitchAsync(CancellationToken token)
    {
        Dispatcher.VerifyAccess();
        if (isDialogOpen || token.IsCancellationRequested) { return (false, false); }
        isDialogOpen = true;
        try
        {
            Show();
            if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; }
            Activate();
            var suppress = new System.Windows.Controls.CheckBox { Content = "不再提示", Margin = new Thickness(0, 16, 0, 0) };
            var content = new System.Windows.Controls.StackPanel { MaxWidth = 440 };
            content.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "切换到主题模式后，此设备将退出 Windows 扩展屏，Windows 可能重新排列扩展屏上的窗口。是否继续？",
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(suppress);
            var result = await dialogs.ShowAsync(new ContentDialog
            {
                Title = "切换到主题模式", Content = content,
                PrimaryButtonText = "继续切换", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            }, token);
            return (!token.IsCancellationRequested && result == ContentDialogResult.Primary, suppress.IsChecked == true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return (false, false); }
        finally { isDialogOpen = false; }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        RootNavigationView.Navigate(typeof(DevicesPage));
        UpdateNavigationLayout();
    }

    private void OnNavigationSelectionChanged(object sender, RoutedEventArgs e)
    {
        bool returnMenuFocus = compactMenuOpen;
        // 五页平级导航不保留回退堆栈；页面实例由组合根独立缓存。
        RootNavigationView.ClearJournal();
        if (RootNavigationView.SelectedItem is NavigationViewItem selected)
        {
            synchronizingNavigation = true;
            var page = pages.FirstOrDefault(candidate => candidate.PageType == selected.TargetPageType);
            PageNavigation.SelectedItem = page is not null && PageNavigation.Items.Contains(page) ? page : null;
            synchronizingNavigation = false;
        }
        compactMenuOpen = false;
        UpdateNavigationLayout();
        if (returnMenuFocus) { CompactMenuButton.Focus(); }
    }

    private void OnPageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!synchronizingNavigation && PageNavigation.SelectedItem is PageLink page)
        { RootNavigationView.Navigate(page.PageType); }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ApplicationSettingsPageViewModel.NavigationLayout)) { UpdateNavigationLayout(); }
    }

    private void OnCompactMenuClick(object sender, RoutedEventArgs e)
    {
        compactMenuOpen = !compactMenuOpen;
        UpdateNavigationLayout();
        if (compactMenuOpen) { PageNavigation.Focus(); }
    }

    private void OnNavigationKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && compactMenuOpen)
        { compactMenuOpen = false; UpdateNavigationLayout(); CompactMenuButton.Focus(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Enter && compactMenuOpen)
        { compactMenuOpen = false; UpdateNavigationLayout(); CompactMenuButton.Focus(); e.Handled = true; }
    }

    private void OnMoreNavigationClick(object sender, RoutedEventArgs e)
    {
        MoreNavigationButton.ContextMenu.PlacementTarget = MoreNavigationButton;
        MoreNavigationButton.ContextMenu.IsOpen = true;
    }
    private void OnDiagnosticsMenuClick(object sender, RoutedEventArgs e)
    {
        RootNavigationView.Navigate(typeof(DiagnosticsPage));
        UpdateNavigationLayout();
    }
    private void OnSettingsMenuClick(object sender, RoutedEventArgs e)
    {
        RootNavigationView.Navigate(typeof(ApplicationSettingsPage));
        UpdateNavigationLayout();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateNavigationLayout();
    }

    private void UpdateNavigationLayout()
    {
        if (PageNavigation is null) { return; }
        bool compact = ActualWidth < 800;
        bool side = !compact && settings.NavigationLayout == NavigationLayout.Side;
        bool partial = !compact && !side && ActualWidth < 1000;
        if (partial != showingPartialNavigation)
        {
            showingPartialNavigation = partial;
            synchronizingNavigation = true;
            PageNavigation.ItemsSource = partial ? primaryPages : pages;
            if (RootNavigationView.SelectedItem is NavigationViewItem item)
            {
                var page = pages.FirstOrDefault(candidate => candidate.PageType == item.TargetPageType);
                PageNavigation.SelectedItem = page is not null && PageNavigation.Items.Contains(page) ? page : null;
            }
            synchronizingNavigation = false;
        }
        double pageMargin = compact ? 16 : 32;
        System.Windows.Application.Current.Resources["PageContentMargin"] = new Thickness(pageMargin, compact ? 16 : 24, pageMargin, compact ? 16 : 24);
        Header.Margin = new Thickness(0, 0, pageMargin, 0);
        WorkspaceError.Margin = new Thickness(pageMargin, 8, pageMargin, 0);
        NavigationColumn.Width = new GridLength(side ? 184 : 0);
        Grid.SetColumn(PageNavigation, side ? 0 : 1);
        Grid.SetRowSpan(PageNavigation, side ? 2 : 1);
        Grid.SetRow(PageNavigation, compact ? 1 : 0);
        Panel.SetZIndex(PageNavigation, compact ? 30 : 0);
        PageNavigation.Tag = compact ? "Compact" : side ? "Side" : "Top";
        PageNavigation.ItemsPanel = (ItemsPanelTemplate)Resources[compact ? "CompactNavigationPanel" : side ? "SideNavigationPanel" : "TopNavigationPanel"];
        Grid.SetColumnSpan(PageNavigation, compact && compactMenuOpen ? 2 : 1);
        PageNavigation.VerticalAlignment = compact ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        PageNavigation.Width = compact ? 220 : double.NaN;
        PageNavigation.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        PageNavigation.MaxHeight = compact ? Math.Max(48, ActualHeight - 110) : double.PositiveInfinity;
        ScrollViewer.SetVerticalScrollBarVisibility(PageNavigation, compact ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        PageNavigation.Margin = side ? new Thickness(0, 12, 0, 12) : compact ? new Thickness(16, 0, 0, 0) : new Thickness(pageMargin - 20, 0, partial ? 140 : 0, 0);
        PageNavigation.Visibility = compact && !compactMenuOpen ? Visibility.Collapsed : Visibility.Visible;
        CompactMenuButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        MoreNavigationButton.Visibility = partial ? Visibility.Visible : Visibility.Collapsed;
        var currentPage = (RootNavigationView.SelectedItem as NavigationViewItem)?.TargetPageType;
        string currentLabel = pages.FirstOrDefault(page => page.PageType == currentPage)?.Label ?? "设备";
        CompactPageLabel.Text = currentLabel;
        CompactChevron.Symbol = compactMenuOpen ? SymbolRegular.ChevronUp16 : SymbolRegular.ChevronDown16;
        System.Windows.Automation.AutomationProperties.SetName(CompactMenuButton,
            $"{(compactMenuOpen ? "收起" : "打开")}页面导航，当前：{currentLabel}");
        MoreNavigationButton.Content = currentPage == typeof(DiagnosticsPage) ? "诊断 ▾"
            : currentPage == typeof(ApplicationSettingsPage) ? "应用设置 ▾" : "更多 ▾";
        System.Windows.Automation.AutomationProperties.SetName(MoreNavigationButton, $"更多页面，当前：{currentLabel}");
        DiagnosticsMenuItem.IsChecked = currentPage == typeof(DiagnosticsPage);
        SettingsMenuItem.IsChecked = currentPage == typeof(ApplicationSettingsPage);
        Header.MinHeight = 56;
    }
}
