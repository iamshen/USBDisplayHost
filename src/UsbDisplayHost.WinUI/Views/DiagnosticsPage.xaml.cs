using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
}
