using System.ComponentModel;
using System.Windows;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Updates;

namespace UsbDisplayHost.Desktop.Services;

public sealed class WindowExitService(Application application, ThemeWorkspaceLifetime workspace, IErrorReporter errorReporter,
    TimeSpan systemCleanupBudget, TimeProvider clock)
{
    private bool exiting;
    private bool systemExit;
    private Task<OperationResult<Unit>>? systemExitTask;
    private Task<OperationResult<Unit>>? systemCleanupOwner;
    private TrayService? tray;
    private bool closing;
    private readonly CancellationTokenSource dialogCancellation = new();

    public void Attach(Window window) => window.Closing += OnClosing;
    public void SetTray(TrayService trayService) => tray = trayService;

    public void NotifySystemExit()
    {
        systemExit = true;
        try { dialogCancellation.Cancel(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { errorReporter.Report(exception, "SystemExitDialogCancel"); }
        systemExitTask ??= StopSystemAsync();
    }

    public Task<OperationResult<Unit>>? SystemCleanupResult => systemExitTask;

    private async Task<MessageBoxResult> ConfirmSaveAsync(Window window, string message)
        => await ShowDialogAsync(window, message, MessageBoxButton.YesNoCancel);

    private async Task<MessageBoxResult> ShowDialogAsync(Window window, string message,
        MessageBoxButton buttons = MessageBoxButton.OK, bool installUpdate = false)
    {
        if (systemExit) { return MessageBoxResult.Cancel; }
        try
        {
            if (buttons == MessageBoxButton.OK) { window.IsEnabled = true; }
            var result = window is MainWindow main
                ? await main.ShowDecisionAsync(message.Replace("选择“否”", "选择“放弃并退出”", StringComparison.Ordinal), buttons, dialogCancellation.Token, installUpdate)
                : MessageBox.Show(window, message, "USB Display", buttons, MessageBoxImage.Information);
            return systemExit ? MessageBoxResult.Cancel : result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "ExitDialogPresentation");
            // 呈现失败不再次打开对话框，也不批准保存、放弃或退出。
            if (!systemExit)
            {
                try
                {
                    if (window.DataContext is ViewModels.WorkspaceViewModel model)
                    { model.ErrorText = $"退出提示未能显示，窗口已保留。错误编号：{report.ErrorId}"; }
                }
                catch (Exception secondary) when (secondary is not OutOfMemoryException)
                { errorReporter.Report(secondary, "ExitDialogFailureNotice"); }
            }
            return MessageBoxResult.Cancel;
        }
    }

    private async Task<OperationResult<Unit>> StopSystemAsync()
    {
        // 观察期限不取消实际资源所有者；超时后仍持有、观察原关闭任务。
        if (systemCleanupBudget < TimeSpan.FromMilliseconds(250) || systemCleanupBudget > TimeSpan.FromSeconds(10))
        {
            return new OperationResult<Unit>.Failure(AppError.Create("InvalidSystemCleanupBudget",
                "关机清理预算无效，未启动清理", "请检查应用生命周期配置"));
        }
        systemCleanupOwner ??= ObserveSystemCleanupAsync();
        try { return await systemCleanupOwner.WaitAsync(systemCleanupBudget, clock); }
        catch (TimeoutException exception)
        {
            var report = errorReporter.Report(exception, "SystemCleanupDeadline");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "SystemCleanupTimedOut",
                "关机清理超过应用等待预算，资源释放未确认", "保留实际清理所有者；不能据此确认句柄已释放"));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "SystemCleanupWait");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "SystemCleanupWaitUnconfirmed",
                "关机清理结果观察未完成", "请保留实际清理所有者与诊断，不声明资源已释放"));
        }
    }

    private async Task<OperationResult<Unit>> ObserveSystemCleanupAsync()
    {
        try { return await workspace.StopAsync(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "SystemCleanup");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "SystemCleanupUnconfirmed",
                "关机资源清理未确认", "保留实际所有者和诊断，不继续释放依赖资源"));
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (systemExit) { return; }
        e.Cancel = true;
        if (exiting || closing || sender is not Window window) { return; }
        closing = true;
        try
        {
            // Closing 尚在调用栈中时不能 Show；先让取消关闭的事件返回，再呈现退出决策。
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            if (systemExit) { return; }
            await workspace.Settings.InitializeAsync();
            if (systemExit) { return; }
            if (tray is null || !workspace.Settings.SavedSettings.CloseToTray) { await RequestExitAsync(window); return; }
            if (tray.IsAvailable && await tray.HideWindowAsync(window)) { return; }
            if (!systemExit && await ShowDialogAsync(window, "托盘暂不可用，窗口将保持打开。是否现在退出？",
                MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                await RequestExitAsync(window);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "CloseToTray");
            if (systemExit) { return; }
            try
            {
                window.Show();
                await ShowDialogAsync(window, $"收至托盘未完成，窗口已保留。错误编号：{report.ErrorId}");
            }
            catch (Exception secondary) when (secondary is not OutOfMemoryException)
            { errorReporter.Report(secondary, "CloseToTrayFailurePresentation"); }
        }
        finally { closing = false; }
    }

    public Task RequestExitAsync(Window window) => RequestExitCoreAsync(window, null);

    public Task RequestUpdateAsync(Window window, IAppUpdateService updates) => RequestExitCoreAsync(window, updates);

    private async Task RequestExitCoreAsync(Window window, IAppUpdateService? updates)
    {
        if (exiting || systemExit) { return; }
        exiting = true;
        bool preparingUpdate = false;
        bool handedOff = false;
        try
        {
            if (tray is not null) { await tray.WaitForPendingNoticeAsync(); }
            if (systemExit) { return; }
            window.Show();
            if (updates is not null)
            {
                if (updates.Snapshot.State != AppUpdateState.ReadyToInstall) { return; }
                if (await ShowDialogAsync(window,
                    $"是否重启并安装 {updates.Snapshot.TargetVersion}？将先处理未保存内容并关闭设备会话，更新后保留设备当前模式。",
                    MessageBoxButton.YesNo, installUpdate: true) != MessageBoxResult.Yes) { return; }
            }
            await workspace.Settings.InitializeAsync();
            if (systemExit) { return; }
            if (workspace.Themes.IsBusy || workspace.Settings.IsBusy || workspace.IsDeviceSessionBusy)
            {
                await ShowDialogAsync(window, "当前文件操作尚未完成，请等待或取消该操作后退出。");
                return;
            }
            if (workspace.Settings.IsDirty)
            {
                var pendingSettingsId = workspace.Settings.UnconfirmedSaveId;
                var settingsChoice = await ConfirmSaveAsync(window, workspace.Settings.HasUnconfirmedSave
                    ? "应用设置保存结果待确认。是否核实并保存后退出？选择“否”放弃待确认记录与未保存编辑，不回滚文件或自启。"
                    : "应用设置尚未保存。是否保存后退出？");
                if (settingsChoice == MessageBoxResult.Cancel) { return; }
                if (settingsChoice == MessageBoxResult.No && pendingSettingsId is { } expectedSettingsId
                    && !await workspace.Settings.DiscardUnconfirmedSaveAsync(expectedSettingsId)) { return; }
                if (settingsChoice == MessageBoxResult.Yes)
                {
                    if (workspace.Settings.HasUnconfirmedSave)
                    {
                        if (!workspace.Settings.VerifySaveCommand.CanExecute(null)) { return; }
                        await workspace.Settings.VerifySaveCommand.ExecuteAsync(null);
                        if (workspace.Settings.HasUnconfirmedSave) { return; }
                    }
                    if (workspace.Settings.IsDirty)
                    {
                        if (!workspace.Settings.SaveCommand.CanExecute(null))
                        {
                            await ShowDialogAsync(window, "应用设置当前不能保存，请检查设置页的错误提示。");
                            return;
                        }
                        await workspace.Settings.SaveCommand.ExecuteAsync(null);
                        if (!workspace.Settings.LastSaveSucceeded || workspace.Settings.IsDirty) { return; }
                    }
                }
            }
            if (workspace.DeviceSettings.IsBusy)
            {
                await ShowDialogAsync(window, "设备设置正在处理，请等待完成或取消应用后退出。");
                return;
            }
            if (workspace.DeviceSettings.IsDirty
                && await ShowDialogAsync(window, "设备设置有尚未应用的更改。退出将放弃这些更改。是否退出？",
                    MessageBoxButton.YesNo) != MessageBoxResult.Yes) { return; }
            if (workspace.Themes.IsDirty
                && await ShowDialogAsync(window, "主题有尚未应用的更改。退出将放弃这些更改。是否退出？",
                    MessageBoxButton.YesNo) != MessageBoxResult.Yes) { return; }
            if (workspace.IsDeviceSessionBusy)
            {
                await ShowDialogAsync(window, "设备应用或恢复配置保存正在处理，请等待结果后退出。");
                return;
            }
            Guid? discardSaveId = null;
            if (workspace.PendingDeviceSave is { } pendingSave)
            {
                var pendingChoice = await ConfirmSaveAsync(window,
                    "已应用的恢复配置尚未确认保存。是否仅重试保存后退出？\n选择“否”将放弃待保存结果，不撤销已经发送的画面或已发生的文件写入。");
                if (pendingChoice == MessageBoxResult.Cancel) { return; }
                if (pendingChoice == MessageBoxResult.Yes)
                {
                    var saved = await workspace.RetryDeviceSaveAsync(pendingSave.RetryId);
                    if (saved is not OperationResult<PendingThemeSave>.Success)
                    {
                        var error = saved switch
                        {
                            OperationResult<PendingThemeSave>.Failure failure => failure.Error,
                            OperationResult<PendingThemeSave>.Unknown unknown => unknown.Error,
                            _ => AppError.Create("ExitSaveUnconfirmed", "恢复配置保存未确认", "请保留窗口并核实保存结果")
                        };
                        await ShowDialogAsync(window, $"{error.Reason}。{error.Action}。错误编号：{error.ErrorId}");
                        return;
                    }
                }
                else { discardSaveId = pendingSave.RetryId; }
            }
            if (systemExit) { return; }
            if (workspace.HasPendingControlSave)
            {
                var choice = await ConfirmSaveAsync(window, "设备控制已执行，但本地设置尚未确认保存。是否仅重试本地保存后退出？选择“否”放弃本次保存结果。");
                if (choice == MessageBoxResult.Cancel) { return; }
                if (choice == MessageBoxResult.No) { workspace.DiscardControlSave(); }
                else if (await workspace.RetryControlSaveAsync() is not OperationResult<Unit>.Success)
                { await ShowDialogAsync(window, "设置保存仍未确认，请在设备页查看错误或明确放弃保存结果。"); return; }
            }
            if (updates is null && workspace.NeedsExitModeSwitch)
            {
                var switched = await workspace.SwitchToSecondaryForExitAsync();
                if (switched is not OperationResult<Unit>.Success)
                {
                    string reason = switched switch
                    {
                        OperationResult<Unit>.Failure failure => failure.Error.Reason,
                        OperationResult<Unit>.Unknown unknown => unknown.Error.Reason,
                        _ => "模式切换已取消"
                    };
                    if (await ShowDialogAsync(window, $"{reason}。是否继续退出？", MessageBoxButton.YesNo) != MessageBoxResult.Yes) { return; }
                }
            }
            if (systemExit) { return; }
            window.IsEnabled = false;
            preparingUpdate = updates is not null;
            var sessionClose = updates is null ? await workspace.PrepareExitAsync(discardSaveId)
                : await workspace.PrepareUpdateAsync(discardSaveId);
            if (sessionClose is not OperationResult<Unit>.Success)
            {
                var error = sessionClose switch
                {
                    OperationResult<Unit>.Failure failure => failure.Error,
                    OperationResult<Unit>.Unknown unknown => unknown.Error,
                    _ => AppError.Create("ExitSessionCloseUnconfirmed", "设备会话关闭未确认", "请检查诊断并保留窗口")
                };
                window.IsEnabled = true;
                await ShowDialogAsync(window, $"{error.Reason}。{error.Action}。错误编号：{error.ErrorId}");
                return;
            }
            if (systemExit) { return; }
            if (updates is not null)
            {
                // 原生 I/O 和配置保存均已确认结束。更新器仅等待正常退出，不在会话关闭前启动。
                var handoff = await updates.HandOffInstallationAsync(dialogCancellation.Token);
                if (handoff is not OperationResult<Unit>.Success)
                {
                    workspace.ResumeAfterUpdateLaunchFailure();
                    string reason = handoff is OperationResult<Unit>.Failure failure
                        ? $"{failure.Error.Reason}。错误编号：{failure.Error.ErrorId}" : "安装交接已取消";
                    await ShowDialogAsync(window, reason + "。当前程序已保留，可重新检测并连接设备。");
                    return;
                }
                handedOff = true;
            }
            var workspaceStop = await workspace.StopAsync();
            if (workspaceStop is not OperationResult<Unit>.Success)
            {
                var error = workspaceStop switch
                {
                    OperationResult<Unit>.Failure failure => failure.Error,
                    OperationResult<Unit>.Unknown unknown => unknown.Error,
                    _ => AppError.Create("ExitWorkspaceStopUnconfirmed", "工作区关闭未确认", "请检查诊断并保留窗口")
                };
                await ShowDialogAsync(window, $"{error.Reason}。{error.Action}。错误编号：{error.ErrorId}");
                return;
            }
            window.Closing -= OnClosing;
            application.Shutdown();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            window.IsEnabled = true;
            var report = errorReporter.Report(exception, "WindowExit");
            if (systemExit) { return; }
            await ShowDialogAsync(window, $"退出未完成，请重试。错误编号：{report.ErrorId}");
        }
        finally
        {
            if (preparingUpdate && !handedOff && !systemExit) { workspace.ResumeAfterUpdateLaunchFailure(); }
            if (!application.Dispatcher.HasShutdownStarted)
            {
                window.IsEnabled = true;
                exiting = false;
            }
        }
    }
}
