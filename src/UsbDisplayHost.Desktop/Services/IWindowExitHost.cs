namespace UsbDisplayHost.Desktop.Services;

internal enum WindowExitPrompt { Information, Continue, Save, Install }
internal enum WindowExitChoice { Cancel, Confirm, Discard }

internal interface IWindowExitHost
{
    bool HasTray { get; }
    bool IsClosed { get; }

    void Show();
    void ShowError(string message);
    void SetEnabled(bool enabled);
    void FinishExit();
    Task<bool> DeferCloseAsync();
    Task<bool> TryHideInTrayAsync();
    Task WaitForPendingTrayNoticeAsync();
    Task<WindowExitChoice> ShowDecisionAsync(string message, WindowExitPrompt prompt, CancellationToken cancellationToken);
}
