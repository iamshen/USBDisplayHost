using Microsoft.UI.Xaml.Media.Imaging;
using UsbDisplayHost.Core.Themes;
using UsbDisplayHost.Desktop.Services;

namespace UsbDisplayHost.WinUI.Services;

internal sealed class WinUiFramePreviewDecoder(IUiDispatcher dispatcher) : IFramePreviewDecoder
{
    public async Task<object> DecodeAsync(EncodedFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (dispatcher.HasThreadAccess) { return await ReadAsync(frame, cancellationToken); }
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        if (!dispatcher.TryEnqueue(() => _ = ReadOnUiAsync(), background: true))
        {
            throw new InvalidOperationException("界面线程已停止接收画面预览");
        }
        return await completion.Task.ConfigureAwait(false);

        async Task ReadOnUiAsync()
        {
            try { completion.TrySetResult(await ReadAsync(frame, cancellationToken)); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { completion.TrySetCanceled(cancellationToken); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { completion.TrySetException(exception); }
        }
    }

    private static async Task<object> ReadAsync(EncodedFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var bytes = new MemoryStream(frame.Jpeg.ToArray(), writable: false);
        using var source = bytes.AsRandomAccessStream();
        var image = new BitmapImage { DecodePixelWidth = Math.Min(frame.Width, 576) };
        await image.SetSourceAsync(source).AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return image;
    }
}
