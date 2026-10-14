using System.Collections.Immutable;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Telemetry;

public interface IMetricProvider : IAsyncDisposable
{
    Task<OperationResult<ImmutableArray<MetricSnapshot>>> SampleAsync(CancellationToken cancellationToken);
    ValueTask IAsyncDisposable.DisposeAsync() => ValueTask.CompletedTask;
}
