using System.IO;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Rendering.Images;

public sealed class WpfImageValidator(IErrorReporter errorReporter) : IImageValidator
{
    private readonly SemaphoreSlim importGate = new(1, 1);

    public async Task<OperationResult<ImageInfo>> ValidateAsync(Stream source, CancellationToken cancellationToken)
    {
        bool entered = false;
        try
        {
            await importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            var completion = new TaskCompletionSource<OperationResult<ImageInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    completion.TrySetResult(WpfImageLoader.Load(source, cancellationToken) switch
                    {
                        OperationResult<DecodedImage>.Success image => new OperationResult<ImageInfo>.Success(image.Value.Info),
                        OperationResult<DecodedImage>.Failure failure => new OperationResult<ImageInfo>.Failure(failure.Error),
                        _ => new OperationResult<ImageInfo>.Cancelled()
                    });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetResult(new OperationResult<ImageInfo>.Cancelled());
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    completion.TrySetResult(Failure(exception));
                }
            }) { IsBackground = true, Name = "UsbDisplayHost.ImageImport" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            // 不用取消等待提前归还流，必须等 STA 解码实际结束。
            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new OperationResult<ImageInfo>.Cancelled();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failure(exception);
        }
        finally
        {
            if (entered) { importGate.Release(); }
        }
    }

    private OperationResult<ImageInfo> Failure(Exception exception)
    {
        var report = errorReporter.Report(exception, "ImageDecode");
        return new OperationResult<ImageInfo>.Failure(new AppError(report.ErrorId, "ImageDecodeFailed",
            "图片无法完整解码，未导入该素材", "请选择有效的 PNG/JPEG 图片后重试"));
    }
}
