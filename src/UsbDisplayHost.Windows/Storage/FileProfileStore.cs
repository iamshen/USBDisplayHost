using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Windows.Storage;

public sealed class FileProfileStore : IProfileStore, IApplicationSettingsStore, IDeviceSettingsDraftStore, IAssetReferenceInventoryStore
{
    private readonly ApplicationDataPaths paths;
    private readonly AtomicJsonStorage storage;
    private readonly StorageBoundary boundary;
    private readonly AssetReferenceRegistry references;

    public FileProfileStore(ApplicationDataPaths paths, StorageCoordinator coordinator)
    {
        coordinator.VerifyPaths(paths);
        this.paths = paths;
        storage = new AtomicJsonStorage(coordinator.Reporter);
        boundary = coordinator.Boundary;
        references = coordinator.References;
    }

    public Task<OperationResult<AssetReferenceInventory>> ScanAssetReferencesAsync(CancellationToken cancellationToken) =>
        boundary.RunAsync(() => AssetReferenceScanner.ScanAsync(paths, cancellationToken), "AssetReferenceInventory", cancellationToken);

    public Task<OperationResult<ProfileReadResult>> LoadAsync(DeviceKey deviceKey, CancellationToken cancellationToken) =>
        boundary.RunAsync<ProfileReadResult>(async () =>
        {
            var result = await storage.ReadAsync(paths.Profile(deviceKey),
                payload => ConfigurationCodec.DecodeProfile(payload, deviceKey), cancellationToken).ConfigureAwait(false);
            var retained = RetainReadAsset(result.Value?.Content);
            if (retained is OperationResult<AssetReferenceLease?>.Failure failure) { return new OperationResult<ProfileReadResult>.Failure(failure.Error); }
            return new OperationResult<ProfileReadResult>.Success(new ProfileReadResult(
                result.Value, result.Recovery, result.Error, ((OperationResult<AssetReferenceLease?>.Success)retained).Value));
        }, "ProfileLoad", cancellationToken);

    public Task<OperationResult<Unit>> SaveAsync(DeviceProfile profile, CancellationToken cancellationToken) =>
        boundary.RunAsync(async () =>
        {
            var validation = ConfigurationCodec.Validate(profile);
            if (validation is not OperationResult<Unit>.Success) { return validation; }
            var asset = OpenAssetForSave(profile.Content);
            if (asset is OperationResult<FileStream?>.Failure missing) { return new OperationResult<Unit>.Failure(missing.Error); }
            using var retainedAsset = ((OperationResult<FileStream?>.Success)asset).Value;
            return await storage.WriteAsync(paths.Profile(profile.DeviceKey), ConfigurationCodec.Encode(profile),
                payload => ConfigurationCodec.DecodeProfile(payload, profile.DeviceKey), cancellationToken).ConfigureAwait(false);
        }, "ProfileSave", cancellationToken);

