using System.Collections.Immutable;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Windows.Storage;

public sealed partial class FileAssetStore
{
    private AssetCleanupRecovery? pendingCleanupRecovery;
    public AssetCleanupRecovery? PendingCleanupRecovery => Volatile.Read(ref pendingCleanupRecovery);

    public Task<OperationResult<Unit>> VerifyUnknownCleanupAsync(CancellationToken cancellationToken) =>
        boundary.RunAsync<Unit>(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var persisted = await AssetCleanupJournal.ReadAsync(paths, cancellationToken).ConfigureAwait(false);
            var recovery = PendingCleanupRecovery ?? persisted;
            if (persisted is not null && recovery is not null
                && (recovery.AssetId != persisted.AssetId || recovery.Error.ErrorId != persisted.Error.ErrorId))
            {
                return new OperationResult<Unit>.Failure(AppError.Create("AssetCleanupJournalMismatch",
                    "内存与磁盘清理记录不一致，未解除暂停", "请核实数据目录归属与未完成记录"));
            }
            if (recovery is null) { return new OperationResult<Unit>.Success(new()); }
            Volatile.Write(ref pendingCleanupRecovery, recovery);
            try
            {
                AssetFileDeletion.VerifyAbsent(Path.Combine(paths.Assets, recovery.AssetId + ".image"));
                if (persisted is not null) { AssetCleanupJournal.Complete(paths); }
                Volatile.Write(ref pendingCleanupRecovery, null);
                return new OperationResult<Unit>.Success(new());
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                errorReporter.Report(exception, "AssetCleanupVerification",
                    new UsbDisplayHost.Core.Diagnostics.DiagnosticContext(OperationId: Guid.ParseExact(recovery.Error.ErrorId, "N")));
                return new OperationResult<Unit>.Unknown(recovery.Error);
            }
        }, "AssetCleanupVerification", cancellationToken);

    public async Task<AssetCleanupOutcome> CleanupUnreferencedAsync(CancellationToken cancellationToken)
    {
        var deleted = ImmutableArray.CreateBuilder<string>();
        int retained = 0;
        bool deletionUnconfirmed = false;
        var result = await boundary.RunAsync<Unit>(async () =>
        {
            if (PendingCleanupRecovery is { } recovery)
            {
                return new OperationResult<Unit>.Unknown(recovery.Error);
            }
            var persisted = await AssetCleanupJournal.ReadAsync(paths, cancellationToken).ConfigureAwait(false);
            if (persisted is not null)
            {
                Volatile.Write(ref pendingCleanupRecovery, persisted);
                return new OperationResult<Unit>.Unknown(persisted.Error);
            }
            var scan = await AssetReferenceScanner.ScanAsync(paths, cancellationToken).ConfigureAwait(false);
            if (scan is OperationResult<AssetReferenceInventory>.Failure failure)
            {
                return new OperationResult<Unit>.Failure(failure.Error);
            }
            if (scan is not OperationResult<AssetReferenceInventory>.Success inventory)
            {
                return new OperationResult<Unit>.Failure(AppError.Create("AssetInventoryUnconfirmed",
                    "素材配置引用未完整核实，未开始清理", "请核实数据目录后重试"));
            }
            // 完整预检先于首次删除；不递归、不处理临时或未知文件，不接受非规范素材名。
            var candidates = new List<(string Path, string Id)>();
            try
            {
                var directoryAttributes = File.GetAttributes(paths.Assets);
                if ((directoryAttributes & FileAttributes.Directory) == 0 || (directoryAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Assets directory is not an ordinary directory.");
                }
            }
            catch (DirectoryNotFoundException) { return new OperationResult<Unit>.Success(new()); }
            catch (FileNotFoundException) { return new OperationResult<Unit>.Success(new()); }
            foreach (string entry in Directory.EnumerateFileSystemEntries(paths.Assets))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(entry);
                var attributes = File.GetAttributes(entry);
                if (candidates.Count == 4096 || (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                    || name.Length != 38 || !name.EndsWith(".image", StringComparison.Ordinal)
                    || name.AsSpan(0, 32).ContainsAnyExcept("0123456789abcdef"))
                {
                    return new OperationResult<Unit>.Failure(AppError.Create("AssetCleanupUnverifiedEntry",
                        "素材目录包含未核实条目或超过清理数量上限，未开始清理", "请核实目录中的临时、未知或重解析条目"));
                }
                candidates.Add((Path.GetFullPath(entry), name[..32]));
            }
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (inventory.Value.AssetIds.Contains(candidate.Id)) { retained++; continue; }
                var reservation = references.ReserveUnreferenced(candidate.Id);
                if (reservation is OperationResult<AssetCleanupReservation>.Failure reservedFailure
                    && reservedFailure.Error.Code == "AssetStillReferenced") { retained++; continue; }
                if (reservation is not OperationResult<AssetCleanupReservation>.Success reserved)
                {
                    return new OperationResult<Unit>.Failure(AppError.Create("AssetCleanupReservationFailed",
                        "未取得素材清理预约，已停止当前批次", "请保留已确认结果并等待现有素材操作收敛"));
                }
                using (reserved.Value)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var intent = new AssetCleanupRecovery(candidate.Id, AppError.Create("AssetCleanupUnconfirmed",
                        "素材清理事务结果未确认，后续清理已暂停", "请先核实删除结果；不会自动再次删除该素材"));
                    try
                    {
                        // 写入意图之前建立内存保护；创建／刷新记录失败也不能继续删除。
                        deletionUnconfirmed = true;
                        Volatile.Write(ref pendingCleanupRecovery, intent);
                        await AssetCleanupJournal.CreateAsync(paths, intent, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        await Task.Run(() => AssetFileDeletion.DeleteVerified(candidate.Path,
                            static () => { }), cancellationToken).ConfigureAwait(false);
                        deleted.Add(candidate.Id);
                        AssetCleanupJournal.Complete(paths);
                        Volatile.Write(ref pendingCleanupRecovery, null);
                        deletionUnconfirmed = false;
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException && deletionUnconfirmed)
                    {
                        errorReporter.Report(exception, "AssetCleanupUnconfirmed",
                            new UsbDisplayHost.Core.Diagnostics.DiagnosticContext(OperationId: Guid.ParseExact(intent.Error.ErrorId, "N")));
                        // 在释放共同存储边界前锁住后续清理，避免排队请求自动重放未知删除。
                        return new OperationResult<Unit>.Unknown(intent.Error);
                    }
                }
            }
            return new OperationResult<Unit>.Success(new());
        }, "AssetCleanup", cancellationToken).ConfigureAwait(false);

        AppError? error = result switch
        {
            OperationResult<Unit>.Failure failed => failed.Error,
            OperationResult<Unit>.Unknown unknown => unknown.Error,
            _ => null
        };
        if (error is not null && (deleted.Count > 0 || deletionUnconfirmed))
        {
            error = error with { Reason = deletionUnconfirmed ? "素材删除结果未确认，已停止当前批次" : "素材清理未全部完成，已保留确认结果",
                Action = "请保留已确认删除清单，核实目录、权限与文件占用；不要自动重放未知删除" };
        }
        var state = deletionUnconfirmed ? AssetCleanupState.Unknown : result switch
        {
            OperationResult<Unit>.Success => AssetCleanupState.Completed,
            OperationResult<Unit>.Cancelled => AssetCleanupState.Cancelled,
            OperationResult<Unit>.Unknown => AssetCleanupState.Unknown,
            _ => AssetCleanupState.Failed
        };
        return new(state, deleted.ToImmutable(), retained, error);
    }
}
