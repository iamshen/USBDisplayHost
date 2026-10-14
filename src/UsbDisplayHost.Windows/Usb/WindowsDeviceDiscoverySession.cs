using System.Collections.Immutable;
using System.Threading.Channels;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Windows.Usb;

public sealed record DeviceDiscoverySnapshot(long Version, ImmutableArray<DeviceIdentity> Interfaces,
    bool IsScanning, bool CanSelect, AppError? Error);

// 成功创建后拥有发现服务；通知只合并重扫，不连接设备，不等待 UI。
public sealed class WindowsDeviceDiscoverySession : IAsyncDisposable
{
    private static readonly object OwnersSync = new();
    private static readonly HashSet<WindowsDeviceDiscoverySession> Owners = [];
    private readonly object sync = new();
    private readonly WindowsDeviceDiscovery discovery;
    private readonly IErrorReporter reporter;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource<bool> subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<Unit> rescans = Channel.CreateBounded<Unit>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Channel<DeviceDiscoverySnapshot> snapshots = Channel.CreateBounded<DeviceDiscoverySnapshot>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Task watching, scanning;
    private DeviceDiscoverySnapshot current = new(0, [], false, false, null);
    private long requested;
    private long verifiedEpoch = -1;
    private long consumedEpoch = -1;
    private bool stopping, watchFailed;
    private readonly Action<DeviceChange> removalHandler;
    private Task<OperationResult<Unit>>? closing;
    private readonly Dictionary<string, long> removedPaths = new(StringComparer.OrdinalIgnoreCase);
    private long removalRevision, allRemovedRevision;

    private WindowsDeviceDiscoverySession(WindowsDeviceDiscovery discovery, IErrorReporter reporter, Action<DeviceChange> removalHandler)
    {
        this.discovery = discovery; this.reporter = reporter; this.removalHandler = removalHandler;
        snapshots.Writer.TryWrite(current);
        watching = Task.Run(WatchAsync);
        scanning = Task.Run(ScanAsync);
    }

    public static OperationResult<WindowsDeviceDiscoverySession> Create(WindowsDeviceDiscovery discovery, IErrorReporter reporter,
        Action<DeviceChange> removalHandler)
    {
        if (discovery is null || reporter is null || removalHandler is null)
        { return new OperationResult<WindowsDeviceDiscoverySession>.Failure(AppError.Create("DiscoverySessionMissingDependency",
            "发现生命周期缺少依赖", "请先创建发现服务及诊断服务")); }
        lock (OwnersSync)
        {
            if (Owners.Count >= 8 || Owners.Any(owner => ReferenceEquals(owner.discovery, discovery)))
            { return new OperationResult<WindowsDeviceDiscoverySession>.Failure(AppError.Create("DiscoverySessionAlreadyOwned",
                "发现服务已有生命周期所有者或达到限额", "请确认原所有者关闭；创建失败时发现服务仍由调用方持有")); }
            var owner = new WindowsDeviceDiscoverySession(discovery, reporter, removalHandler);
            Owners.Add(owner);
            return new OperationResult<WindowsDeviceDiscoverySession>.Success(owner);
        }
    }

    public DeviceDiscoverySnapshot Current { get { lock (sync) { return current; } } }
    public ChannelReader<DeviceDiscoverySnapshot> Reader => snapshots.Reader;

    public bool RequestRescan()
    {
        lock (sync)
        {
            if (stopping || watchFailed || requested == long.MaxValue || current.Version == long.MaxValue) { return false; }
            requested++;
            PublishLocked([], true, false, null);
            rescans.Writer.TryWrite(new());
            return true;
        }
    }

    public bool IsSelectionCurrent(long expectedVersion)
    {
        lock (sync) { return !stopping && !watchFailed && current.CanSelect && current.Version == expectedVersion
            && verifiedEpoch == discovery.ChangeEpoch && consumedEpoch == verifiedEpoch; }
    }

