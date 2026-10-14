using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Desktop.Services;

public sealed class ModeSwitchNoticeService(ApplicationSettingsService settings, Func<MainWindow?> getWindow)
{
    public async Task<OperationResult<Unit>> ConfirmAsync(CancellationToken token)
    {
        var loaded = await settings.LoadAsync(token);
        if (loaded is not OperationResult<ApplicationSettingsReadResult>.Success read)
        {
            return loaded switch
            {
                OperationResult<ApplicationSettingsReadResult>.Failure failure => new OperationResult<Unit>.Failure(failure.Error),
                OperationResult<ApplicationSettingsReadResult>.Unknown unknown => new OperationResult<Unit>.Unknown(unknown.Error),
                _ => new OperationResult<Unit>.Cancelled()
            };
        }
        if (read.Value.Settings.SuppressModeSwitchNotice && read.Value.Error is null)
        { return new OperationResult<Unit>.Success(new()); }
        if (getWindow() is not { } window) { return new OperationResult<Unit>.Cancelled(); }
        var answer = await window.ConfirmModeSwitchAsync(token);
        if (!answer.Confirmed) { return new OperationResult<Unit>.Cancelled(); }
        return answer.Suppress ? await settings.SuppressModeSwitchNoticeAsync(token)
            : new OperationResult<Unit>.Success(new());
    }
}
