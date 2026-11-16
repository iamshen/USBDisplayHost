using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.WinUI.Services;

internal sealed class NoDeviceStartupRegistration : IStartupRegistration
{
    public Task<OperationResult<StartupRegistrationState>> QueryAsync(CancellationToken cancellationToken) =>
        Task.FromResult<OperationResult<StartupRegistrationState>>(
            new OperationResult<StartupRegistrationState>.Success(new(false, false)));

    public Task<OperationResult<IStartupRegistrationCheckpoint>> ChangeAsync(bool enabled, CancellationToken cancellationToken) =>
        Disabled<IStartupRegistrationCheckpoint>();

    public Task<OperationResult<Unit>> RestoreAsync(IStartupRegistrationCheckpoint checkpoint, CancellationToken cancellationToken) =>
        Disabled<Unit>();

    private static Task<OperationResult<T>> Disabled<T>() => Task.FromResult<OperationResult<T>>(
        new OperationResult<T>.Failure(AppError.Create("UiOnlyStartupDisabled",
            "无设备模式不修改 Windows 自启", "如需配置自启，请打开已安装的应用")));
}
