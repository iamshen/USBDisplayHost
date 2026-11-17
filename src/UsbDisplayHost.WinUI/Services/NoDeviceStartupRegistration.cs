using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.WinUI.Services;

internal sealed class NoDeviceStartupRegistration : IStartupRegistration
{
    private sealed class NoDeviceCheckpoint : IStartupRegistrationCheckpoint;

    public Task<OperationResult<StartupRegistrationState>> QueryAsync(CancellationToken cancellationToken) =>
        Disabled<StartupRegistrationState>();

    public Task<OperationResult<IStartupRegistrationCheckpoint>> ChangeAsync(bool enabled, CancellationToken cancellationToken) =>
        enabled ? Disabled<IStartupRegistrationCheckpoint>()
            : Task.FromResult<OperationResult<IStartupRegistrationCheckpoint>>(
                new OperationResult<IStartupRegistrationCheckpoint>.Success(new NoDeviceCheckpoint()));

    public Task<OperationResult<Unit>> RestoreAsync(IStartupRegistrationCheckpoint checkpoint, CancellationToken cancellationToken) =>
        checkpoint is NoDeviceCheckpoint
            ? Task.FromResult<OperationResult<Unit>>(new OperationResult<Unit>.Success(new Unit()))
            : Disabled<Unit>();

    private static Task<OperationResult<T>> Disabled<T>() => Task.FromResult<OperationResult<T>>(
        new OperationResult<T>.Failure(AppError.Create("UiOnlyStartupDisabled",
            "无设备模式不修改 Windows 自启", "如需配置自启，请打开已安装的应用")));
}