    public async Task<OperationResult<ProfileApplicationSaveAttempt>> SaveApplicationAsync(DeviceProfile candidate,
        DeviceProfile? previousRestore, CancellationToken cancellationToken)
    {
        DeviceProfile? attempted = null;
        var outcome = await boundary.RunAsync<ProfileApplicationSaveAttempt>(async () =>
        {
            var validation = ConfigurationCodec.Validate(candidate);
            if (validation is not OperationResult<Unit>.Success)
            {
                return new OperationResult<ProfileApplicationSaveAttempt>.Failure(
                    validation is OperationResult<Unit>.Failure failure ? failure.Error : AppError.Create(
                        "InvalidApplicationProfile", "应用恢复配置无效", "请核实当前应用候选"));
            }
            if (previousRestore is not null && previousRestore.DeviceKey != candidate.DeviceKey)
            { return ApplicationSaveRejected("ApplicationRestoreKeyMismatch", "原恢复配置不属于应用设备"); }
            var read = await storage.ReadAsync(paths.Profile(candidate.DeviceKey),
                payload => ConfigurationCodec.DecodeProfile(payload, candidate.DeviceKey), cancellationToken).ConfigureAwait(false);
            if (read.Recovery is not (ProfileRecovery.Primary or ProfileRecovery.Missing)
                || read.Value is null && previousRestore is not null)
            { return ApplicationSaveRejected("ApplicationRestoreUnverified", "当前主恢复配置未确认，未覆盖主题或控制意图"); }
            var latest = read.Value;
            if (latest is not null && !SameApplicationFields(latest, previousRestore) && !SameApplicationFields(latest, candidate))
            { return ApplicationSaveRejected("ApplicationRestoreChanged", "恢复主题或设置已改变，未覆盖为旧候选"); }
            var merged = latest is null ? candidate : candidate with
            {
                ConfirmedBrightness = latest.ConfirmedBrightness != previousRestore?.ConfirmedBrightness
                    ? latest.ConfirmedBrightness : candidate.ConfirmedBrightness,
                PowerIntent = latest.PowerIntent != previousRestore?.PowerIntent ? latest.PowerIntent : candidate.PowerIntent
            };
            var asset = OpenAssetForSave(merged.Content);
            if (asset is OperationResult<FileStream?>.Failure missing)
            { return new OperationResult<ProfileApplicationSaveAttempt>.Failure(missing.Error); }
            using var retainedAsset = ((OperationResult<FileStream?>.Success)asset).Value;
            var encoded = ConfigurationCodec.Encode(merged);
            cancellationToken.ThrowIfCancellationRequested();
            attempted = merged;
            var written = await storage.WriteAsync(paths.Profile(merged.DeviceKey), encoded,
                payload => ConfigurationCodec.DecodeProfile(payload, merged.DeviceKey), cancellationToken).ConfigureAwait(false);
            return new OperationResult<ProfileApplicationSaveAttempt>.Success(new(merged, written));
        }, "ProfileApplicationSave", cancellationToken).ConfigureAwait(false);
        if (outcome is not OperationResult<ProfileApplicationSaveAttempt>.Success && attempted is not null)
        {
            var error = outcome switch
            {
                OperationResult<ProfileApplicationSaveAttempt>.Failure failure => failure.Error,
                OperationResult<ProfileApplicationSaveAttempt>.Unknown unknown => unknown.Error,
                _ => AppError.Create("ApplicationSaveWriteUnconfirmed", "应用恢复配置写入未确认", "请核实实际主文件，不直接重写")
            };
            return new OperationResult<ProfileApplicationSaveAttempt>.Success(new(attempted, new OperationResult<Unit>.Unknown(error)));
        }
        return outcome;
    }

    public async Task<OperationResult<ProfileApplicationSaveAttempt>> SaveControlAsync(DeviceProfile candidate, CancellationToken token)
    {
        DeviceProfile? attempted = null;
        var result = await boundary.RunAsync(async () =>
        {
            if (ConfigurationCodec.Validate(candidate) is OperationResult<Unit>.Failure invalid)
            { return new OperationResult<ProfileApplicationSaveAttempt>.Failure(invalid.Error); }
            var read = await storage.ReadAsync(paths.Profile(candidate.DeviceKey),
                payload => ConfigurationCodec.DecodeProfile(payload, candidate.DeviceKey), token).ConfigureAwait(false);
            if (read.Recovery is not (ProfileRecovery.Primary or ProfileRecovery.Missing))
            { return ApplicationSaveRejected("ControlRestoreUnverified", "主恢复配置未确认，未覆盖控制意图"); }
            var merged = read.Value is { } latest ? latest with
            { ConfirmedBrightness = candidate.ConfirmedBrightness, PowerIntent = candidate.PowerIntent } : candidate;
            var asset = OpenAssetForSave(merged.Content);
            if (asset is OperationResult<FileStream?>.Failure missing)
            { return new OperationResult<ProfileApplicationSaveAttempt>.Failure(missing.Error); }
            using var retainedAsset = ((OperationResult<FileStream?>.Success)asset).Value;
            attempted = merged;
            var write = await storage.WriteAsync(paths.Profile(merged.DeviceKey), ConfigurationCodec.Encode(merged),
                payload => ConfigurationCodec.DecodeProfile(payload, merged.DeviceKey), token).ConfigureAwait(false);
            return new OperationResult<ProfileApplicationSaveAttempt>.Success(new(merged, write));
        }, "ProfileControlSave", token).ConfigureAwait(false);
        if (result is not OperationResult<ProfileApplicationSaveAttempt>.Success && attempted is not null)
        { return new OperationResult<ProfileApplicationSaveAttempt>.Success(new(attempted,
            new OperationResult<Unit>.Unknown(AppError.Create("ControlSaveUnconfirmed", "控制设置落盘结果待确认", "核实主文件，不重复设备命令")))); }
        return result;
    }

