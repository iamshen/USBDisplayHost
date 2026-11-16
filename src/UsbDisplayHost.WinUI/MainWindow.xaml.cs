using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace UsbDisplayHost.WinUI;

public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 1180;
    private const int DefaultHeight = 820;
    private const int DefaultDpi = 96;
    private readonly StartupLog log;

    internal MainWindow(StartupLog log)
    {
        this.log = log;
        InitializeComponent();
        ApplyInitialSize();

        WorkspaceNavigation.SelectedItem = DevicesItem;
        WorkspaceNavigation.SelectionChanged += OnSelectionChanged;
        Closed += OnClosed;
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

    private void OnOpenDiagnostics(object sender, RoutedEventArgs args)
    {
        WorkspaceNavigation.SelectedItem = DiagnosticsItem;
        DiagnosticsItem.Focus(FocusState.Programmatic);
    }

    private void OnSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        bool showDiagnostics = sender.SelectedItem == DiagnosticsItem;
        if (showDiagnostics)
        {
            StartupEntries.Text = log.Entries;
            StartupLogLocation.Text = log.StorageStatus;
        }

        DevicesView.Visibility = showDiagnostics ? Visibility.Collapsed : Visibility.Visible;
        DiagnosticsView.Visibility = showDiagnostics ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        WorkspaceNavigation.SelectionChanged -= OnSelectionChanged;
        log.Write("主窗口已关闭");
        log.Dispose();
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);
}
