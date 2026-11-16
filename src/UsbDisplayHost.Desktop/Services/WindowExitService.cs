using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Updates;
using UsbDisplayHost.Desktop.ViewModels;

namespace UsbDisplayHost.Desktop.Services;

public sealed class WindowExitService(Application application, ThemeWorkspaceLifetime workspace, IErrorReporter errorReporter,
    TimeSpan systemCleanupBudget, TimeProvider clock)
{
    private readonly WindowExitWorkflow workflow = new(workspace, errorReporter, systemCleanupBudget, clock);
    private TrayService? tray;

    public void Attach(Window window) => window.Closing += OnClosing;
    public void SetTray(TrayService trayService) => tray = trayService;
    public void NotifySystemExit() => workflow.NotifySystemExit();
    public Task<OperationResult<Unit>>? SystemCleanupResult => workflow.SystemCleanupResult;

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (workflow.IsSystemExiting) { return; }
        e.Cancel = true;
        if (sender is Window window) { await workflow.HandleClosingAsync(CreateHost(window)); }
    }

    public Task RequestExitAsync(Window window) => workflow.RequestExitAsync(CreateHost(window));

    public Task RequestUpdateAsync(Window window, IAppUpdateService updates) =>
        workflow.RequestUpdateAsync(CreateHost(window), updates);

    private IWindowExitHost CreateHost(Window window) => new ExitHost(window, application, tray, OnClosing);

    private sealed class ExitHost(Window window, Application application, TrayService? tray, CancelEventHandler onClosing)
        : IWindowExitHost
    {
        public bool HasTray => tray is not null;
        public bool IsClosed => application.Dispatcher.HasShutdownStarted;

        public void Show() => window.Show();

        public void SetEnabled(bool enabled) => window.IsEnabled = enabled;

        public void ShowError(string message)
        {
            if (window.DataContext is WorkspaceViewModel model) { model.ErrorText = message; }
        }

        public void FinishExit()
        {
            window.Closing -= onClosing;
            application.Shutdown();
        }

        public async Task<bool> DeferCloseAsync()
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            return true;
        }

        public Task<bool> TryHideInTrayAsync() => tray is { IsAvailable: true } ? tray.HideWindowAsync(window) : Task.FromResult(false);

        public Task WaitForPendingTrayNoticeAsync() => tray?.WaitForPendingNoticeAsync() ?? Task.CompletedTask;

        public async Task<WindowExitChoice> ShowDecisionAsync(string message, WindowExitPrompt prompt, CancellationToken cancellationToken)
        {
            var buttons = prompt switch
            {
                WindowExitPrompt.Save => MessageBoxButton.YesNoCancel,
                WindowExitPrompt.Information => MessageBoxButton.OK,
                _ => MessageBoxButton.YesNo
            };
            var result = window is MainWindow main
                ? await main.ShowDecisionAsync(message.Replace("选择“否”", "选择“放弃并退出”", StringComparison.Ordinal),
                    buttons, cancellationToken, installUpdate: prompt == WindowExitPrompt.Install)
                : MessageBox.Show(window, message, "USB Display", buttons, MessageBoxImage.Information);
            return result switch
            {
                MessageBoxResult.Yes => WindowExitChoice.Confirm,
                MessageBoxResult.No => WindowExitChoice.Discard,
                _ => WindowExitChoice.Cancel
            };
        }
    }
}
