using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Windows.Storage;

// 由 FileProfileStore 的同一存储边界调用；不调用会产生损坏副本的配置恢复读取路径。
internal static class AssetReferenceScanner
{
    private const int MaximumFiles = 4096;
    private const int MaximumFileBytes = 256 * 1024;

    public static async Task<OperationResult<AssetReferenceInventory>> ScanAsync(ApplicationDataPaths paths, CancellationToken token)
    {
        var references = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        int files = 0;
        if (!IsExistingDirectory(paths.Root)) { return Success(references, files); }
        var root = await VerifyRootAsync(paths, token).ConfigureAwait(false);
        if (root is OperationResult<int>.Failure rootFailure) { return new OperationResult<AssetReferenceInventory>.Failure(rootFailure.Error); }
        files = ((OperationResult<int>.Success)root).Value;
        foreach (var directory in new[] { paths.Profiles, paths.Drafts, paths.DeviceSettingsDrafts })
        {
            token.ThrowIfCancellationRequested();
            if (!IsExistingDirectory(directory)) { continue; }
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (++files > MaximumFiles) { return Blocked("AssetInventoryLimit", "配置引用扫描超过文件数量上限"); }
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    return Blocked("AssetInventoryUnexpectedEntry", "配置目录存在未核实的目录或重解析项");
                }
                string primary = entry.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ? entry[..^4] : entry;
                if (!primary.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    return Blocked("AssetInventoryUnknownFile", "配置目录存在临时、损坏副本或未知文件，未判断素材可清理");
                }
                await using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length is <= 0 or > MaximumFileBytes) { return Blocked("AssetInventoryFileSize", "配置文件大小超出可核实范围"); }
                var envelope = await JsonSerializer.DeserializeAsync<VersionedDocument>(stream, AtomicJsonStorage.Options, token).ConfigureAwait(false);
                if (envelope is null || envelope.SchemaVersion is not (1 or 2) || envelope.Data.ValueKind != JsonValueKind.Object)
                {
                    return Blocked("AssetInventoryUnknownVersion", "配置格式或版本未核实，不能判断素材可清理");
                }
                var payload = envelope.Data;
                if (!payload.TryGetProperty("deviceKey", out var keyValue)
                    || keyValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    return Blocked("AssetInventoryInvalidIdentity", "配置设备归属未核实");
                }
                DeviceKey? key = null;
                if (keyValue.ValueKind == JsonValueKind.String)
                {
                    string text = keyValue.GetString()!;
                    if (text.Length < 6 || text[4] != ':'
                        || !ushort.TryParse(text.AsSpan(0, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort vendor)
                        || DeviceKey.FromSerialNumber(vendor, text[5..]) is not OperationResult<DeviceKey>.Success parsed
                        || parsed.Value.Value != text)
                    {
                        return Blocked("AssetInventoryInvalidIdentity", "配置设备键格式未核实");
                    }
                    key = parsed.Value;
                }
                ThemeContent? content = null;
                string expected;
                if (directory == paths.Profiles)
                {
                    if (key is null || ConfigurationCodec.DecodeProfile(payload, key) is not OperationResult<DeviceProfile>.Success profile)
                    {
                        return Blocked("AssetInventoryInvalidProfile", "恢复配置字段未完整核实");
                    }
                    expected = paths.Profile(key);
                    content = profile.Value.Content;
                }
                else if (directory == paths.Drafts)
                {
                    if (ConfigurationCodec.DecodeDraft(payload, key) is not OperationResult<ThemeDraft>.Success draft)
                    {
                        return Blocked("AssetInventoryInvalidDraft", "主题草稿字段未完整核实");
                    }
                    expected = paths.Draft(key);
                    content = draft.Value.Content;
                }
                else
                {
                    if (ConfigurationCodec.DecodeDeviceSettingsDraft(payload, key) is not OperationResult<DeviceSettingsDraft>.Success)
                    {
                        return Blocked("AssetInventoryInvalidSettingsDraft", "设备设置草稿字段未完整核实");
                    }
                    expected = paths.DeviceSettingsDraft(key);
                }
                if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(primary), expected))
                {
                    return Blocked("AssetInventoryOwnershipMismatch", "配置文件名与设备归属不匹配");
                }
                if (content is ThemeContent.Image { AssetId: not null } image) { references.Add(image.AssetId.ToLowerInvariant()); }
            }
        }
        return Success(references, files);
    }

    private static async Task<OperationResult<int>> VerifyRootAsync(ApplicationDataPaths paths, CancellationToken token)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            paths.Profiles, paths.Drafts, paths.DeviceSettingsDrafts, paths.Assets, Path.Combine(paths.Root, "diagnostics")
        };
        int settingsFiles = 0;
        int entries = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(paths.Root))
        {
            token.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            if (++entries > 8 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                return RootBlocked("AssetInventoryUnknownRoot", "数据目录存在未核实的顶层条目或重解析项");
            }
            string full = Path.GetFullPath(entry);
            if (StringComparer.OrdinalIgnoreCase.Equals(full, paths.PendingAssetCleanup))
            {
                return RootBlocked("AssetCleanupRecoveryPending", "存在待核实清理记录，未判断素材可再次清理");
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (!directories.Contains(full)) { return RootBlocked("AssetInventoryUnknownDirectory", "数据目录存在未知配置目录"); }
                continue;
            }
            if (!StringComparer.OrdinalIgnoreCase.Equals(full, paths.Settings)
                && !StringComparer.OrdinalIgnoreCase.Equals(full, paths.Settings + ".bak"))
            {
                return RootBlocked("AssetInventoryUnknownRootFile", "数据目录存在未知、临时或损坏配置副本");
            }
            await using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumFileBytes)
            {
                return RootBlocked("AssetInventorySettingsSize", "应用设置大小超出可核实范围");
            }
            var envelope = await JsonSerializer.DeserializeAsync<VersionedDocument>(stream, AtomicJsonStorage.Options, token).ConfigureAwait(false);
            if (envelope is null || envelope.SchemaVersion is not (1 or 2) || envelope.Data.ValueKind != JsonValueKind.Object
                || ConfigurationCodec.DecodeApplicationSettings(envelope.Data) is not OperationResult<ApplicationSettings>.Success)
            {
                return RootBlocked("AssetInventorySettingsUnverified", "应用设置格式或版本未核实，不能推断没有素材引用");
            }
            settingsFiles++;
        }
        return new OperationResult<int>.Success(settingsFiles);
    }

    private static OperationResult<int> RootBlocked(string code, string reason) =>
        new OperationResult<int>.Failure(AppError.Create(code, reason, "请先核实数据目录与应用设置；未删除素材"));

    private static bool IsExistingDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Configuration inventory directory is not a verified ordinary directory.");
            }
            return true;
        }
        catch (DirectoryNotFoundException) { return false; }
        catch (FileNotFoundException) { return false; }
    }

    private static OperationResult<AssetReferenceInventory> Success(ImmutableHashSet<string>.Builder references, int files) =>
        new OperationResult<AssetReferenceInventory>.Success(new(references.ToImmutable(), files));

    private static OperationResult<AssetReferenceInventory> Blocked(string code, string reason) =>
        new OperationResult<AssetReferenceInventory>.Failure(AppError.Create(code, reason, "请先核实配置与备份；未删除素材"));
}
