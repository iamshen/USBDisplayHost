using System.Reflection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Core.Telemetry;
using UsbDisplayHost.Desktop.Services;
using UsbDisplayHost.Desktop.ViewModels;
using UsbDisplayHost.Rendering;
using UsbDisplayHost.Rendering.Images;
using UsbDisplayHost.Windows.Diagnostics;
using UsbDisplayHost.Windows.Lifecycle;
using UsbDisplayHost.Windows.Storage;
using UsbDisplayHost.Windows.Telemetry;
using UsbDisplayHost.Windows.Updates;
using UsbDisplayHost.WinUI.Services;

namespace UsbDisplayHost.WinUI;

public partial class App : Application
{
    private readonly DiagnosticLog diagnostics;
    private readonly ApplicationDataPaths paths;
    private readonly WinUiDispatcher dispatcher;
    private readonly bool uiOnly;
    private readonly string? updatedTo;
    private readonly StartupLog log = new();
    private MainWindow? window;
    private bool activationPending;

    internal App(DiagnosticLog diagnostics, ApplicationDataPaths paths, DispatcherQueue queue, bool uiOnly, string? updatedTo)
    {
        this.diagnostics = diagnostics;
        this.paths = paths;
        this.uiOnly = uiOnly;
        this.updatedTo = updatedTo;
        dispatcher = new WinUiDispatcher(queue);
        UnhandledException += OnUnhandledException;
        log.Write(uiOnly ? "启动无设备模式" : "启动 USB Display Beta");
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (window is not null) { window.RestoreAndActivate(); return; }
        ThemeWorkspaceLifetime? lifetime = null;
        try
        {
            var workspace = new WorkspaceViewModel();
            var storage = new StorageCoordinator(paths, diagnostics);
            var profiles = new FileProfileStore(paths, storage);
            var coordinator = new DeviceCoordinator(diagnostics, TimeProvider.System);
            IStartupRegistration startup = uiOnly
                ? new NoDeviceStartupRegistration()
                : new WindowsStartupRegistration(diagnostics, Program.ProductId, Program.ExecutableName);
            var settingsService = new ApplicationSettingsService(profiles, startup, diagnostics, () => coordinator.Current);
            var settings = new ApplicationSettingsPageViewModel(settingsService, diagnostics);
            string? updateRepository = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "BetaUpdateRepository")?.Value;
            var updateService = GitHubAppUpdateService.ForBeta(paths, diagnostics, !uiOnly, updateRepository);
            var updates = new AppUpdateViewModel(updateService, settings, dispatcher, diagnostics, updateService.ReleasePageUrl,
                updateService.ReleasePageUrl is null ? "此 Beta 包未配置在线更新。可使用测试方提供的完整安装包。" : null);
            settings.Updates = updates;
            var assets = new FileAssetStore(paths, new WpfImageValidator(diagnostics), storage);
            var renderer = new WpfFrameRenderer(assets, TimeProvider.System, diagnostics);
            var metrics = new MetricSamplingService(new WindowsHostMetricProvider(TimeProvider.System, diagnostics),
                TimeProvider.System, diagnostics);
            var decoder = new WinUiFramePreviewDecoder(dispatcher);
            var draftPreview = new DraftPreviewController(renderer, dispatcher, decoder, TimeProvider.System, diagnostics, metrics, assets);
            var sessions = new DeviceSessionLifetime(coordinator, diagnostics);
            var devices = new DevicesPageViewModel(coordinator, sessions, diagnostics, decoder);

            var deviceSettings = new DeviceSettingsPageViewModel(diagnostics, coordinator, sessions, metrics, devices);
            var pickers = new WinUiFilePickers(() => window?.AppWindow.Id);
            var themes = new ThemePageViewModel(assets, pickers, draftPreview, diagnostics, coordinator, sessions, metrics);
            var diagnosticsView = new DiagnosticsPageViewModel(diagnostics,
                new DiagnosticExportService(paths, diagnostics, TimeProvider.System),
                new DiagnosticFolderOpener(paths, diagnostics), pickers, themes.CaptureDiagnosticSummary, sessions);

            var snapshots = new DeviceSnapshotPresenter(coordinator, sessions, dispatcher, devices, workspace, diagnostics, themes, deviceSettings, settings);
            var discovery = new DeviceDiscoveryLifetime(dispatcher, devices, coordinator, sessions, diagnostics, exclusiveAccess: true);
            var protocol = new DeviceProtocolService(coordinator, sessions, discovery, profiles, assets, renderer, metrics, diagnostics, exclusiveAccess: true);
            if (!uiOnly) { devices.AttachProtocol(protocol, discovery.StartAsync); }
            lifetime = new ThemeWorkspaceLifetime(themes, settings, deviceSettings, diagnosticsView, draftPreview,
                renderer, metrics, sessions, coordinator, snapshots, discovery, diagnostics, protocol, devices);

            await deviceSettings.InitializeAsync();
            window = new MainWindow(log, workspace, devices, themes, deviceSettings, diagnosticsView, settings, diagnostics, uiOnly);
            var modeNotice = new ModeSwitchNoticeService(settingsService, window.ConfirmModeSwitchAsync);
            devices.AttachModeSwitchConfirmation(modeNotice.ConfirmAsync);
            lifetime.AttachNotices(new WorkspaceNoticeLifetime(dispatcher, window.ShowNotice, devices, themes, deviceSettings, diagnostics));
            var power = uiOnly ? null : new PowerSessionLifetime(coordinator, sessions, discovery, diagnostics);
            if (power is not null) { lifetime.AttachPowerLifetime(power.StopAsync); }
            var windowLifetime = new WinUiWindowLifetime(window, lifetime, settingsService, devices, power, dispatcher, diagnostics);
            window.Activate();
            windowLifetime.UpdateVisibility();
            log.Write("主窗口已打开");
            if (activationPending)
            {
                activationPending = false;
                window.RestoreAndActivate();
            }

            updates.Start(() => windowLifetime.RequestUpdateAsync(updateService), updatedTo,
                () => windowLifetime.IsReadyForUpdate);
            if (uiOnly) { await settings.InitializeAsync(); }
            else { await devices.InitializeProtocolAsync(settings, updatedTo is not null); }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = diagnostics.Report(exception, "Startup");
            log.Write($"启动失败，错误编号：{report.ErrorId}");
            Program.ShowMessage($"启动未完成，请重启后重试。错误编号：{report.ErrorId}", error: true);
            if (lifetime is not null) { await lifetime.StopAsync(); }
            if (window is null) { log.Dispose(); }
            else { window.Close(); }
            Exit();
        }
    }

    internal void RequestActivation() => dispatcher.TryEnqueue(() =>
    {
        if (window is null) { activationPending = true; }
        else { window.RestoreAndActivate(); }
    });

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        if (args.Exception is OutOfMemoryException) { return; }
        var report = diagnostics.Report(args.Exception, "WinUiUnhandled");
        log.Write($"窗口未处理异常，错误编号：{report.ErrorId}");
    }
}
