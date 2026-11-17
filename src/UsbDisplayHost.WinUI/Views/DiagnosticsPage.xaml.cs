using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Desktop.ViewModels;

namespace UsbDisplayHost.WinUI.Views;

public sealed partial class DiagnosticsPage : Page
{
    private readonly DispatcherQueueTimer refreshTimer;
    private DiagnosticsPageViewModel? viewModel;
    private IErrorReporter? errorReporter;
    private bool loaded;
    private bool pageVisible;

    public DiagnosticsPage()
    {
        InitializeComponent();
        refreshTimer = DispatcherQueue.CreateTimer();
        refreshTimer.Interval = TimeSpan.FromSeconds(1);
        refreshTimer.Tick += OnRefreshTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    internal void InitializeView(DiagnosticsPageViewModel viewModel, IErrorReporter reporter)
    {
        this.viewModel = viewModel;
        errorReporter = reporter;
        DataContext = viewModel;
        UpdateRefresh();
    }

    internal void SetPageVisible(bool visible)
    {
        if (visible != pageVisible)
        {
            pageVisible = visible;
            UpdateRefresh();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        loaded = true;
        UpdateRefresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        loaded = false;
        UpdateRefresh();
    }

    private void UpdateRefresh()
    {
        if (!loaded || !pageVisible)
        {
            refreshTimer.Stop();
        }
        else if (!refreshTimer.IsRunning && viewModel is { } model && Refresh(model))
        {
            refreshTimer.Start();
        }
    }

    private void OnRefreshTick(DispatcherQueueTimer sender, object args)
    {
        if (loaded && pageVisible && viewModel is { } model && !Refresh(model))
        {
            sender.Stop();
        }
    }

    private bool Refresh(DiagnosticsPageViewModel model)
    {
        try
        {
            model.Refresh();
            TransferSummary.Foreground = WorkspacePalette.GetBrush(model.ConnectionSummary switch
            {
                "就绪" => "WorkspaceSuccessBrush",
                "需要恢复" or "I/O 未确认" or "关闭未确认" => "WorkspaceWarningBrush",
                _ => "WorkspaceTextSecondaryBrush"
            });
            LogState.Foreground = WorkspacePalette.GetBrush(model.LogStateText == "正常"
                ? "WorkspaceSuccessBrush" : "WorkspaceWarningBrush");
            bool hasEvents = model.Events.Count != 0;
            EventHeader.Visibility = hasEvents ? Visibility.Visible : Visibility.Collapsed;
            EventList.Visibility = EventHeader.Visibility;
            EmptyEvents.Visibility = hasEvents ? Visibility.Collapsed : Visibility.Visible;
            RefreshErrorText.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter!.Report(exception, "DiagnosticsRefresh");
            RefreshErrorText.Text = $"诊断状态刷新未完成。错误编号：{report.ErrorId}";
            RefreshErrorText.Visibility = Visibility.Visible;
            return false;
        }
    }

    private static string DisclosureGlyph(bool? isExpanded) => isExpanded == true ? "\uE70D" : "\uE76C";

    public static Brush SeverityBrush(string severity) => WorkspacePalette.GetBrush(severity switch
    {
        "错误" => "WorkspaceErrorBrush",
        "警告" => "WorkspaceWarningBrush",
        _ => "WorkspaceTextSecondaryBrush"
    });

    public static string EventText(string stage, string code) => (stage, code) switch
    {
        ("Application", "StartingUiOnly") => "应用 · 无设备模式已启动",
        ("Application", "Starting") => "应用 · 会话已启动",
        ("DeviceProtocol", "UsbInterfaceInUse") => "连接 · USB 接口被占用",
        ("FrameTransfer", "UsbFrameCompleted") => "发送 · 完成一帧",
        ("DeviceOperation", "ApplyContentStarted") => "主题 · 开始应用",
        ("DeviceOperation", "ApplyContentCompleted") => "主题 · 应用完成",
        _ => $"{stage} · {code}"
    };
}
