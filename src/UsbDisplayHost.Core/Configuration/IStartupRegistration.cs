using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Configuration;

public sealed record StartupRegistrationState(bool IsEnabled, bool IsManaged);

// 检查点内容只由平台适配器识别；Core 不持有注册表键或原生句柄。
public interface IStartupRegistrationCheckpoint;

public interface IStartupRegistration
{
    Task<OperationResult<StartupRegistrationState>> QueryAsync(CancellationToken cancellationToken);
    Task<OperationResult<IStartupRegistrationCheckpoint>> ChangeAsync(bool enabled, CancellationToken cancellationToken);
    Task<OperationResult<Unit>> RestoreAsync(IStartupRegistrationCheckpoint checkpoint, CancellationToken cancellationToken);
}
