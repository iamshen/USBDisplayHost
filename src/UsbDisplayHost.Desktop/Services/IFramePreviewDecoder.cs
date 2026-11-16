using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Desktop.Services;

public interface IFramePreviewDecoder
{
    Task<object> DecodeAsync(EncodedFrame frame, CancellationToken cancellationToken);
}