    public OperationResult<DeviceSelectionAdmission> Select(long expectedVersion, DeviceKey key)
    {
        lock (sync)
        {
            if (!IsSelectionCurrent(expectedVersion))
            { return new OperationResult<DeviceSelectionAdmission>.Failure(AppError.Create("DiscoverySelectionStale",
                "发现列表尚未核实或已经变化", "请等待本次重扫完成，再明确选择设备")); }
            var resolved = DeviceSelectionResolver.Resolve(current.Interfaces, key);
            return resolved is OperationResult<DeviceSelection>.Success selected
                ? new OperationResult<DeviceSelectionAdmission>.Success(new(selected.Value, new SelectionValidity(this, selected.Value, expectedVersion)))
                : new OperationResult<DeviceSelectionAdmission>.Failure(((OperationResult<DeviceSelection>.Failure)resolved).Error);
        }
    }
    private sealed class SelectionValidity(WindowsDeviceDiscoverySession owner, DeviceSelection selection, long version) : IDeviceSelectionValidity
    {
        private readonly long admittedRemoval = owner.removalRevision;
        public bool IsCurrent(DeviceSelection candidate)
        {
            lock (owner.sync)
            {
                return ReferenceEquals(candidate, selection) && version >= 0 && !owner.stopping && !owner.watchFailed
                    && owner.allRemovedRevision <= admittedRemoval
                    && !selection.Interfaces.Any(identity => owner.removedPaths.TryGetValue(identity.InterfacePath, out var removed)
                        && removed > admittedRemoval);
            }
        }
    }

