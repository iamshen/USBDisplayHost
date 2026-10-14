using System.Globalization;
using System.ComponentModel;
using Serilog.Events;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Windows.Storage;

namespace UsbDisplayHost.Windows.Diagnostics;

// 启动日志服务不可用时同步记录受限字段，沿用正式日志的文件配额。
public sealed class BootstrapErrorReporter : IErrorReporter
{
    private readonly object syncRoot = new();

    public ErrorReport Report(Exception exception, string stage)
    {
        string errorId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        try
        {
            lock (syncRoot)
            {
                using var sink = new StrictRollingFileSink(new ApplicationDataPaths().Logs);
                string safeStage = stage.Length is > 0 and <= 64
                    && stage.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
                    ? stage : "Unknown";
                string errorType = exception.GetType().FullName ?? "Exception";
                if (errorType.Length > 256) { errorType = errorType[..256]; }
                sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, null,
                    new MessageTemplate("Bootstrap operation failed", []),
                    [new LogEventProperty("ErrorId", new ScalarValue(errorId)),
                     new LogEventProperty("Stage", new ScalarValue(safeStage)),
                     new LogEventProperty("Code", new ScalarValue("BootstrapFailed")),
                     new LogEventProperty("ErrorType", new ScalarValue(errorType)),
                     new LogEventProperty("HResult", new ScalarValue(exception.HResult)),
                     new LogEventProperty("NativeError", new ScalarValue(exception is Win32Exception native
                         ? native.NativeErrorCode : (int?)null))]));
                // 只有写入及关闭均已确认，才能将应急记录标为已保存。
                sink.Dispose();
                return new ErrorReport(errorId, sink.Written == 1 && sink.Failures == 0 && sink.StorageAvailable);
            }
        }
        catch (Exception recordingException) when (recordingException is not OutOfMemoryException)
        {
            // 应急记录失败不能递归报告；由界面明确提示记录未保存。
            return new ErrorReport(errorId, false);
        }
    }
}