    private static bool SameApplicationFields(DeviceProfile value, DeviceProfile? other) => other is not null
        && value.DeviceKey == other.DeviceKey && value.Content == other.Content && value.Settings == other.Settings;
    private static OperationResult<ProfileApplicationSaveAttempt> ApplicationSaveRejected(string code, string reason) =>
        new OperationResult<ProfileApplicationSaveAttempt>.Failure(AppError.Create(code, reason, "请保留已发送结果并核实恢复配置"));

    public Task<OperationResult<Unit>> SaveDraftAsync(ThemeDraft draft, CancellationToken cancellationToken) =>
        boundary.RunAsync(async () =>
        {
            if (draft.Revision < 0)
            {
                return new OperationResult<Unit>.Failure(AppError.Create(
                    "InvalidDraftRevision", "草稿修订号无效", "请重新打开主题编辑页"));
            }
            var validation = ThemeValidation.Validate(draft.Content);
            if (validation is not OperationResult<Unit>.Success) { return validation; }
            var asset = OpenAssetForSave(draft.Content);
            if (asset is OperationResult<FileStream?>.Failure missing) { return new OperationResult<Unit>.Failure(missing.Error); }
            using var retainedAsset = ((OperationResult<FileStream?>.Success)asset).Value;
            return await storage.WriteAsync(paths.Draft(draft.DeviceKey), ConfigurationCodec.Encode(draft),
                payload => ConfigurationCodec.DecodeDraft(payload, draft.DeviceKey), cancellationToken).ConfigureAwait(false);
        }, "DraftSave", cancellationToken);

    public Task<OperationResult<DraftReadResult>> LoadDraftAsync(DeviceKey? deviceKey, CancellationToken cancellationToken) =>
        boundary.RunAsync<DraftReadResult>(async () =>
        {
            var result = await storage.ReadAsync(paths.Draft(deviceKey),
                payload => ConfigurationCodec.DecodeDraft(payload, deviceKey), cancellationToken).ConfigureAwait(false);
            var retained = RetainReadAsset(result.Value?.Content);
            if (retained is OperationResult<AssetReferenceLease?>.Failure failure) { return new OperationResult<DraftReadResult>.Failure(failure.Error); }
            // 恢复结果与可用草稿同时返回；损坏或高版本不能悄悄清空编辑。
            return new OperationResult<DraftReadResult>.Success(new DraftReadResult(
                result.Value, result.Recovery, result.Error, ((OperationResult<AssetReferenceLease?>.Success)retained).Value));
        }, "DraftLoad", cancellationToken);

    public Task<OperationResult<ApplicationSettingsReadResult>> LoadSettingsAsync(CancellationToken cancellationToken) =>
        boundary.RunAsync<ApplicationSettingsReadResult>(async () =>
        {
            var result = await storage.ReadAsync(paths.Settings,
                ConfigurationCodec.DecodeApplicationSettings, cancellationToken).ConfigureAwait(false);
            return new OperationResult<ApplicationSettingsReadResult>.Success(new ApplicationSettingsReadResult(
                result.Value ?? ApplicationSettings.Default, result.Recovery, result.Error));
        }, "ApplicationSettingsLoad", cancellationToken);

