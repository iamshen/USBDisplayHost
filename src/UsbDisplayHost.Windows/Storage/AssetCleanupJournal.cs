using System.Text.Json;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Windows.Storage;

internal sealed record AssetCleanupIntent(int SchemaVersion, string AssetId, string ErrorId);

// 共同存储边界内调用；删除前写入唯一意图，不覆盖未完成记录，也不恢复重放删除。
internal static class AssetCleanupJournal
{
    public static async Task<AssetCleanupRecovery?> ReadAsync(ApplicationDataPaths paths, CancellationToken token)
    {
        FileStream stream;
        try
        {
            var attributes = File.GetAttributes(paths.PendingAssetCleanup);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException("Asset cleanup journal is not an ordinary file.");
            }
            stream = new FileStream(paths.PendingAssetCleanup, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        await using (stream)
        {
            if (stream.Length is <= 0 or > 2048) { throw new InvalidDataException("Asset cleanup journal size is invalid."); }
            var intent = await JsonSerializer.DeserializeAsync<AssetCleanupIntent>(stream, AtomicJsonStorage.Options, token).ConfigureAwait(false);
            if (intent is null || intent.SchemaVersion != 1 || intent.AssetId is null || intent.AssetId.Length != 32
                || intent.AssetId.AsSpan().ContainsAnyExcept("0123456789abcdef")
                || !Guid.TryParseExact(intent.ErrorId, "N", out var errorId) || errorId == Guid.Empty)
            {
                throw new InvalidDataException("Asset cleanup journal format or version is unverified.");
            }
            return new(intent.AssetId, new(intent.ErrorId, "AssetCleanupUnconfirmed",
                "存在未确认的素材清理记录，后续清理已暂停", "请先核实删除结果；不会自动重发素材删除"));
        }
    }

    public static async Task CreateAsync(ApplicationDataPaths paths, AssetCleanupRecovery recovery, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new AssetCleanupIntent(1, recovery.AssetId, recovery.Error.ErrorId),
            AtomicJsonStorage.Options);
        await using var stream = new FileStream(paths.PendingAssetCleanup, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    public static void Complete(ApplicationDataPaths paths) =>
        AssetFileDeletion.DeleteVerified(paths.PendingAssetCleanup, static () => { });
}
