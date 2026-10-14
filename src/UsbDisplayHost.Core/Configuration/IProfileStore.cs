using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Core.Configuration;

public enum ProfileRecovery { Missing, Primary, Backup, Defaults, UnsupportedVersion }
public sealed record ProfileApplicationSaveAttempt(DeviceProfile Candidate, OperationResult<Unit> Write);
// 成功读取结果持有素材引用，调用方接收自身引用后释放结果；失败路径也须释放成功结果。
public sealed record ProfileReadResult(DeviceProfile? Profile, ProfileRecovery Recovery, AppError? Error,
    AssetReferenceLease? Reference = null) : IDisposable
{
    public void Dispose() => Reference?.Dispose();
}
public sealed record DraftReadResult(ThemeDraft? Draft, ProfileRecovery Recovery, AppError? Error,
    AssetReferenceLease? Reference = null) : IDisposable
{
    public void Dispose() => Reference?.Dispose();
}

public interface IProfileStore
{
    Task<OperationResult<ProfileReadResult>> LoadAsync(DeviceKey deviceKey, CancellationToken cancellationToken);
    Task<OperationResult<Unit>> SaveAsync(DeviceProfile profile, CancellationToken cancellationToken);
    Task<OperationResult<ProfileApplicationSaveAttempt>> SaveControlAsync(DeviceProfile candidate, CancellationToken cancellationToken);
    Task<OperationResult<ProfileApplicationSaveAttempt>> SaveApplicationAsync(DeviceProfile candidate,
        DeviceProfile? previousRestore, CancellationToken cancellationToken);
    Task<OperationResult<Unit>> SaveDraftAsync(ThemeDraft draft, CancellationToken cancellationToken);
    Task<OperationResult<DraftReadResult>> LoadDraftAsync(DeviceKey? deviceKey, CancellationToken cancellationToken);
}