    public Task<OperationResult<Unit>> SaveSettingsAsync(ApplicationSettings settings, CancellationToken cancellationToken) =>
        boundary.RunAsync(() => storage.WriteAsync(paths.Settings, settings,
            ConfigurationCodec.DecodeApplicationSettings, cancellationToken), "ApplicationSettingsSave", cancellationToken);

    public Task<OperationResult<DeviceSettingsDraftReadResult>> LoadDeviceSettingsDraftAsync(DeviceKey? deviceKey, CancellationToken cancellationToken) =>
        boundary.RunAsync<DeviceSettingsDraftReadResult>(async () =>
        {
            var result = await storage.ReadAsync(paths.DeviceSettingsDraft(deviceKey),
                payload => ConfigurationCodec.DecodeDeviceSettingsDraft(payload, deviceKey), cancellationToken).ConfigureAwait(false);
            return new OperationResult<DeviceSettingsDraftReadResult>.Success(new(result.Value, result.Recovery, result.Error));
        }, "DeviceSettingsDraftLoad", cancellationToken);

    public Task<OperationResult<Unit>> SaveDeviceSettingsDraftAsync(DeviceSettingsDraft draft, CancellationToken cancellationToken) =>
        boundary.RunAsync(() =>
        {
            if (draft.Revision < 0 || draft.Settings is null)
            {
                return Task.FromResult<OperationResult<Unit>>(new OperationResult<Unit>.Failure(AppError.Create(
                    "InvalidSettingsDraft", "设置草稿字段无效", "请重新打开设备设置页")));
            }
            return storage.WriteAsync(paths.DeviceSettingsDraft(draft.DeviceKey), ConfigurationCodec.Encode(draft),
                payload => ConfigurationCodec.DecodeDeviceSettingsDraft(payload, draft.DeviceKey), cancellationToken);
        }, "DeviceSettingsDraftSave", cancellationToken);

    // 在共同存储边界内核实素材并持有只读句柄到写入收敛，不保存已清理的素材引用。
    private OperationResult<FileStream?> OpenAssetForSave(ThemeContent content)
    {
        if (content is not ThemeContent.Image { AssetId: not null } image)
        {
            return new OperationResult<FileStream?>.Success(null);
        }
        try
        {
            string path = Path.Combine(paths.Assets, image.AssetId.ToLowerInvariant() + ".image");
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return InvalidSaveAsset();
            }
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (stream.Length is <= 0 or > 20 * 1024 * 1024)
                {
                    stream.Dispose();
                    return InvalidSaveAsset();
                }
                return new OperationResult<FileStream?>.Success(stream);
            }
            catch { stream.Dispose(); throw; }
        }
        catch (FileNotFoundException) { return InvalidSaveAsset(); }
        catch (DirectoryNotFoundException) { return InvalidSaveAsset(); }
    }

    private static OperationResult<FileStream?> InvalidSaveAsset() => new OperationResult<FileStream?>.Failure(
        AppError.Create("ConfigurationAssetUnavailable", "图片素材缺失或文件未核实，未开始保存配置",
            "请重新导入图片后保存；原配置与备份保持可恢复"));

    private OperationResult<AssetReferenceLease?> RetainReadAsset(ThemeContent? content)
    {
        if (content is not ThemeContent.Image { AssetId: not null } image)
        {
            return new OperationResult<AssetReferenceLease?>.Success(null);
        }
        return references.Acquire(image.AssetId, AssetReferenceOwner.ConfigurationRead) switch
        {
            OperationResult<AssetReferenceLease>.Success acquired => new OperationResult<AssetReferenceLease?>.Success(acquired.Value),
            OperationResult<AssetReferenceLease>.Failure failure => new OperationResult<AssetReferenceLease?>.Failure(failure.Error),
            _ => new OperationResult<AssetReferenceLease?>.Failure(AppError.Create("ConfigurationReferenceUnconfirmed",
                "读取配置的素材引用未确认", "请等待素材操作收敛后重新读取"))
        };
    }
}
