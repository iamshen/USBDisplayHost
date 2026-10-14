using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.Async;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Windows.Storage;

namespace UsbDisplayHost.Windows.Diagnostics;

public sealed class DiagnosticLog : IDiagnosticService
{
    private readonly TimeProvider timeProvider;
    private readonly StrictRollingFileSink file;
    private readonly QueueMonitor monitor = new();
    private readonly Logger logger;
    private readonly object syncRoot = new();
    private Task? stopping;
    private int stopped;
    private long reportingFailures;

    public DiagnosticLog(ApplicationDataPaths paths, TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        file = new StrictRollingFileSink(paths.Logs);
        logger = new LoggerConfiguration().MinimumLevel.Information()
            .Enrich.WithProperty("ApplicationVersion", typeof(DiagnosticLog).Assembly.GetName().Version?.ToString() ?? "unknown")
            .WriteTo.Async(sink => sink.Sink(file), bufferSize: 512, blockWhenFull: false, monitor: monitor)
            .CreateLogger();
    }

    public DiagnosticJournal Journal { get; } = new();
    internal LogFileSnapshot[] CaptureLogSnapshot() => file.CaptureSnapshot();

    public ErrorReport Report(Exception exception, string stage) => ReportCore(exception, stage, null);
    public ErrorReport Report(Exception exception, string stage, DiagnosticContext context) => ReportCore(exception, stage, context);

    private ErrorReport ReportCore(Exception exception, string stage, DiagnosticContext? context)
    {
        string errorId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            stage = Token(stage);
            context = Sanitize(context);
            Journal.Append(timeProvider.GetUtcNow(), DiagnosticSeverity.Error, stage, "OperationFailed", errorId, context);
            if (Volatile.Read(ref stopped) != 0) { return new ErrorReport(errorId, false); }
            string stack = Limit(string.Join(Environment.NewLine, (new StackTrace(exception, false).GetFrames() ?? [])
                .Take(32).Select(frame => frame.GetMethod() is { } method
                    ? Limit($"{method.DeclaringType?.FullName}.{method.Name}", 256) : "unknown")), 4096);
            var innerErrors = new List<ErrorCause>(3);
            for (Exception? cause = exception.InnerException; cause is not null && innerErrors.Count < 3; cause = cause.InnerException)
            {
                innerErrors.Add(new ErrorCause(Limit(cause.GetType().FullName ?? "Exception", 256), cause.HResult,
                    cause is Win32Exception nativeCause ? nativeCause.NativeErrorCode : null));
            }
            logger.ForContext("ErrorId", errorId).ForContext("Stage", stage)
                .ForContext("ErrorType", Limit(exception.GetType().FullName ?? "Exception", 256))
                .ForContext("HResult", exception.HResult)
                .ForContext("NativeError", exception is Win32Exception native ? native.NativeErrorCode : (int?)null)
                .ForContext("InnerErrors", innerErrors, destructureObjects: true)
                .ForContext("StackMethods", stack).ForContext("Association", context, destructureObjects: true)
                .Error("Operation failed at {Stage}", stage);
            // 异步缓冲返回不能证明落盘；最终写入／丢弃／失败由独立状态呈现。
            return new ErrorReport(errorId, false, IsPending: true);
        }
        catch (Exception reportingException) when (reportingException is not OutOfMemoryException)
        {
            Interlocked.Increment(ref reportingFailures);
            return new ErrorReport(errorId, false);
        }
    }

    public void Write(DiagnosticSeverity severity, string stage, string code, DiagnosticContext? context = null, string? errorId = null)
    {
        try
        {
            if (Volatile.Read(ref stopped) != 0) { return; }
            stage = Token(stage);
            code = Token(code);
            context = Sanitize(context);
            errorId = Guid.TryParseExact(errorId, "N", out var parsed) ? parsed.ToString("N") : null;
            Journal.Append(timeProvider.GetUtcNow(), severity, stage, code, errorId, context);
            logger.ForContext("Stage", stage).ForContext("Code", code).ForContext("ErrorId", errorId)
                .ForContext("Association", context, destructureObjects: true)
                .Write(severity switch
                {
                    DiagnosticSeverity.Error => LogEventLevel.Error,
                    DiagnosticSeverity.Warning => LogEventLevel.Warning,
                    _ => LogEventLevel.Information
                }, "{Stage} {Code}", stage, code);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Interlocked.Increment(ref reportingFailures); }
    }

    public DiagnosticStorageStatus GetStorageStatus() => new(file.Written, monitor.Dropped, monitor.Pending,
        file.Failures + Interlocked.Read(ref reportingFailures), file.StorageAvailable, Volatile.Read(ref stopped) == 2);

    private static DiagnosticContext? Sanitize(DiagnosticContext? context) => context is null ? null : context with
    {
        AnonymousDeviceKey = context.AnonymousDeviceKey is { Length: 64 } key && key.All(Uri.IsHexDigit) ? key : null,
        State = context.State is null ? null : Token(context.State),
        FirmwareVersion = context.FirmwareVersion is null ? null : Token(context.FirmwareVersion),
        DriverVersion = context.DriverVersion is null ? null : Token(context.DriverVersion),
        ActualBytes = context.ActualBytes is >= 0 ? context.ActualBytes : null,
        SessionGeneration = context.SessionGeneration is >= 0 ? context.SessionGeneration : null,
        ElapsedMilliseconds = context.ElapsedMilliseconds is >= 0 and <= double.MaxValue ? context.ElapsedMilliseconds : null
    };

    private static string Token(string value) => value.Length is > 0 and <= 64
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.') ? value : "Unknown";
    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];

    public ValueTask DisposeAsync()
    {
        lock (syncRoot) { return new ValueTask(stopping ??= Task.Run(Stop)); }
    }

    private void Stop()
    {
        Interlocked.Exchange(ref stopped, 1);
        try { logger.Dispose(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Interlocked.Increment(ref reportingFailures); }
        finally { Interlocked.Exchange(ref stopped, 2); }
    }

    private sealed class QueueMonitor : IAsyncLogEventSinkMonitor
    {
        private IAsyncLogEventSinkInspector? inspector;
        private long finalDropped;
        public long Dropped => Volatile.Read(ref inspector)?.DroppedMessagesCount ?? Interlocked.Read(ref finalDropped);
        public int Pending => Volatile.Read(ref inspector)?.Count ?? 0;
        public void StartMonitoring(IAsyncLogEventSinkInspector value) => Volatile.Write(ref inspector, value);
        public void StopMonitoring(IAsyncLogEventSinkInspector value)
        {
            Interlocked.Exchange(ref finalDropped, value.DroppedMessagesCount);
            Volatile.Write(ref inspector, null);
        }
    }

    private sealed record ErrorCause(string Type, int HResult, int? NativeError);
}
