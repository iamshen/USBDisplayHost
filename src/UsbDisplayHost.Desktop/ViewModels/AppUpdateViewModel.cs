using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Updates;
using UsbDisplayHost.Windows.Updates;

namespace UsbDisplayHost.Desktop.ViewModels;

public sealed partial class AppUpdateViewModel : ObservableObject
{
    private readonly IAppUpdateService service;
    private readonly ApplicationSettingsPageViewModel settings;
    private readonly Dispatcher dispatcher;
    private readonly IErrorReporter reporter;
    private readonly CancellationTokenSource lifetime = new();
    private Task? background;
    private Func<Task>? install;
    private bool initialized;
    private bool stopping;
    private string? updatedVersion;
    private AppUpdateSnapshot snapshot;

    public AppUpdateViewModel(IAppUpdateService service, ApplicationSettingsPageViewModel settings,
        Dispatcher dispatcher, IErrorReporter reporter)
    {
        this.service = service; this.settings = settings; this.dispatcher = dispatcher; this.reporter = reporter;
        snapshot = service.Snapshot;
        service.Changed += OnChanged;
    }

    public string CurrentVersion => snapshot.CurrentVersion;
    public string ActionText => snapshot.State switch
    {
        AppUpdateState.Available => "下载更新",
        AppUpdateState.ReadyToInstall => "重启并安装",
        _ => "检查更新"
    };
    public string StatusText => snapshot.State switch
    {
        AppUpdateState.Unavailable => "开发或便携构建不支持应用内更新，请下载并安装完整版本。",
        AppUpdateState.Checking => "正在检查 GitHub Release",
        AppUpdateState.UpToDate => updatedVersion is null ? "未发现更高的已发布版本" : $"已更新至 {updatedVersion}",
        AppUpdateState.Available => $"发现新版本 {snapshot.TargetVersion}",
        AppUpdateState.Downloading => snapshot.TotalBytes is > 0
            ? $"正在下载 {snapshot.TargetVersion} · {Size(snapshot.DownloadedBytes)} / {Size(snapshot.TotalBytes.Value)}"
            : $"正在下载 {snapshot.TargetVersion} · {Size(snapshot.DownloadedBytes)}",
        AppUpdateState.Verifying => "正在校验更新包",
        AppUpdateState.ReadyToInstall => $"{snapshot.TargetVersion} 已准备好，可现在安装或稍后操作",
        AppUpdateState.Installing => "正在交接安装",
        AppUpdateState.Failed => "更新未完成，可重试",
        _ => updatedVersion is null ? "尚未检查更新" : $"已更新至 {updatedVersion}"
    };
    public bool IsProgressVisible => snapshot.State is AppUpdateState.Downloading or AppUpdateState.Verifying;
    public bool IsIndeterminate => snapshot.State == AppUpdateState.Verifying || snapshot.TotalBytes is not > 0;
    public double Progress => snapshot.TotalBytes is > 0 ? Math.Clamp(snapshot.DownloadedBytes * 100d / snapshot.TotalBytes.Value, 0, 100) : 0;
    public bool CanCancel => ActCommand.IsRunning && snapshot.State is AppUpdateState.Checking or AppUpdateState.Downloading;
    public string ErrorText => snapshot.Error is { } error ? $"{error.Reason}。{error.Action}。错误编号：{error.ErrorId}" : "";

    public void Start(Func<Task> requestInstall, string? expectedVersion, Func<bool> desktopReady)
    {
        install = requestInstall;
        background ??= RunBackgroundAsync(expectedVersion, desktopReady);
    }

    private async Task RunBackgroundAsync(string? expectedVersion, Func<bool> desktopReady)
    {
        try
        {
            await settings.InitializeAsync();
            await service.InitializeAsync(lifetime.Token);
            initialized = true;
            if (expectedVersion == CurrentVersion && settings.IsReady && settings.ErrorText.Length == 0 && desktopReady())
            { updatedVersion = expectedVersion; }
            Refresh();
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
            do
            {
                if (settings.IsReady && settings.ErrorText.Length == 0 && !settings.HasUnconfirmedSave
                    && settings.SavedSettings.AutomaticallyCheckUpdates)
                { await service.CheckAsync(false, lifetime.Token); }
            } while (await timer.WaitForNextTickAsync(lifetime.Token));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { reporter.Report(exception, "AppUpdateBackground"); }
    }

    private bool CanAct() => initialized && !stopping && snapshot.State is
        AppUpdateState.Idle or AppUpdateState.UpToDate or AppUpdateState.Available or AppUpdateState.ReadyToInstall or AppUpdateState.Failed;

    [RelayCommand(CanExecute = nameof(CanAct), IncludeCancelCommand = true)]
    private async Task ActAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        try
        {
            if (snapshot.State == AppUpdateState.ReadyToInstall)
            {
                if (install is not null) { await install(); }
            }
            else if (snapshot.State == AppUpdateState.Available) { await service.DownloadAsync(linked.Token); }
            else { await service.CheckAsync(true, linked.Token); }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { reporter.Report(exception, "AppUpdateCommand"); }
        finally { Refresh(); }
    }

    [RelayCommand]
    private void OpenReleases()
    {
        try { Process.Start(new ProcessStartInfo(GitHubAppUpdateService.ReleasesUrl) { UseShellExecute = true }); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { reporter.Report(exception, "OpenGitHubReleases"); }
    }

    private void OnChanged(AppUpdateSnapshot value)
    {
        if (dispatcher.HasShutdownStarted || stopping) { return; }
        dispatcher.BeginInvoke(() => { if (!stopping) { snapshot = value; Refresh(); } });
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(ErrorText)); OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(IsProgressVisible)); OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(CanCancel)); ActCommand.NotifyCanExecuteChanged();
    }

    public async Task StopAsync()
    {
        stopping = true;
        service.Changed -= OnChanged;
        lifetime.Cancel();
        ActCommand.NotifyCanExecuteChanged();
        if (background is not null) { await background; }
        // 安装命令自身会调用退出清理，不能在这里等待该命令。
        if (service.Snapshot.State != AppUpdateState.Installing && ActCommand.ExecutionTask is { } active) { await active; }
    }

    private static string Size(long bytes) => $"{bytes / 1048576d:F1} MB";
}
