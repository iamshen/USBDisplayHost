using System.Collections.Immutable;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Telemetry;

public sealed class MetricSamplingService : IAsyncDisposable
{
    private readonly object syncRoot = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly IMetricProvider provider;
    private readonly TimeProvider timeProvider;
    private readonly MetricFreshness freshness;
    private readonly IErrorReporter errorReporter;
    private readonly Task sampling;
    private ImmutableArray<MetricSnapshot> latest = [];
    private Task? stopping;
    private bool resourcesDisposed;

    public MetricSamplingService(IMetricProvider provider, TimeProvider timeProvider, IErrorReporter errorReporter)
    {
        this.provider = provider;
        this.timeProvider = timeProvider;
        this.errorReporter = errorReporter;
        freshness = new MetricFreshness(timeProvider);
        sampling = Task.Run(SampleLoopAsync);
    }

    public ImmutableArray<MetricSnapshot> GetSnapshot()
    {
        lock (syncRoot) { return [.. latest.Select(freshness.Evaluate)]; }
    }

    private async Task SampleLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
            do
            {
                try
                {
                    var result = await provider.SampleAsync(lifetime.Token).ConfigureAwait(false);
                    if (result is OperationResult<ImmutableArray<MetricSnapshot>>.Success success)
                    {
                        lock (syncRoot) { latest = success.Value; }
                    }
                    // 暂时失败保留原有效样本，单调时钟将其标为过期，不伪造新采样时间。
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    errorReporter.Report(exception, "MetricSampling");
                }
            }
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            errorReporter.Report(exception, "MetricSamplingLoop");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task stop;
        lock (syncRoot) { stop = stopping ??= Task.Run(StopAsync); }
        await stop.ConfigureAwait(false);
    }

    private async Task StopAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await sampling.ConfigureAwait(false);
        await provider.DisposeAsync().ConfigureAwait(false);
        lock (syncRoot)
        {
            if (!resourcesDisposed) { resourcesDisposed = true; lifetime.Dispose(); }
        }
    }
}
