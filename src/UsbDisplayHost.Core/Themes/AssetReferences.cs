using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Themes;

public enum AssetReferenceOwner { Rendering, Candidate, ActiveProfile, PendingRestore, DraftEditor, Importing, ConfigurationRead }

public sealed class AssetReferenceLease : IDisposable
{
    private Action? release;
    internal AssetReferenceLease(string assetId, AssetReferenceOwner owner, Action release)
    {
        AssetId = assetId; Owner = owner; this.release = release;
    }
    public string AssetId { get; }
    public AssetReferenceOwner Owner { get; }
    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
}

// 仅预约本进程引用互斥；调用方仍须在同一存储事务中核实磁盘引用与实际文件。
public sealed class AssetCleanupReservation : IDisposable
{
    private Action? release;
    internal AssetCleanupReservation(string assetId, Action release) { AssetId = assetId; this.release = release; }
    public string AssetId { get; }
    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
}

// 进程内临时引用；磁盘恢复／草稿／备份引用仍须另行核实，零租约不授权删除文件。
public sealed class AssetReferenceRegistry
{
    private const int MaximumLeases = 4096;
    private readonly object syncRoot = new();
    private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
    private int leases;
    private string? cleanupAsset;

    public OperationResult<AssetReferenceLease> Acquire(string assetId, AssetReferenceOwner owner)
    {
        if (string.IsNullOrEmpty(assetId) || assetId.Length != 32 || assetId.Any(character => !char.IsAsciiHexDigit(character))
            || !Enum.IsDefined(owner))
        {
            return new OperationResult<AssetReferenceLease>.Failure(AppError.Create("InvalidAssetReference",
                "素材引用标识或用途无效", "请重新选择已导入素材"));
        }
        string canonical = assetId.ToLowerInvariant();
        lock (syncRoot)
        {
            if (cleanupAsset == canonical)
            {
                return new OperationResult<AssetReferenceLease>.Failure(AppError.Create("AssetCleanupReserved",
                    "该素材正在核实清理，未取得引用", "请等待素材操作结束后重新选择"));
            }
            if (leases == MaximumLeases)
            {
                return new OperationResult<AssetReferenceLease>.Failure(AppError.Create("AssetReferenceLimit",
                    "素材在途引用达到限额", "请等待现有渲染与配置操作收敛"));
            }
            counts.TryGetValue(canonical, out int count);
            counts[canonical] = count + 1;
            leases++;
            return new OperationResult<AssetReferenceLease>.Success(new(canonical, owner, () => Release(canonical)));
        }
    }

    public int GetReferenceCount(string? assetId)
    {
        if (string.IsNullOrEmpty(assetId)) { return 0; }
        lock (syncRoot) { return counts.GetValueOrDefault(assetId.ToLowerInvariant()); }
    }
    public int LeaseCount { get { lock (syncRoot) { return leases; } } }

    public OperationResult<AssetCleanupReservation> ReserveUnreferenced(string assetId)
    {
        if (string.IsNullOrEmpty(assetId) || assetId.Length != 32 || assetId.Any(character => !char.IsAsciiHexDigit(character)))
        {
            return new OperationResult<AssetCleanupReservation>.Failure(AppError.Create("InvalidAssetCleanupId",
                "素材清理标识无效", "请使用已核实的素材标识"));
        }
        string canonical = assetId.ToLowerInvariant();
        lock (syncRoot)
        {
            if (cleanupAsset is not null)
            {
                return new OperationResult<AssetCleanupReservation>.Failure(AppError.Create("AssetCleanupBusy",
                    "已有素材清理预约，未建立新的预约", "请等待现有清理操作结束"));
            }
            if (counts.ContainsKey(canonical))
            {
                return new OperationResult<AssetCleanupReservation>.Failure(AppError.Create("AssetStillReferenced",
                    "素材仍被编辑、渲染或配置持有，不能预约清理", "请保留素材"));
            }
            // 预约与租约取得共用一把锁，不用先查计数再另行标记。
            var reservation = new AssetCleanupReservation(canonical, () => ReleaseCleanup(canonical));
            cleanupAsset = canonical;
            return new OperationResult<AssetCleanupReservation>.Success(reservation);
        }
    }

    private void ReleaseCleanup(string assetId)
    {
        lock (syncRoot)
        {
            if (cleanupAsset == assetId) { cleanupAsset = null; }
        }
    }

    private void Release(string assetId)
    {
        lock (syncRoot)
        {
            int count = counts[assetId];
            if (count == 1) { counts.Remove(assetId); }
            else { counts[assetId] = count - 1; }
            leases--;
        }
    }
}
