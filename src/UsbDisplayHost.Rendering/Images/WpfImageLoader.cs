using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Rendering.Images;

internal sealed record DecodedImage(BitmapSource Pixels, ImageInfo Info);

internal static class WpfImageLoader
{
    public static OperationResult<DecodedImage> Load(Stream source, CancellationToken cancellationToken)
    {
        var inspection = ImageInputInspector.Inspect(source, cancellationToken);
        if (inspection is OperationResult<ImageInfo>.Failure failure)
        {
            return new OperationResult<DecodedImage>.Failure(failure.Error);
        }
        var info = ((OperationResult<ImageInfo>.Success)inspection).Value;
        int? pngOrientation = null;
        if (info.Format == ImageFileFormat.Png)
        {
            var orientationResult = PngOrientationInspector.Inspect(source, cancellationToken);
            if (orientationResult is OperationResult<int>.Failure invalid)
            { return new OperationResult<DecodedImage>.Failure(invalid.Error); }
            pngOrientation = ((OperationResult<int>.Success)orientationResult).Value;
        }
        source.Position = 0;
        var decoder = BitmapDecoder.Create(source, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth != info.Width
            || decoder.Frames[0].PixelHeight != info.Height)
        {
            return new OperationResult<DecodedImage>.Failure(AppError.Create(
                "ImageDimensionsMismatch", "图片解码尺寸与文件头不一致", "请选择完整有效的 PNG/JPEG 图片"));
        }

        var frame = decoder.Frames[0];
        int orientation = pngOrientation ?? ReadOrientation(frame.Metadata as BitmapMetadata);
        BitmapSource pixels = new CachedBitmap(frame, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var transform = new TransformGroup();
        if (orientation is 2 or 5 or 7)
        {
            transform.Children.Add(new ScaleTransform(-1, 1));
        }
        if (orientation == 4)
        {
            transform.Children.Add(new ScaleTransform(1, -1));
        }
        int rotation = orientation switch { 3 => 180, 5 or 8 => 270, 6 or 7 => 90, _ => 0 };
        if (rotation != 0)
        {
            transform.Children.Add(new RotateTransform(rotation));
        }
        if (transform.Children.Count > 0)
        {
            pixels = new TransformedBitmap(pixels, transform);
        }

        // 逐块读取像素使截断或损坏在导入阶段暴露，不把仅有合法头部视为有效图片。
        var converted = new FormatConvertedBitmap(pixels, PixelFormats.Bgra32, null, 0);
        int stride = checked(converted.PixelWidth * 4);
        int rows = Math.Min(32, converted.PixelHeight);
        byte[] buffer = new byte[checked(stride * rows)];
        for (int row = 0; row < converted.PixelHeight; row += rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            converted.CopyPixels(new Int32Rect(0, row, converted.PixelWidth,
                Math.Min(rows, converted.PixelHeight - row)), buffer, stride, 0);
        }
        converted.Freeze();
        return new OperationResult<DecodedImage>.Success(new DecodedImage(converted,
            info with { Width = converted.PixelWidth, Height = converted.PixelHeight }));
    }

    private static int ReadOrientation(BitmapMetadata? metadata)
    {
        if (metadata is null)
        {
            return 1;
        }
        foreach (string query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            if (metadata.ContainsQuery(query) && metadata.GetQuery(query) is ushort orientation)
            {
                return orientation is >= 1 and <= 8 ? orientation : 1;
            }
        }
        return 1;
    }
}
