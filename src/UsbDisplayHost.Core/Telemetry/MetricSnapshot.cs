namespace UsbDisplayHost.Core.Telemetry;

public enum MetricSource { Host, Device }
public enum MetricKind { CpuUsage, MemoryUsage, Temperature, FanSpeed, FramesPerSecond }
public enum MetricAvailability { Available, Unavailable, Stale }

public sealed record MetricSnapshot(
    MetricSource Source, MetricKind Kind, double? Value, string Unit,
    long SampleTimestamp, DateTimeOffset SampleTime, MetricAvailability Availability);

public sealed class MetricFreshness(TimeProvider timeProvider)
{
    public MetricSnapshot Evaluate(MetricSnapshot metric)
    {
        if (metric.Availability == MetricAvailability.Unavailable || metric.Value is null)
        {
            return metric with { Value = null, Availability = MetricAvailability.Unavailable };
        }

        return metric with
        {
            Availability = timeProvider.GetElapsedTime(metric.SampleTimestamp) > TimeSpan.FromSeconds(3)
                ? MetricAvailability.Stale
                : MetricAvailability.Available
        };
    }
}
