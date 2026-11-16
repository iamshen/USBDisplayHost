using System.Windows.Threading;

namespace UsbDisplayHost.Desktop.Services;

internal sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public bool HasThreadAccess => dispatcher.CheckAccess();

    public bool TryEnqueue(Action callback, bool background = false)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) { return false; }
        var priority = background ? DispatcherPriority.Background : DispatcherPriority.Normal;
        return dispatcher.BeginInvoke(priority, callback).Status != DispatcherOperationStatus.Aborted;
    }
}
