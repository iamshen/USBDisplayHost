using System.Windows.Controls;
using System.Windows;
using System.Windows.Threading;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Desktop.ViewModels;

namespace UsbDisplayHost.Desktop.Views;

public partial class DiagnosticsPage : Page
{
    private readonly DiagnosticsPageViewModel viewModel;
    private readonly IErrorReporter errorReporter;
    private readonly DispatcherTimer timer;

    public DiagnosticsPage(DiagnosticsPageViewModel viewModel, IErrorReporter errorReporter)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.errorReporter = errorReporter;
        DataContext = viewModel;
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += OnTick;
        Loaded += (_, _) => UpdateVisibility();
        Unloaded += (_, _) => timer.Stop();
        IsVisibleChanged += (_, _) => UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        if (IsLoaded && IsVisible) { Refresh(); timer.Start(); }
        else { timer.Stop(); }
    }

    private void OnTick(object? sender, EventArgs e) => Refresh();
    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 紧凑窗口可滚动到全部操作，事件列表仍由有限高度的 Grid 承载。
        DiagnosticsWorkspace.Height = Math.Max(480, e.NewSize.Height);
    }

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 摘要保持有限高度，让事件列表拥有独立的虚拟化视口。
        bool stacked = e.NewSize.Width < 460;
        SummaryPanels.Columns = stacked ? 1 : 3;
        HostSummaryCard.Margin = stacked ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 12, 0);
        FramesSummaryCard.Margin = HostSummaryCard.Margin;
        SummaryScroll.MaxHeight = Math.Max(0, e.NewSize.Height * 0.42);
    }
    private void Refresh()
    {
        try { viewModel.Refresh(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            timer.Stop();
            var report = errorReporter.Report(exception, "DiagnosticsRefresh");
            viewModel.StorageText = $"诊断状态刷新未完成。错误编号：{report.ErrorId}";
        }
    }
}
