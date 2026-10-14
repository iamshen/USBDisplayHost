using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Themes;
using UsbDisplayHost.Rendering.Encoding;
using UsbDisplayHost.Rendering.Images;
using UsbDisplayHost.Rendering.Scenes;
using UsbDisplayHost.Rendering.Scheduling;

namespace UsbDisplayHost.Rendering;

public sealed class WpfFrameRenderer : IFrameRenderer
{
    private readonly IAssetStore assets;
    private readonly TimeProvider timeProvider;
    private readonly ScenePainter painter;
    private readonly LatestRenderScheduler scheduler;

    public WpfFrameRenderer(IAssetStore assets, TimeProvider timeProvider, IErrorReporter errorReporter)
    {
        this.assets = assets;
        this.timeProvider = timeProvider;
        painter = new ScenePainter(timeProvider);
        scheduler = new LatestRenderScheduler(Render, errorReporter);
    }

    public Task<OperationResult<EncodedFrame>> RenderAsync(RenderRequest request, CancellationToken cancellationToken) =>
        scheduler.Enqueue(request, cancellationToken);

    private OperationResult<EncodedFrame> Render(RenderRequest request, CancellationToken cancellationToken)
    {
        if (request.Width <= 0 || request.Height <= 0 || (long)request.Width * request.Height > ImageInputInspector.MaximumPixels
            || request.MaximumFrameBytes is < 1 or > 1_048_576 || request.FrameNumber < 0
            || request.Context.ContentRevision < 0 || request.Context.OperationId == Guid.Empty
            || (request.Purpose == FramePurpose.Active && (request.Context.DeviceKey is null
                || request.Context.SessionGeneration <= 0 || request.Width != 1920 || request.Height != 1080)))
        {
            return Invalid("渲染尺寸、限额或会话信息无效");
        }
        if (ThemeValidation.Validate(request.Content) is OperationResult<Unit>.Failure invalid)
        {
            return new OperationResult<EncodedFrame>.Failure(invalid.Error);
        }

        BitmapSource? image = null;
        if (request.Content is ThemeContent.Image { AssetId: not null } picture)
        {
            // 等待只发生在专用 STA 工作线程，不占用管理窗口 Dispatcher。
            var sourceResult = assets.OpenReadAsync(picture.AssetId, cancellationToken).GetAwaiter().GetResult();
            if (sourceResult is OperationResult<Stream>.Failure missing)
            {
                return new OperationResult<EncodedFrame>.Failure(missing.Error);
            }
            if (sourceResult is OperationResult<Stream>.Unknown unknown)
            {
                return new OperationResult<EncodedFrame>.Failure(unknown.Error);
            }
            if (sourceResult is not OperationResult<Stream>.Success source) { return new OperationResult<EncodedFrame>.Cancelled(); }
            using (source.Value)
            {
                var decoded = WpfImageLoader.Load(source.Value, cancellationToken);
                if (decoded is OperationResult<DecodedImage>.Failure failure)
                {
                    return new OperationResult<EncodedFrame>.Failure(failure.Error);
                }
                image = ((OperationResult<DecodedImage>.Success)decoded).Value.Pixels;
            }
        }

        bool sideways = request.Settings.Rotation is DisplayRotation.Degrees90 or DisplayRotation.Degrees270;
        double logicalWidth = sideways ? request.Height : request.Width;
        double logicalHeight = sideways ? request.Width : request.Height;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            Matrix rotation = request.Settings.Rotation switch
            {
                DisplayRotation.Degrees0 => Matrix.Identity,
                DisplayRotation.Degrees90 => new Matrix(0, 1, -1, 0, request.Width, 0),
                DisplayRotation.Degrees180 => new Matrix(-1, 0, 0, -1, request.Width, request.Height),
                DisplayRotation.Degrees270 => new Matrix(0, -1, 1, 0, 0, request.Height),
                _ => throw new InvalidDataException("Invalid display rotation.")
            };
            drawing.PushTransform(new MatrixTransform(rotation));
            painter.Paint(drawing, request, logicalWidth, logicalHeight, image);
            drawing.Pop();
        }
        cancellationToken.ThrowIfCancellationRequested();
        var bitmap = new RenderTargetBitmap(request.Width, request.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        foreach (int quality in request.Settings.GetEncodingQualities())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var output = new LimitedEncodingStream(request.MaximumFrameBytes, cancellationToken);
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            try { encoder.Save(output); }
            catch (Exception exception) when (exception is not OutOfMemoryException && output.HasExceededLimit) { continue; }
            catch (Exception exception) when (exception is not OutOfMemoryException && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            return new OperationResult<EncodedFrame>.Success(new EncodedFrame(
                ImmutableArray.CreateRange(output.ToArray()), request.Width, request.Height,
                timeProvider.GetTimestamp(), request.Context, request.FrameNumber, request.Purpose));
        }
        return Invalid("所有画质尝试均超过设备帧长限制，请调整图片或主题内容");
    }

    private static OperationResult<EncodedFrame> Invalid(string reason) =>
        new OperationResult<EncodedFrame>.Failure(AppError.Create("InvalidRenderInput", reason, "请检查内容和设备配置后重试"));

    public ValueTask DisposeAsync() => scheduler.DisposeAsync();
}
