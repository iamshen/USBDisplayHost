using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Updates;

public enum AppUpdateState { Unavailable, Idle, Checking, UpToDate, Available, Downloading, Verifying, ReadyToInstall, Installing, Failed }
public sealed record AppUpdateSnapshot(
    AppUpdateState State, string CurrentVersion, string? TargetVersion,
    long DownloadedBytes, long? TotalBytes, DateTimeOffset? LastAutomaticAttempt, AppError? Error);

public interface IAppUpdateService
{
    AppUpdateSnapshot Snapshot { get; }
    event Action<AppUpdateSnapshot>? Changed;
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<OperationResult<Unit>> CheckAsync(bool isManual, CancellationToken cancellationToken);
    Task<OperationResult<Unit>> DownloadAsync(CancellationToken cancellationToken);
    // 安装交接由生命周期先完成设备及配置清理，再调用此入口。
    Task<OperationResult<Unit>> HandOffInstallationAsync(CancellationToken cancellationToken);
}
