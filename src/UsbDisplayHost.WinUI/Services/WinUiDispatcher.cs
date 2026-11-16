using Microsoft.UI.Dispatching;
using UsbDisplayHost.Desktop.Services;

namespace UsbDisplayHost.WinUI.Services;

internal sealed class WinUiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public bool HasThreadAccess => queue.HasThreadAccess;

    public bool TryEnqueue(Action callback, bool background = false) =>
        queue.TryEnqueue(background ? DispatcherQueuePriority.Low : DispatcherQueuePriority.Normal, () => callback());
}
