using System.Collections.Immutable;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Themes;

// 扫描结果是当次只读快照，不包含删除授权；清理仍须协调在途租约与全部写入。
public sealed record AssetReferenceInventory(ImmutableHashSet<string> AssetIds, int ConfigurationFiles);

public interface IAssetReferenceInventoryStore
{
    Task<OperationResult<AssetReferenceInventory>> ScanAssetReferencesAsync(CancellationToken cancellationToken);
}
