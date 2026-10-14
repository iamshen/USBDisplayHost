using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Themes;
using UsbDisplayHost.Core.Transport;

namespace UsbDisplayHost.Rendering.Images;

public sealed class WpfJpegFrameValidator(IErrorReporter errorReporter) : IJpegFrameValidator
{
    private readonly SemaphoreSlim decodeGate = new(1, 1);

    public async Task<OperationResult<JpegFrameInfo>> ValidateAsync(EncodedFrame frame, DeviceCapabilities capabilities,
        FirmwareJpegProfile? profile, CancellationToken cancellationToken)
    {
        bool entered = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (profile is null || !capabilities.IsProtocolMatched
                || !StringComparer.Ordinal.Equals(profile.FirmwareVersion, capabilities.FirmwareVersion))
            {
                return Reject("UnverifiedJpegCompatibility", "缺少匹配固件的 JPEG 兼容记录");
            }
            if (frame.Purpose != FramePurpose.Active || frame.Context.DeviceKey is null
                || frame.Context.SessionGeneration <= 0 || frame.Context.OperationId == Guid.Empty
                || frame.Context.ContentRevision < 0 || frame.FrameNumber < 0
                || frame.Jpeg.IsDefaultOrEmpty || frame.Jpeg.Length > Math.Min(1_048_576, capabilities.MaximumFrameBytes)
                || frame.Width != capabilities.Width || frame.Height != capabilities.Height
                || frame.Width != 1920 || frame.Height != 1080)
            {
                return Reject("InvalidDeviceFrame", "设备帧、尺寸或会话字段无效");
            }
            await decodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            // 一次持有不超过 1MiB 的自有副本，取消不提前结束正在使用它的解码线程。
            byte[] bytes = frame.Jpeg.ToArray();
            var inspected = JpegStructureInspector.Inspect(bytes);
            if (inspected is not OperationResult<JpegFrameInfo>.Success success) { return inspected; }
            var info = success.Value;
            if (info.Width != frame.Width || info.Height != frame.Height || !profile.Accepts(info.Format))
            {
                return Reject("UnsupportedFirmwareJpeg", "JPEG 编码信息与设备尺寸或固件清单不匹配");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<OperationResult<JpegFrameInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    using var stream = new MemoryStream(bytes, writable: false);
                    // 设备帧使用 SOF 物理像素，不应用素材 EXIF 旋转或 DPI 缩放。
                    var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth != info.Width
                        || decoder.Frames[0].PixelHeight != info.Height)
                    {
                        completion.TrySetResult(Reject("JpegDecodeMismatch", "JPEG 解码尺寸与编码信息不一致"));
                        return;
                    }
                    var pixels = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
                    int stride = checked(info.Width * 4);
                    const int rows = 32;
                    byte[] strip = new byte[checked(stride * Math.Min(rows, info.Height))];
                    for (int row = 0; row < info.Height; row += rows)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        pixels.CopyPixels(new Int32Rect(0, row, info.Width, Math.Min(rows, info.Height - row)), strip, stride, 0);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    completion.TrySetResult(new OperationResult<JpegFrameInfo>.Success(info));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetResult(new OperationResult<JpegFrameInfo>.Cancelled());
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    completion.TrySetResult(Failure(exception));
                }
            }) { IsBackground = true, Name = "UsbDisplayHost.JpegValidation" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new OperationResult<JpegFrameInfo>.Cancelled();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failure(exception);
        }
        finally
        {
            if (entered) { decodeGate.Release(); }
        }
    }

    private OperationResult<JpegFrameInfo> Failure(Exception exception)
    {
        var report = errorReporter.Report(exception, "DeviceJpegDecode");
        return new OperationResult<JpegFrameInfo>.Failure(new AppError(report.ErrorId, "DeviceJpegDecodeFailed",
            "JPEG 无法完整解码，未放行该帧", "请重新生成画面并核对固件兼容记录"));
    }

    private static OperationResult<JpegFrameInfo> Reject(string code, string reason) =>
        new OperationResult<JpegFrameInfo>.Failure(AppError.Create(code, reason, "请重新生成画面并核对固件兼容记录"));
}
