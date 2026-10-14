using System.Windows;
using System.Windows.Threading;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Desktop.ViewModels;
using UsbDisplayHost.Desktop.Services;
using UsbDisplayHost.Desktop.Views;
using UsbDisplayHost.Rendering;
using UsbDisplayHost.Rendering.Images;
using UsbDisplayHost.Windows.Storage;
using UsbDisplayHost.Windows.Telemetry;
using UsbDisplayHost.Core.Telemetry;
using UsbDisplayHost.Windows.Diagnostics;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Windows.Lifecycle;
using UsbDisplayHost.Core.Sessions;

namespace UsbDisplayHost.Desktop;

public sealed class App : Application
{
    private readonly DiagnosticLog errorReporter;
    private bool isReporting;
    private readonly WorkspaceViewModel workspace = new();
    private DesktopThemeService? themeService;
    private ThemeWorkspaceLifetime? themeWorkspace;
    private DeviceSessionLifetime? deviceSessions;
    private Task? shutdownTask;
    private WindowExitService? exitService;
    private TrayService? tray;
    private System.Windows.Controls.TextBlock? startupErrorText;
    private bool activationPending;
    private readonly bool uiOnly;

    public void RequestActivation()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) { return; }
        Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is null) { activationPending = true; return; }
            ShowMainWindow();
        });
    }

    private void ShowMainWindow()
    {
        if (MainWindow is null) { return; }
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) { MainWindow.WindowState = WindowState.Normal; }
        MainWindow.Activate();
    }

    public App(DiagnosticLog errorReporter, bool uiOnly = false)
    {
        this.errorReporter = errorReporter;
        this.uiOnly = uiOnly;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            themeService = new DesktopThemeService(this, errorReporter, message => workspace.ErrorText = message);
            var dataPaths = new ApplicationDataPaths();
            var storageCoordinator = new StorageCoordinator(dataPaths, errorReporter);
            var profiles = new FileProfileStore(dataPaths, storageCoordinator);
            var deviceCoordinator = new DeviceCoordinator(errorReporter, TimeProvider.System);
            var settingsService = new ApplicationSettingsService(profiles, new WindowsStartupRegistration(errorReporter), errorReporter,
                () => deviceCoordinator.Current);
            var settings = new ApplicationSettingsPageViewModel(settingsService, errorReporter);
            var assets = new FileAssetStore(dataPaths, new WpfImageValidator(errorReporter), storageCoordinator);
            var renderer = new WpfFrameRenderer(assets, TimeProvider.System, errorReporter);
            var metrics = new MetricSamplingService(new WindowsHostMetricProvider(TimeProvider.System, errorReporter),
                TimeProvider.System, errorReporter);
            var preview = new DraftPreviewController(renderer, Dispatcher, TimeProvider.System, errorReporter, metrics, assets);
            deviceSessions = new DeviceSessionLifetime(deviceCoordinator, errorReporter);
            var devices = new DevicesPageViewModel(deviceCoordinator, deviceSessions, errorReporter);
            var deviceSettings = new DeviceSettingsPageViewModel(errorReporter, deviceCoordinator, deviceSessions, metrics, devices);
            var themes = new ThemePageViewModel(assets, new ImagePicker(() => MainWindow), preview, errorReporter,
                deviceCoordinator, deviceSessions, metrics);
            var diagnosticsPage = new DiagnosticsPageViewModel(errorReporter,
                new DiagnosticExportService(dataPaths, errorReporter, TimeProvider.System),
                new DiagnosticFolderOpener(dataPaths, errorReporter), new ExportDestinationPicker(() => MainWindow),
                themes.CaptureDiagnosticSummary, deviceSessions);
            var deviceSnapshots = new DeviceSnapshotPresenter(deviceCoordinator, deviceSessions, Dispatcher, devices, workspace, errorReporter, themes, deviceSettings, settings);
            var deviceDiscovery = new DeviceDiscoveryLifetime(Dispatcher, devices, deviceCoordinator, deviceSessions, errorReporter);
            var protocol = new DeviceProtocolService(deviceCoordinator, deviceSessions, deviceDiscovery,
                profiles, assets, renderer, metrics, errorReporter);
            if (!uiOnly) { devices.AttachProtocol(protocol, deviceDiscovery.StartAsync); }
            var modeNotice = new ModeSwitchNoticeService(settingsService, () => MainWindow as MainWindow);
            devices.AttachModeSwitchConfirmation(modeNotice.ConfirmAsync);
            themeWorkspace = new ThemeWorkspaceLifetime(themes, settings, deviceSettings, diagnosticsPage, preview, renderer, metrics,
                deviceSessions, deviceCoordinator, deviceSnapshots, deviceDiscovery, errorReporter, protocol, devices);
            var pageProvider = new WorkspacePageProvider([
                new DevicesPage(devices), new ThemesPage(themes, errorReporter), new DeviceSettingsPage(deviceSettings, errorReporter),
                new ApplicationSettingsPage(settings, errorReporter), new DiagnosticsPage(diagnosticsPage, errorReporter)]);
            var navigationService = new Wpf.Ui.NavigationService(pageProvider);
            MainWindow = new MainWindow(workspace, navigationService, pageProvider, settings, devices);
            _ = new WindowWorkAreaLifetime(MainWindow, errorReporter, message => workspace.ErrorText = message);
            themeWorkspace.AttachNotices(new WorkspaceNoticeLifetime((MainWindow)MainWindow, devices, themes, deviceSettings, errorReporter));
            if (!uiOnly)
            {
                themeWorkspace.AttachPowerLifetime(new WindowPowerLifetime(MainWindow, deviceCoordinator,
                    deviceSessions, deviceDiscovery, errorReporter));
            }
            exitService = new WindowExitService(this, themeWorkspace, errorReporter,
                TimeSpan.FromSeconds(2), TimeProvider.System);
            exitService.Attach(MainWindow);
            try
            {
                tray = new TrayService(ShowMainWindow, () => _ = exitService.RequestExitAsync(MainWindow),
                    message => workspace.ErrorText = message, settingsService, errorReporter, devices);
                exitService.SetTray(tray);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = errorReporter.Report(exception, "TrayInitialization");
                workspace.ErrorText = $"托盘不可用，关闭窗口将执行退出。错误编号：{report.ErrorId}";
            }
            MainWindow.IsVisibleChanged += OnWindowVisibilityChanged;
            MainWindow.StateChanged += OnWindowStateChanged;
            MainWindow.Show();
            if (uiOnly) { _ = settings.InitializeAsync(); }
            else { _ = devices.InitializeProtocolAsync(settings); }
            if (activationPending) { activationPending = false; ShowMainWindow(); }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowFailure(exception, "Startup");
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (!e.Cancel) { exitService?.NotifySystemExit(); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        shutdownTask ??= themeWorkspace?.StopAsync();
        DisposeDesktopResource(() => tray?.Dispose(), "TrayDispose");
        DisposeDesktopResource(() => themeService?.Dispose(), "ThemeServiceDispose");
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        base.OnExit(e);
    }

    private void DisposeDesktopResource(Action dispose, string stage)
    {
        try { dispose(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { errorReporter.Report(exception, stage); }
    }

    private void OnWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdatePreviewVisibility();
    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdatePreviewVisibility();
    private void UpdatePreviewVisibility()
    {
        bool visible = MainWindow is { IsVisible: true } && MainWindow.WindowState != WindowState.Minimized;
        themeWorkspace?.Themes.SetWindowVisible(visible);
        themeWorkspace?.SetWindowVisible(visible);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (isReporting || e.Exception is OutOfMemoryException)
        {
            return;
        }

        // 当前骨架没有设备操作；失败操作不重试，不将全局捕获视为业务恢复成功。
        ShowFailure(e.Exception, "Dispatcher");
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ErrorReport report = errorReporter.Report(e.Exception, "UnobservedTask");
        e.SetObserved();

        // 不持有后台线程等待 UI，也不在应用退出后投递通知。
        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            try
            {
                Dispatcher.BeginInvoke(() => PresentFailure(report));
            }
            catch (InvalidOperationException exception)
            {
                // 退出可能发生在状态检查之后；保留记录，不在终结器线程继续抛出。
                errorReporter.Report(exception, "NotificationDuringShutdown");
            }
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // CLR 已决定终止的后台线程故障只能尽力记录，不能宣称可取消进程终止。
        if (e.ExceptionObject is Exception exception && exception is not OutOfMemoryException)
        {
            errorReporter.Report(exception, "ProcessTermination");
        }
    }

    private void ShowFailure(Exception exception, string stage)
    {
        isReporting = true;
        try
        {
            PresentFailure(errorReporter.Report(exception, stage));
        }
        finally
        {
            isReporting = false;
        }
    }

    private void PresentFailure(ErrorReport report)
    {
        string message = $"应用内部处理失败，当前操作未完成。请重启应用后重试；若仍失败，请提供错误编号：{report.ErrorId}";
        if (!report.IsRecorded && !report.IsPending)
        {
            message += "。错误记录未保存，请检查本地目录权限和磁盘空间";
        }
        else if (report.IsPending) { message += "。诊断记录已提交后台写入，可在诊断页查看存储状态"; }

        if (MainWindow is MainWindow)
        {
            workspace.ErrorText = message;
        }
        else if (startupErrorText is not null)
        {
            startupErrorText.Text = message;
        }
        else
        {
            // XAML 初始化失败时使用无资源依赖的窗口，仍让用户看见失败并自主关闭。
            startupErrorText = new System.Windows.Controls.TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24)
            };
            MainWindow = new Window
            {
                Title = "USB Display — 启动失败",
                Width = 640,
                Height = 400,
                Content = startupErrorText
            };
            if (themeWorkspace is not null)
            {
                    exitService = new WindowExitService(this, themeWorkspace, errorReporter,
                        TimeSpan.FromSeconds(2), TimeProvider.System);
                exitService.Attach(MainWindow);
            }
            else { MainWindow.Closed += (_, _) => Shutdown(); }
            MainWindow.Show();
        }
    }
}
