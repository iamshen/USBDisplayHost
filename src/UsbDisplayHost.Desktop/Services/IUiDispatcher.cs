namespace UsbDisplayHost.Desktop.Services;

public interface IUiDispatcher
{
    bool HasThreadAccess { get; }

    // 始终异步投递。返回 false 表示界面队列已停止接收工作。
    bool TryEnqueue(Action callback, bool background = false);
}