    private async Task WatchAsync()
    {
        try
        {
            // Windows 监视器注册后提供初始重扫通知；收到它才允许初次枚举。
            await foreach (var change in discovery.WatchAsync(lifetime.Token).ConfigureAwait(false))
            {
                if (change.Kind == DeviceChangeKind.Removed)
                {
                    lock (sync)
                    {
                        removalRevision++;
                        if (change.InterfacePath is null || removedPaths.Count >= 256)
                        { allRemovedRevision = removalRevision; removedPaths.Clear(); }
                        if (change.InterfacePath is { } removedPath) { removedPaths[removedPath] = removalRevision; }
                    }
                    WinUsbPathOwnership.RevokeRemovedPath(change.InterfacePath);
                    bool stopped;
                    lock (sync) { stopped = stopping; }
                    if (!stopped) { removalHandler(change); }
                }
                if (change.NotificationEpoch is { } epoch)
                {
                    lock (sync) { consumedEpoch = Math.Max(consumedEpoch, epoch); }
                    subscribed.TrySetResult(true);
                }
                RequestRescan();
            }
            if (!lifetime.IsCancellationRequested)
            { FailSubscription(AppError.Create("DiscoveryWatchEnded", "设备变化订阅提前结束", "请关闭发现服务并检查诊断后重新创建")); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { FailSubscription(Report(exception, "DiscoveryWatch", "设备变化订阅失败")); }
        finally { subscribed.TrySetResult(false); }
    }

    private void FailSubscription(AppError error)
    {
        lock (sync)
        {
            if (stopping) { return; }
            watchFailed = true;
            PublishLocked([], false, false, error);
            rescans.Writer.TryComplete();
        }
        subscribed.TrySetResult(false);
    }

    private async Task ScanAsync()
    {
        try
        {
            if (!await subscribed.Task.ConfigureAwait(false)) { return; }
            long completedRequest = 0;
            await foreach (var signal in rescans.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                long accepted;
                lock (sync)
                {
                    if (stopping || watchFailed) { return; }
                    accepted = requested;
                    if (accepted == completedRequest) { continue; }
                }
                long startedEpoch = discovery.ChangeEpoch;
                var result = await discovery.EnumerateAsync(lifetime.Token).ConfigureAwait(false);
                lock (sync)
                {
                    if (stopping || watchFailed || accepted != requested) { continue; }
                    completedRequest = accepted;
                    switch (result)
                    {
                        case OperationResult<ImmutableArray<DeviceIdentity>>.Success success:
                            if (startedEpoch != discovery.ChangeEpoch || consumedEpoch != startedEpoch)
                            {
                                PublishLocked([], false, false, AppError.Create("DiscoveryEpochChanged",
                                    "发现结果提交前接口通知代次已变化", "请等待重扫完成后重新选择"));
                            }
                            else { verifiedEpoch = startedEpoch; PublishLocked(success.Value, false, true, null); }
                            break;
                        case OperationResult<ImmutableArray<DeviceIdentity>>.Failure failure:
                            PublishLocked([], false, false, failure.Error); break;
                        case OperationResult<ImmutableArray<DeviceIdentity>>.Unknown unknown:
                            watchFailed = true;
                            PublishLocked([], false, false, unknown.Error);
                            rescans.Writer.TryComplete(); break;
                        default:
                            PublishLocked([], false, false, AppError.Create("DiscoveryScanCancelled",
                                "设备发现已取消，旧列表不能用于选择", "请重新检测设备")); break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { FailSubscription(Report(exception, "DiscoveryScan", "设备发现循环未完成")); }
    }

    private void PublishLocked(ImmutableArray<DeviceIdentity> interfaces, bool isScanning, bool canSelect, AppError? error)
    {
        if (current.Version == long.MaxValue) { watchFailed = true; return; }
        if (current.Version == long.MaxValue - 1)
        {
            watchFailed = true;
            current = new(long.MaxValue, [], false, false, AppError.Create("DiscoveryVersionExhausted",
                "发现快照版本已耗尽，已停止选择", "请确认当前发现服务关闭后重新创建"));
            snapshots.Writer.TryWrite(current);
            return;
        }
        current = new(current.Version + 1, interfaces, isScanning, canSelect, error);
        snapshots.Writer.TryWrite(current);
    }

    public Task<OperationResult<Unit>> CloseAsync()
    {
        lock (sync)
        {
            if (closing is not null) { return closing; }
            stopping = true;
            PublishLocked([], false, false, null);
            return closing = CloseCoreAsync();
        }
    }

    private async Task<OperationResult<Unit>> CloseCoreAsync()
    {
        try
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            rescans.Writer.TryComplete();
            subscribed.TrySetResult(false);
            await Task.WhenAll(watching, scanning).ConfigureAwait(false);
            var closed = await discovery.CloseAsync().ConfigureAwait(false);
            if (closed is not OperationResult<Unit>.Success)
            {
                var error = closed switch
                {
                    OperationResult<Unit>.Failure failure => failure.Error,
                    OperationResult<Unit>.Unknown unknown => unknown.Error,
                    _ => AppError.Create("DiscoverySessionCloseUnconfirmed", "取消未证明发现服务已关闭", "请保留原所有者")
                };
                lock (sync) { PublishLocked([], false, false, error); }
                return new OperationResult<Unit>.Unknown(error);
            }
            snapshots.Writer.TryComplete();
            lock (OwnersSync) { Owners.Remove(this); }
            return new OperationResult<Unit>.Success(new());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var error = Report(exception, "DiscoverySessionClose", "发现生命周期关闭未确认，所有者已保留");
            lock (sync) { PublishLocked([], false, false, error); }
            return new OperationResult<Unit>.Unknown(error);
        }
    }

    private AppError Report(Exception exception, string operation, string reason)
    {
        var report = reporter.Report(exception, operation);
        return new(report.ErrorId, operation, reason, "请保留诊断，确认原发现服务关闭后再创建");
    }
    public async ValueTask DisposeAsync()
    {
        if (await CloseAsync().ConfigureAwait(false) is not OperationResult<Unit>.Success)
        { throw new InvalidOperationException("Discovery session close is unconfirmed; owner retained."); }
    }
}
