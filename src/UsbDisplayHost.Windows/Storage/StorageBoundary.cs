using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Windows.Storage;

internal sealed class StorageBoundary(IErrorReporter errorReporter)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<OperationResult<T>> RunAsync<T>(Func<Task<OperationResult<T>>> operation,
        string stage, CancellationToken cancellationToken)
    {
        bool entered = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new OperationResult<T>.Cancelled();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, stage);
            return new OperationResult<T>.Failure(new AppError(report.ErrorId, "StorageFailed",
                "本地文件操作失败，原配置未标记为保存成功", "请检查目录权限、磁盘空间或关闭占用文件的程序后重试"));
        }
        finally
        {
            if (entered)
            {
                gate.Release();
            }
        }
    }
}
