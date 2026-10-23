using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Updates;
using UsbDisplayHost.Windows.Storage;
using Velopack;
using Velopack.NuGet;
using Velopack.Sources;

namespace UsbDisplayHost.Windows.Updates;

public sealed class GitHubAppUpdateService : IAppUpdateService
{
    public const string RepositoryUrl = "https://github.com/iamshen/USBDisplayHost";
    public const string ReleasesUrl = RepositoryUrl + "/releases";
    private const string ProductId = "USBDisplayHost";
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly IDiagnosticService diagnostics;
    private readonly string stateDirectory;
    private readonly VerifiedUpdateManager manager;
    private readonly UpdateDownloader downloader;
    private UpdateInfo? target;
    private AppUpdateSnapshot snapshot;

    public GitHubAppUpdateService(ApplicationDataPaths paths, IDiagnosticService diagnostics)
    {
        this.diagnostics = diagnostics;
        stateDirectory = Path.Combine(paths.Root, "updates");
        downloader = new UpdateDownloader((received, total) =>
            Set(Snapshot with { DownloadedBytes = received, TotalBytes = total }));
        manager = new VerifiedUpdateManager(new GithubSource(RepositoryUrl, null, false, downloader));
        string version = manager.CurrentVersion?.ToString()
            ?? Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? "未知";
        bool available = manager.IsInstalled && !manager.IsPortable && manager.AppId == ProductId
            && RuntimeInformation.ProcessArchitecture == Architecture.X64;
        snapshot = new(available ? AppUpdateState.Idle : AppUpdateState.Unavailable, version, null, 0, null, null, null);
    }

