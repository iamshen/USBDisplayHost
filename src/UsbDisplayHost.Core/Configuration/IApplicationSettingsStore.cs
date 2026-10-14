using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Configuration;

public sealed record ApplicationSettingsReadResult(
    ApplicationSettings Settings, ProfileRecovery Recovery, AppError? Error);

public interface IApplicationSettingsStore
{
    Task<OperationResult<ApplicationSettingsReadResult>> LoadSettingsAsync(CancellationToken cancellationToken);
    Task<OperationResult<Unit>> SaveSettingsAsync(ApplicationSettings settings, CancellationToken cancellationToken);
}
