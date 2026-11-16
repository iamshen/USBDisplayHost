using System.IO;
using System.Windows.Media.Imaging;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Desktop.Services;

internal sealed class WpfFramePreviewDecoder : IFramePreviewDecoder
{
    public Task<object> DecodeAsync(EncodedFrame frame, CancellationToken cancellationToken)
        => Task.Run<object>(() =>
        {
            using var source = new MemoryStream(frame.Jpeg.ToArray(), writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = Math.Min(frame.Width, 576);
            bitmap.StreamSource = source;
            bitmap.EndInit();
            bitmap.Freeze();
            cancellationToken.ThrowIfCancellationRequested();
            return bitmap;
        }, cancellationToken);
}