    public AppUpdateSnapshot Snapshot => Volatile.Read(ref snapshot);
    public event Action<AppUpdateSnapshot>? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (Snapshot.State == AppUpdateState.Unavailable) { return; }
        await RunAsync("Initialize", async () =>
        {
            string attemptPath = Path.Combine(stateDirectory, "last-automatic-attempt.txt");
            if (File.Exists(attemptPath))
            {
                string text = await File.ReadAllTextAsync(attemptPath, cancellationToken).ConfigureAwait(false);
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var attempt))
                { Set(Snapshot with { LastAutomaticAttempt = attempt }); }
            }
            string pendingPath = Path.Combine(stateDirectory, "pending-package.json");
            if (!File.Exists(pendingPath)) { return; }
            var pending = JsonSerializer.Deserialize<PendingPackage>(
                await File.ReadAllTextAsync(pendingPath, cancellationToken).ConfigureAwait(false));
            if (pending is null) { throw new InvalidDataException("待安装包记录无效"); }
            var asset = pending.ToAsset();
            if (asset.Version <= manager.CurrentVersion) { File.Delete(pendingPath); return; }
            ValidateTarget(asset);
            Set(Snapshot with { State = AppUpdateState.Verifying, TargetVersion = asset.Version.ToString() });
            await manager.VerifyAsync(asset).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            target = new UpdateInfo(asset, false);
            Set(Snapshot with { State = AppUpdateState.ReadyToInstall, DownloadedBytes = asset.Size, TotalBytes = asset.Size });
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<OperationResult<Unit>> CheckAsync(bool isManual, CancellationToken cancellationToken) =>
        RunAsync("Check", async () =>
        {
            // 已下载目标保持固定；新发布不能替换用户准备安装的版本。
            if (Snapshot.State is AppUpdateState.ReadyToInstall or AppUpdateState.Installing) { return; }
            if (!isManual && Snapshot.LastAutomaticAttempt is { } previous
                && DateTimeOffset.UtcNow - previous < TimeSpan.FromHours(24)) { return; }
            if (!isManual)
            {
                var now = DateTimeOffset.UtcNow;
                Set(Snapshot with { LastAutomaticAttempt = now });
                Directory.CreateDirectory(stateDirectory);
                await File.WriteAllTextAsync(Path.Combine(stateDirectory, "last-automatic-attempt.txt"),
                    now.ToString("O", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            }
            target = null;
            Set(Snapshot with { State = AppUpdateState.Checking, TargetVersion = null, Error = null, DownloadedBytes = 0, TotalBytes = null });
            downloader.RequestToken = cancellationToken;
            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (update is null) { Set(Snapshot with { State = AppUpdateState.UpToDate }); return; }
            ValidateTarget(update.TargetFullRelease);
            target = update;
            Set(Snapshot with { State = AppUpdateState.Available, TargetVersion = update.TargetFullRelease.Version.ToString() });
        }, cancellationToken);

    public Task<OperationResult<Unit>> DownloadAsync(CancellationToken cancellationToken) =>
        RunAsync("Download", async () =>
        {
            if (target is null) { throw new InvalidOperationException("请先检查更新"); }
            ValidateTarget(target.TargetFullRelease);
            Set(Snapshot with { State = AppUpdateState.Downloading, Error = null, DownloadedBytes = 0, TotalBytes = null });
            downloader.RequestToken = cancellationToken;
            // 仅使用完整包，保证下载目标与字节进度一致。
            var full = new UpdateInfo(target.TargetFullRelease, false);
            await manager.RemoveInvalidCacheAsync(full.TargetFullRelease).ConfigureAwait(false);
            await manager.DownloadUpdatesAsync(full, cancelToken: cancellationToken).ConfigureAwait(false);
            Set(Snapshot with { State = AppUpdateState.Verifying });
            // SDK 对已存在的包直接返回；这里始终复核缓存，安装前也再次复核。
            await manager.VerifyAsync(full.TargetFullRelease).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(stateDirectory);
            string pendingPath = Path.Combine(stateDirectory, "pending-package.json");
            await File.WriteAllTextAsync(pendingPath + ".tmp",
                JsonSerializer.Serialize(PendingPackage.From(full.TargetFullRelease)), cancellationToken).ConfigureAwait(false);
            File.Move(pendingPath + ".tmp", pendingPath, true);
            Set(Snapshot with { State = AppUpdateState.ReadyToInstall, DownloadedBytes = full.TargetFullRelease.Size, TotalBytes = full.TargetFullRelease.Size });
        }, cancellationToken);

    public Task<OperationResult<Unit>> HandOffInstallationAsync(CancellationToken cancellationToken) =>
        RunAsync("Install", async () =>
        {
            if (Snapshot.State != AppUpdateState.ReadyToInstall || target is null)
            { throw new InvalidOperationException("更新包尚未完成校验"); }
            ValidateTarget(target.TargetFullRelease);
            await manager.VerifyAsync(target.TargetFullRelease).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            manager.WaitExitThenApplyUpdates(target.TargetFullRelease, silent: false, restart: true,
                restartArgs: ["--updated-to=" + target.TargetFullRelease.Version]);
            Set(Snapshot with { State = AppUpdateState.Installing, Error = null });
        }, cancellationToken);

    private void ValidateTarget(VelopackAsset asset)
    {
        string expectedName = $"{ProductId}-{asset.Version}-win-x64-full.nupkg";
        if (asset.PackageId != ProductId || asset.Type != VelopackAssetType.Full || asset.Version is null
            || asset.Version <= manager.CurrentVersion || asset.Version.IsPrerelease || asset.Size <= 0
            || asset.FileName != expectedName || asset.SHA256 is not { Length: 64 }
            || !asset.SHA256.All(Uri.IsHexDigit))
        { throw new InvalidDataException("更新包的产品、版本、文件名或校验信息不符合要求"); }
    }

    private async Task<OperationResult<Unit>> RunAsync(string stage, Func<Task> action, CancellationToken token)
    {
        if (Snapshot.State == AppUpdateState.Unavailable)
        { return new OperationResult<Unit>.Failure(AppError.Create("UpdateNotInstalled", "开发或便携构建不支持应用内更新", "请从 GitHub Releases 安装完整版本")); }
        if (!await operation.WaitAsync(0, token).ConfigureAwait(false)) { return new OperationResult<Unit>.Cancelled(); }
        var before = Snapshot;
        try
        {
            token.ThrowIfCancellationRequested();
            await action().ConfigureAwait(false);
            diagnostics.Write(DiagnosticSeverity.Information, "AppUpdate." + stage, Snapshot.State.ToString());
            return new OperationResult<Unit>.Success(new());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Set(Snapshot with { State = before.State is AppUpdateState.ReadyToInstall ? before.State
                : target is null ? AppUpdateState.Idle : AppUpdateState.Available, Error = null });
            return new OperationResult<Unit>.Cancelled();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = diagnostics.Report(exception, "AppUpdate." + stage);
            var error = new AppError(report.ErrorId, "Update" + stage + "Failed",
                stage == "Install" ? "更新安装交接失败" : stage == "Check" ? "检查更新失败" : "更新包读取、下载或校验失败",
                "请检查网络、磁盘空间和诊断记录后重试，或从 GitHub 下载完整安装包");
            target = null;
            Set(Snapshot with { State = AppUpdateState.Failed, Error = error });
            return new OperationResult<Unit>.Failure(error);
        }
        finally { operation.Release(); }
    }

    private void Set(AppUpdateSnapshot value)
    {
        Volatile.Write(ref snapshot, value);
        Changed?.Invoke(value);
    }

    private sealed class VerifiedUpdateManager(IUpdateSource source) : UpdateManager(source,
        new UpdateOptions { ExplicitChannel = "win-x64", AllowVersionDowngrade = false, MaximumDeltasBeforeFallback = -1 })
    {
        public async Task VerifyAsync(VelopackAsset asset)
        {
            await VerifyPackageChecksumAsync(asset).ConfigureAwait(false);
            using var archive = ZipFile.OpenRead(Path.Combine(Locator.PackagesDir!, asset.FileName));
            var manifest = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            using var stream = manifest.Open();
            var metadata = XDocument.Load(stream).Root?.Elements().Single(element => element.Name.LocalName == "metadata")
                ?? throw new InvalidDataException("更新包缺少元数据");
            string? Value(string name) => metadata.Elements().SingleOrDefault(element => element.Name.LocalName == name)?.Value;
            if (Value("id") != ProductId || Value("version") != asset.Version.ToString() || Value("channel") != "win-x64"
                || Value("rid") != "win-x64" || Value("os") != "win" || Value("machineArchitecture") != "x64")
            { throw new InvalidDataException("包内产品、版本、系统或架构与发布索引不一致"); }
            if (Value("osMinVersion") is { Length: > 0 } minimum
                && (!Version.TryParse(minimum, out var required) || Environment.OSVersion.Version < required))
            { throw new InvalidDataException("当前 Windows 版本不满足更新包要求"); }
        }

        public async Task RemoveInvalidCacheAsync(VelopackAsset asset)
        {
            string path = Path.Combine(Locator.PackagesDir!, asset.FileName);
            if (!File.Exists(path)) { return; }
            try { await VerifyAsync(asset).ConfigureAwait(false); }
            catch (Exception exception) when (exception is Velopack.Exceptions.ChecksumFailedException or InvalidDataException)
            { File.Delete(path); }
        }
    }

    private sealed record PendingPackage(string Version, string FileName, long Size, string SHA256, string SHA1)
    {
        public static PendingPackage From(VelopackAsset asset) => new(asset.Version.ToString(), asset.FileName, asset.Size, asset.SHA256, asset.SHA1);
        public VelopackAsset ToAsset() => new()
        {
            PackageId = ProductId, Version = SemanticVersion.Parse(Version), Type = VelopackAssetType.Full,
            FileName = FileName, Size = Size, SHA256 = SHA256, SHA1 = SHA1
        };
    }
}
