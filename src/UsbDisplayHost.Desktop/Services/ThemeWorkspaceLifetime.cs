using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Themes;
using UsbDisplayHost.Core.Telemetry;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Desktop.ViewModels;

namespace UsbDisplayHost.Desktop.Services;

public sealed class ThemeWorkspaceLifetime(
    ThemePageViewModel themes, ApplicationSettingsPageViewModel settings, DeviceSettingsPageViewModel deviceSettings,
    DiagnosticsPageViewModel diagnosticPage, DraftPreviewController preview,
    IFrameRenderer renderer, MetricSamplingService metrics, DeviceSessionLifetime sessions,
    DeviceCoordinator deviceCoordinator, DeviceSnapshotPresenter deviceSnapshots, DeviceDiscoveryLifetime deviceDiscovery,
    IDiagnosticService errorReporter, DeviceProtocolService protocol, DevicesPageViewModel devices)
{
    private readonly object syncRoot = new();
    private Task<OperationResult<Unit>>? stopping;
    private WindowPowerLifetime? power;
    private WorkspaceNoticeLifetime? notices;
    public void AttachNotices(WorkspaceNoticeLifetime value)
    {
        lock (syncRoot)
        {
            if (notices is not null || stopping is not null) { throw new InvalidOperationException("工作区提示已附加或正在停止"); }
            notices = value;
        }
    }
    public void AttachPowerLifetime(WindowPowerLifetime value)
    {
        lock (syncRoot)
        {
            if (power is not null || stopping is not null) { throw new InvalidOperationException("工作区电源通知已附加或正在停止"); }
            power = value;
        }
    }
    public ThemePageViewModel Themes { get; } = themes;
    public ApplicationSettingsPageViewModel Settings { get; } = settings;
    public DeviceSettingsPageViewModel DeviceSettings { get; } = deviceSettings;
    public DeviceCoordinator Coordinator => deviceCoordinator;
    public DeviceDiscoveryLifetime Discovery => deviceDiscovery;
    public void SetWindowVisible(bool visible)
    {
        deviceSnapshots.SetVisible(visible);
        deviceDiscovery.SetVisible(visible);
    }
    public bool IsDeviceSessionBusy => sessions.IsBusy || devices.IsProtocolBusy;
    public bool HasPendingControlSave => protocol.HasPendingSave;
    public Task<OperationResult<Unit>> RetryControlSaveAsync() => protocol.RetrySaveAsync(CancellationToken.None);
    public void DiscardControlSave() => protocol.DiscardPendingSave();
    public async Task<OperationResult<Unit>> SwitchToSecondaryForExitAsync()
    {
        devices.IsProtocolBusy = true;
        try { return await protocol.SwitchModeAsync(DeviceMode.SecondaryDisplay, CancellationToken.None); }
        finally { devices.IsProtocolBusy = false; }
    }
    public bool NeedsExitModeSwitch => Settings.SavedSettings.SwitchToSecondaryOnExit
        && deviceCoordinator.Current is { Connection: ConnectionState.Ready, Mode: DeviceMode.Theme };
    public PendingThemeSave? PendingDeviceSave => sessions.PendingSave;
    public Task<OperationResult<PendingThemeSave>> RetryDeviceSaveAsync(Guid retryId) => sessions.RetrySaveAsync(retryId);
    public Task<OperationResult<Unit>> PrepareExitAsync(Guid? discardSaveId = null) => sessions.CloseAsync(discardSaveId);

    public Task<OperationResult<Unit>> StopAsync()
    {
        lock (syncRoot) { return stopping ??= StopCoreAsync(); }
    }

    private async Task<OperationResult<Unit>> StopCoreAsync()
    {
        var noticeStop = await StopResourceAsync(() => { notices?.Dispose(); return Task.CompletedTask; }, "WorkspaceNoticeStop");
        if (noticeStop is not OperationResult<Unit>.Success) { return noticeStop; }
        if (power is not null)
        {
            var powerStop = await power.StopAsync();
            if (powerStop is not OperationResult<Unit>.Success) { return powerStop; }
        }
        var commandStop = await StopResourceAsync(devices.StopAsync, "DeviceCommandsStop");
        if (commandStop is not OperationResult<Unit>.Success) { return commandStop; }
        var sessionClose = await PrepareExitAsync();
        if (sessionClose is not OperationResult<Unit>.Success)
        {
            errorReporter.Write(DiagnosticSeverity.Warning, "Application", "ShutdownBlockedBySession");
            return sessionClose;
        }
        var discoveryClose = await deviceDiscovery.CloseAsync();
        if (discoveryClose is not OperationResult<Unit>.Success) { return discoveryClose; }
        (Func<Task> Stop, string Stage)[] stages =
        [
            (Themes.StopAsync, "ThemeCommandsStop"),
            (Settings.StopAsync, "ApplicationSettingsCommandsStop"),
            (DeviceSettings.StopAsync, "DeviceSettingsCommandsStop"),
            (() => deviceSnapshots.DisposeAsync().AsTask(), "DeviceSnapshotSubscriptionStop"),
            (() => deviceCoordinator.DisposeAsync().AsTask(), "DeviceCoordinatorStop"),
            (() => preview.DisposeAsync().AsTask(), "DraftPreviewStop"),
            (() => renderer.DisposeAsync().AsTask(), "RendererStop"),
            (() => metrics.DisposeAsync().AsTask(), "MetricSamplingStop"),
            (diagnosticPage.StopAsync, "DiagnosticCommandsStop"),
            (() => errorReporter.DisposeAsync().AsTask(), "DiagnosticLogStop")
        ];
        foreach (var stage in stages)
        {
            var stopped = await StopResourceAsync(stage.Stop, stage.Stage);
            if (stopped is not OperationResult<Unit>.Success) { return stopped; }
        }
        return new OperationResult<Unit>.Success(new Unit());
    }

    private async Task<OperationResult<Unit>> StopResourceAsync(Func<Task> stop, string stage)
    {
        try
        {
            await stop();
            return new OperationResult<Unit>.Success(new Unit());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 保留后续依赖资源；错误记录不能替代前一阶段已经收敛的证明。
            var report = errorReporter.Report(exception, stage);
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "WorkspaceStopUnconfirmed",
                "工作区资源关闭尚未确认，窗口已保留", "请检查诊断；未确认收敛前不能继续退出"));
        }
    }
}
