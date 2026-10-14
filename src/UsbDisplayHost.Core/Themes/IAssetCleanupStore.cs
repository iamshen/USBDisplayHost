using System.Collections.Immutable;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Themes;

public enum AssetCleanupState { Completed, Cancelled, Failed, Unknown }

// 批次可以部分完成；仅 DeletedAssetIds 中的素材已确认删除，不自动重放未知结果。
public sealed record AssetCleanupOutcome(AssetCleanupState State, ImmutableArray<string> DeletedAssetIds,
    int RetainedFiles, AppError? Error);

public sealed record AssetCleanupRecovery(string AssetId, AppError Error);

public interface IAssetCleanupStore
{
    AssetCleanupRecovery? PendingCleanupRecovery { get; }
    Task<AssetCleanupOutcome> CleanupUnreferencedAsync(CancellationToken cancellationToken);
    // 查询素材是否已不存在，不再次删除素材；确认后可收敛意图日志，仍存在则保留未知状态。
    Task<OperationResult<Unit>> VerifyUnknownCleanupAsync(CancellationToken cancellationToken);
}
