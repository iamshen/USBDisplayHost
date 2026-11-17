using System.Threading.Channels;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Themes;
using UsbDisplayHost.Core.Telemetry;

namespace UsbDisplayHost.Desktop.Services;

public sealed record PreviewUpdate(DeviceKey? DeviceKey, long Revision, object? Image, AppError? Error);

public sealed class DraftPreviewController : IAsyncDisposable
{
    private readonly object syncRoot = new();
    private readonly Channel<byte> signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false
    });
    private readonly CancellationTokenSource lifetime = new();
    private readonly IFrameRenderer renderer;
    private readonly IUiDispatcher dispatcher;
    private readonly IFramePreviewDecoder decoder;
    private readonly TimeProvider timeProvider;
    private readonly IErrorReporter errorReporter;
    private readonly MetricSamplingService metrics;
    private readonly IAssetStore assets;
    private AssetReferenceLease? desiredAsset;
    private readonly Task processing;
    private readonly Task clock;
    private ThemeDraft? desired;
    private PreviewUpdate? latest;
    private CancellationTokenSource? rendering;
    private bool visible;
    private bool notificationQueued;
    private bool disposed;
    private long? failedRevision;
    private long frameNumber;
    private int resourcesDisposed;

    public DraftPreviewController(IFrameRenderer renderer, IUiDispatcher dispatcher, IFramePreviewDecoder decoder,
        TimeProvider timeProvider, IErrorReporter errorReporter, MetricSamplingService metrics, IAssetStore assets)
    {
        this.renderer = renderer;
        this.dispatcher = dispatcher;
        this.decoder = decoder;
        this.timeProvider = timeProvider;
        this.errorReporter = errorReporter;
        this.metrics = metrics;
        this.assets = assets;
        processing = Task.Run(ProcessAsync);
        clock = Task.Run(UpdateClockAsync);
    }

    public event Action<PreviewUpdate>? Updated;

    public void Request(ThemeDraft draft, bool force = false)
    {
        lock (syncRoot)
        {
            if (disposed) { return; }
            var referenceError = AcquireReference(draft, out var nextAsset);
            var previousAsset = desiredAsset;
            desiredAsset = nextAsset;
            previousAsset?.Dispose();
            if (desired?.Revision != draft.Revision || desired?.DeviceKey != draft.DeviceKey)
            {
                rendering?.Cancel();
                failedRevision = null;
                latest = null;
            }
            desired = draft;
            if (referenceError is not null)
            {
                rendering?.Cancel();
                failedRevision = draft.Revision;
                Publish(draft, new(draft.DeviceKey, draft.Revision, null, referenceError));
                return;
            }
            if (force) { failedRevision = null; }
            if (visible && failedRevision != draft.Revision) { signals.Writer.TryWrite(0); }
        }
    }

    public void SetVisible(bool value)
    {
        lock (syncRoot)
        {
            if (disposed) { return; }
            visible = value;
            if (!visible) { rendering?.Cancel(); latest = null; }
            else if (desired is not null && failedRevision != desired.Revision) { signals.Writer.TryWrite(0); }
        }
    }

    public void Invalidate()
    {
        lock (syncRoot)
        {
            desired = null;
            desiredAsset?.Dispose();
            desiredAsset = null;
            latest = null;
            rendering?.Cancel();
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (byte signal in signals.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                ThemeDraft draft;
                AssetReferenceLease? renderAsset;
                CancellationTokenSource cancellation;
                lock (syncRoot)
                {
                    if (!visible || desired is null || failedRevision == desired.Revision) { continue; }
                    draft = desired;
                    var referenceError = AcquireReference(draft, out renderAsset);
                    if (referenceError is not null)
                    {
                        failedRevision = draft.Revision;
                        Publish(draft, new(draft.DeviceKey, draft.Revision, null, referenceError));
                        continue;
                    }
                    rendering = cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                }
                using (cancellation)
                using (renderAsset)
                {
                    try
                    {
                        var request = new RenderRequest(draft.Content, DeviceSettings.Default,
                            1920, 1080, 1_048_576, new FrameContext(draft.DeviceKey, 0, Guid.NewGuid(), draft.Revision),
                            ++frameNumber, FramePurpose.Draft, timeProvider.GetLocalNow(), metrics.GetSnapshot());
                        var result = await renderer.RenderAsync(request, cancellation.Token).ConfigureAwait(false);
                        if (result is OperationResult<EncodedFrame>.Success success)
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            var image = await decoder.DecodeAsync(success.Value, cancellation.Token).ConfigureAwait(false);
                            cancellation.Token.ThrowIfCancellationRequested();
                            Publish(draft, new PreviewUpdate(draft.DeviceKey, draft.Revision, image, null));
                        }
                        else if (result is OperationResult<EncodedFrame>.Failure failure)
                        {
                            Publish(draft, new PreviewUpdate(draft.DeviceKey, draft.Revision, null, failure.Error));
                        }
                        else if (result is OperationResult<EncodedFrame>.Unknown unknown)
                        {
                            Publish(draft, new PreviewUpdate(draft.DeviceKey, draft.Revision, null, unknown.Error));
                        }
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        var report = errorReporter.Report(exception, "DraftPreview");
                        Publish(draft, new PreviewUpdate(draft.DeviceKey, draft.Revision, null, new AppError(report.ErrorId,
                            "PreviewFailed", "主题预览生成失败", "请检查主题内容后重试预览")));
                    }
                    finally
                    {
                        lock (syncRoot) { rendering = null; }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            errorReporter.Report(exception, "PreviewWorker");
        }
    }

    private AppError? AcquireReference(ThemeDraft draft, out AssetReferenceLease? lease)
    {
        lease = null;
        if (draft.Content.GetImageAssetId() is not { } assetId) { return null; }
        try
        {
            var acquired = assets.AcquireReference(assetId, AssetReferenceOwner.Rendering);
            if (acquired is OperationResult<AssetReferenceLease>.Success success) { lease = success.Value; return null; }
            return acquired is OperationResult<AssetReferenceLease>.Failure failure ? failure.Error
                : AppError.Create("PreviewAssetReferenceUnconfirmed", "预览素材引用未确认", "请重新选择素材后重试预览");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "PreviewAssetReference");
            return new(report.ErrorId, "PreviewAssetReferenceFailed", "预览素材引用未取得", "请检查诊断后重试预览");
        }
    }

    private void Publish(ThemeDraft draft, PreviewUpdate update)
    {
        lock (syncRoot)
        {
            if (disposed || !visible || desired?.Revision != draft.Revision || desired.DeviceKey != draft.DeviceKey) { return; }
            latest = update;
            if (update.Error is not null) { failedRevision = draft.Revision; }
            if (notificationQueued) { return; }
            notificationQueued = true;
        }
        try
        {
            if (!dispatcher.TryEnqueue(PublishLatest, background: true)) { ClearPendingNotification(); }
        }        catch (InvalidOperationException exception)
        {
            ClearPendingNotification();
            errorReporter.Report(exception, "PreviewNotificationDuringShutdown");
        }
    }

    private void PublishLatest()
    {
        PreviewUpdate? update;
        lock (syncRoot)
        {
            update = visible && !disposed && latest?.Revision == desired?.Revision
                && latest?.DeviceKey == desired?.DeviceKey ? latest : null;
            latest = null;
            notificationQueued = false;
        }
        if (update is not null) { Updated?.Invoke(update); }
    }

    private void ClearPendingNotification()
    {
        lock (syncRoot) { latest = null; notificationQueued = false; }
    }

    private async Task UpdateClockAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                lock (syncRoot)
                {
                    if (visible && desired is { Content.IsDynamic: true }
                        && failedRevision != desired.Revision) { signals.Writer.TryWrite(0); }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { errorReporter.Report(exception, "PreviewClock"); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (syncRoot)
        {
            if (!disposed)
            {
                disposed = true;
                visible = false;
                lifetime.Cancel();
                rendering?.Cancel();
                signals.Writer.TryComplete();
                latest = null;
            }
        }
        await Task.WhenAll(processing, clock).ConfigureAwait(false);
        lock (syncRoot)
        {
            desiredAsset?.Dispose();
            desiredAsset = null;
            desired = null;
        }
        Updated = null;
        if (Interlocked.Exchange(ref resourcesDisposed, 1) == 0) { lifetime.Dispose(); }
    }
}
