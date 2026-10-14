using System.Collections.Immutable;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Telemetry;

namespace UsbDisplayHost.Core.Themes;

public sealed record RenderRequest(
    ThemeContent Content, DeviceSettings Settings, int Width, int Height, int MaximumFrameBytes,
    FrameContext Context, long FrameNumber, FramePurpose Purpose,
    DateTimeOffset LocalTime, ImmutableArray<MetricSnapshot> Metrics);

public interface IFrameRenderer : IAsyncDisposable
{
    Task<OperationResult<EncodedFrame>> RenderAsync(RenderRequest request, CancellationToken cancellationToken);
}
