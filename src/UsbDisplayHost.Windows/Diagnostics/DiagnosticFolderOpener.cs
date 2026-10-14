using System.Diagnostics;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Windows.Storage;

namespace UsbDisplayHost.Windows.Diagnostics;

public sealed class DiagnosticFolderOpener(ApplicationDataPaths paths, IErrorReporter errorReporter) : IDiagnosticFolderOpener
{
    public Task<OperationResult<Unit>> OpenAsync(CancellationToken cancellationToken) => Task.Run<OperationResult<Unit>>(() =>
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(paths.Logs);
            cancellationToken.ThrowIfCancellationRequested();
            using var opened = Process.Start(new ProcessStartInfo(paths.Logs) { UseShellExecute = true });
            return new OperationResult<Unit>.Success(new Unit());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new OperationResult<Unit>.Cancelled(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "OpenLogDirectory");
            return new OperationResult<Unit>.Failure(new AppError(report.ErrorId, "OpenLogsFailed",
                "日志目录无法打开", "请检查本地目录权限和系统文件管理器"));
        }
    }, CancellationToken.None);
}
