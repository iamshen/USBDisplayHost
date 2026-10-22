using System.Windows.Controls;
using UsbDisplayHost.Desktop.ViewModels;
using System.Windows.Threading;

namespace UsbDisplayHost.Desktop.Views;

public partial class DevicesPage : Page
{
    public DevicesPage(DevicesPageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        void RefreshVisibility()
        {
            bool visible = IsLoaded && IsVisible;
            viewModel.SetPreviewPageVisible(visible);
            if (visible) { _ = viewModel.RefreshHostPreviewAsync(); timer.Start(); }
            else { timer.Stop(); }
        }
        timer.Tick += (_, _) => _ = viewModel.RefreshHostPreviewAsync();
        Loaded += (_, _) => RefreshVisibility();
        Unloaded += (_, _) => { timer.Stop(); viewModel.SetPreviewPageVisible(false); };
        IsVisibleChanged += (_, _) => RefreshVisibility();
    }
    private void OnDisplaySizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        bool stacked = e.NewSize.Width < 850;
        DisplayGrid.ColumnDefinitions[1].Width = stacked ? new System.Windows.GridLength(0) : new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        Grid.SetColumn(CurrentDisplay, stacked ? 0 : 1);
        Grid.SetRow(CurrentDisplay, stacked ? 1 : 0);
        DeviceCard.Margin = stacked ? new System.Windows.Thickness(0, 0, 0, 16) : new System.Windows.Thickness(0, 0, 20, 0);
    }

    private void OnEditThemeClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow window) { window.NavigateToThemes(); }
    }
}
