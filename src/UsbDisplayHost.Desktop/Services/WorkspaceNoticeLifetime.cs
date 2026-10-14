using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Desktop.ViewModels;

namespace UsbDisplayHost.Desktop.Services;

// 只展示已经结束操作的页面结果，不参与设备状态或保存成功判定。
public sealed class WorkspaceNoticeLifetime : IDisposable
{
    private sealed record Notice(string Text, bool Failed);
    private readonly object sync = new();
    private readonly MainWindow window;
    private readonly IErrorReporter reporter;
    private readonly Dictionary<IAsyncRelayCommand, Func<Notice>> commands;
    private readonly HashSet<IAsyncRelayCommand> accepted = [];
    private readonly Func<IAsyncRelayCommand, bool> isExecuting;
    private DispatcherOperation? pending;
    private Notice? latest;
    private bool disposed;

    public WorkspaceNoticeLifetime(MainWindow window, DevicesPageViewModel devices, ThemePageViewModel themes,
        DeviceSettingsPageViewModel settings, IErrorReporter reporter)
    {
        this.window = window; this.reporter = reporter;
        isExecuting = command => ReferenceEquals(command, themes.ApplyCommand) ? themes.IsApplying
            : ReferenceEquals(command, settings.ApplyCommand) ? settings.IsApplying
            : ReferenceEquals(command, devices.DisconnectCommand) ? devices.IsDisconnecting
            : ReferenceEquals(command, devices.RetryPendingSaveCommand) ? devices.IsRetryingPendingSave
            : devices.IsChangingStreaming;
        commands = new()
        {
            [devices.PauseStreamingCommand] = () => new(devices.StreamingResultText, devices.StreamingErrorText.Length != 0),
            [devices.ResumeStreamingCommand] = () => new(devices.StreamingResultText, devices.StreamingErrorText.Length != 0),
            [devices.DisconnectCommand] = () => new(devices.DisconnectText, devices.DisconnectErrorText.Length != 0),
            [devices.RetryPendingSaveCommand] = () => new(devices.LocalSaveResultText, devices.LocalSaveErrorText.Length != 0),
            [themes.ApplyCommand] = () => new(themes.ApplicationText, themes.ErrorText.Length != 0),
            [settings.ApplyCommand] = () => new(settings.ApplicationText, settings.ErrorText.Length != 0)
        };
        foreach (var command in commands.Keys) { command.PropertyChanged += OnCommandChanged; }
    }
    private void OnCommandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IAsyncRelayCommand.IsRunning) || sender is not IAsyncRelayCommand command) { return; }
        try
        {
            lock (sync)
            {
                if (disposed || !commands.TryGetValue(command, out var read)) { return; }
                if (command.IsRunning)
                {
                    if (isExecuting(command)) { accepted.Add(command); }
                    return;
                }
                if (!accepted.Remove(command) || command.ExecutionTask is not { IsCompletedSuccessfully: true }) { return; }
                latest = read();
                if (string.IsNullOrWhiteSpace(latest.Text) || pending is not null
                    || window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished) { return; }
                pending = window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Present));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { reporter.Report(exception, "WorkspaceNoticeSchedule"); }
    }
    private void Present()
    {
        Notice? notice;
        lock (sync)
        {
            pending = null;
            if (disposed) { return; }
            notice = latest; latest = null;
        }
        if (notice is null) { return; }
        try { window.ShowNotice(notice.Text, notice.Failed); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { reporter.Report(exception, "WorkspaceNoticePresentation"); }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) { return; }
            disposed = true;
            foreach (var command in commands.Keys) { command.PropertyChanged -= OnCommandChanged; }
            commands.Clear(); accepted.Clear(); latest = null;
            pending?.Abort(); pending = null;
        }
    }
}
