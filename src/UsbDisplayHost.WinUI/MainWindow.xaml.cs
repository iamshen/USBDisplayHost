using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Desktop.Services;
using UsbDisplayHost.Desktop.ViewModels;
using Windows.Graphics;
using WinRT.Interop;

namespace UsbDisplayHost.WinUI;

public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 1180;
    private const int DefaultHeight = 820;
    private const int DefaultDpi = 96;
    private readonly StartupLog log;
    private readonly WorkspaceViewModel workspace;
    private readonly ApplicationSettingsPageViewModel settings;
    private string? selectedPage;
    private bool dialogOpen;
    private bool workspaceVisible;

    internal MainWindow(StartupLog log, WorkspaceViewModel workspace, DevicesPageViewModel devices,
        ThemePageViewModel themes, DeviceSettingsPageViewModel deviceSettings, DiagnosticsPageViewModel diagnostics,
        ApplicationSettingsPageViewModel settings, IErrorReporter reporter, bool uiOnly)
    {
        this.log = log;
        this.workspace = workspace;
        this.settings = settings;
        InitializeComponent();
        Title = Program.ProductName;
        ApplyInitialSize();

        DevicesView.InitializeView(devices, reporter, uiOnly);
        ThemesView.InitializeView(themes, reporter);
        DeviceSettingsView.InitializeView(deviceSettings);
        DiagnosticsView.InitializeView(diagnostics, reporter);
        AppSettingsView.InitializeView(settings, uiOnly);
        DevicesView.DiagnosticsRequested += OnDiagnosticsRequested;
        DevicesView.ThemeRequested += OnThemeRequested;
        SelectPage("Devices");
        UpdateNavigationLayout();
        WorkspaceNavigation.SelectionChanged += OnTopSelectionChanged;
        SideNavigation.SelectionChanged += OnSideSelectionChanged;
        settings.PropertyChanged += OnSettingsChanged;
        workspace.PropertyChanged += OnWorkspaceChanged;
        Closed += OnClosed;
    }

    internal void RestoreAndActivate()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        Activate();
    }

    internal void ShowNotice(string message, bool failed)
    {
        if (string.IsNullOrWhiteSpace(message)) { return; }
        WorkspaceNotice.Message = message;
        WorkspaceNotice.Severity = failed ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        WorkspaceNotice.IsOpen = true;
    }

    internal async Task<WindowExitChoice> ShowExitDecisionAsync(string message, WindowExitPrompt prompt, CancellationToken token)
    {
        var dialog = new ContentDialog
        {
            Title = prompt == WindowExitPrompt.Install ? "安装软件更新" : $"退出 {Program.ProductName}",
            Content = new TextBlock
            {
                Text = message.Replace("选择“否”", "选择“放弃并退出”", StringComparison.Ordinal),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440
            },
            PrimaryButtonText = prompt switch
            {
                WindowExitPrompt.Save => "保存后退出",
                WindowExitPrompt.Continue => "退出",
                WindowExitPrompt.Install => "重启并安装",
                _ => string.Empty
            },
            SecondaryButtonText = prompt == WindowExitPrompt.Save ? "放弃并退出" : string.Empty,
            CloseButtonText = prompt switch
            {
                WindowExitPrompt.Information => "知道了",
                WindowExitPrompt.Continue => "保持窗口",
                WindowExitPrompt.Install => "稍后",
                _ => "取消"
            },
            DefaultButton = ContentDialogButton.Close
        };
        return await ShowDialogAsync(dialog, token) switch
        {
            ContentDialogResult.Primary => WindowExitChoice.Confirm,
            ContentDialogResult.Secondary => WindowExitChoice.Discard,
            _ => WindowExitChoice.Cancel
        };
    }

    internal async Task<(bool Confirmed, bool Suppress)> ConfirmModeSwitchAsync(CancellationToken token)
    {
        if (dialogOpen || token.IsCancellationRequested) { return (false, false); }
        RestoreAndActivate();
        var suppress = new CheckBox { Content = "不再提示" };
        var panel = new StackPanel { Spacing = 16, MaxWidth = 440 };
        panel.Children.Add(new TextBlock
        {
            Text = "切换到主题模式后，此设备将退出 Windows 扩展屏，Windows 可能重新排列扩展屏上的窗口。是否继续？",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(suppress);
        var dialog = new ContentDialog
        {
            Title = "切换到主题模式",
            Content = panel,
            PrimaryButtonText = "继续切换",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        var result = await ShowDialogAsync(dialog, token);
        return (result == ContentDialogResult.Primary, suppress.IsChecked == true);
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, CancellationToken token)
    {
        if (dialogOpen || token.IsCancellationRequested || WorkspaceRoot.XamlRoot is not { } root)
        {
            return ContentDialogResult.None;
        }
        dialog.XamlRoot = root;
        dialogOpen = true;
        try
        {
            using var cancellation = token.Register(() => DispatcherQueue.TryEnqueue(() =>
            {
                if (dialogOpen) { dialog.Hide(); }
            }));
            var result = await dialog.ShowAsync();
            return token.IsCancellationRequested ? ContentDialogResult.None : result;
        }
        finally { dialogOpen = false; }
    }

    private void ApplyInitialSize()
    {
        uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
        double scale = dpi == 0 ? 1.0 : (double)dpi / DefaultDpi;
        DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        RectInt32 workArea = display.WorkArea;
        RectInt32 displayBounds = display.OuterBounds;
        PointInt32 position = AppWindow.Position;
        int width = Math.Min((int)Math.Round(DefaultWidth * scale), workArea.Width);
        int height = Math.Min((int)Math.Round(DefaultHeight * scale), workArea.Height);
        int x = Math.Clamp(position.X - displayBounds.X, workArea.X, workArea.X + workArea.Width - width);
        int y = Math.Clamp(position.Y - displayBounds.Y, workArea.Y, workArea.Y + workArea.Height - height);

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height), display);
    }

    private void OnDiagnosticsRequested(object? sender, EventArgs args) => OpenPage("Diagnostics");

    private void OnThemeRequested(object? sender, EventArgs args) => OpenPage("Themes");

    private void OpenPage(string page)
    {
        SelectPage(page);
        if (settings.NavigationLayout == NavigationLayout.Side)
        {
            (SideNavigation.SelectedItem as Control)?.Focus(FocusState.Programmatic);
        }
        else
        {
            WorkspaceNavigation.SelectedItem?.Focus(FocusState.Programmatic);
        }
    }

    private void OnTopSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        SelectPage(sender.SelectedItem?.Tag as string);

    private void OnSideSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args) =>
        SelectPage((args.SelectedItem as NavigationViewItem)?.Tag as string);

    private void SelectPage(string? page)
    {
        if (page is null || page == selectedPage) { return; }
        selectedPage = page;
        WorkspaceNavigation.SelectedItem = WorkspaceNavigation.Items.First(item => Equals(item.Tag, page));
        SideNavigation.SelectedItem = SideNavigation.MenuItems.OfType<NavigationViewItem>().First(item => Equals(item.Tag, page));

        DevicesView.Visibility = VisibleWhen(page == "Devices");
        ThemesView.Visibility = VisibleWhen(page == "Themes");
        DeviceSettingsView.Visibility = VisibleWhen(page == "DeviceSettings");
        DiagnosticsView.Visibility = VisibleWhen(page == "Diagnostics");
        AppSettingsView.Visibility = VisibleWhen(page == "AppSettings");
        SetWorkspaceVisible(workspaceVisible);
    }

    internal void SetWorkspaceVisible(bool visible)
    {
        workspaceVisible = visible;
        DevicesView.SetPageVisible(visible && selectedPage == "Devices");
        ThemesView.SetPageVisible(visible && selectedPage == "Themes");
        DiagnosticsView.SetPageVisible(visible && selectedPage == "Diagnostics");
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ApplicationSettingsPageViewModel.NavigationLayout)) { UpdateNavigationLayout(); }
    }

    private void UpdateNavigationLayout()
    {
        bool side = settings.NavigationLayout == NavigationLayout.Side;
        TopNavigation.Visibility = VisibleWhen(!side);
        SideNavigation.Visibility = VisibleWhen(side);
    }

    private static Visibility VisibleWhen(bool condition) => condition ? Visibility.Visible : Visibility.Collapsed;

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(WorkspaceViewModel.ErrorText)) { ShowNotice(workspace.ErrorText, failed: true); }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        WorkspaceNavigation.SelectionChanged -= OnTopSelectionChanged;
        SideNavigation.SelectionChanged -= OnSideSelectionChanged;
        settings.PropertyChanged -= OnSettingsChanged;
        DevicesView.DiagnosticsRequested -= OnDiagnosticsRequested;
        DevicesView.ThemeRequested -= OnThemeRequested;
        SetWorkspaceVisible(false);
        workspace.PropertyChanged -= OnWorkspaceChanged;
        log.Write("主窗口已关闭");
        log.Dispose();
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetDpiForWindow(nint windowHandle);
}
