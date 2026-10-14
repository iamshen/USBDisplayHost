using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using UsbDisplayHost.Core.Devices;

namespace UsbDisplayHost.Core.Diagnostics;

public enum DiagnosticSeverity { Information, Warning, Error }

public sealed record DiagnosticContext(string? AnonymousDeviceKey = null, long? SessionGeneration = null,
    Guid? OperationId = null, string? State = null, long? ActualBytes = null, double? ElapsedMilliseconds = null,
    string? FirmwareVersion = null, string? DriverVersion = null)
{
    public static string Anonymize(DeviceKey key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Value)));
}

public sealed record DiagnosticEvent(long Sequence, DateTimeOffset Timestamp, DiagnosticSeverity Severity,
    string Stage, string Code, string? ErrorId, DiagnosticContext? Context);

public sealed record DiagnosticStorageStatus(long WrittenEvents, long DroppedEvents, int PendingEvents,
    long WriteFailures, bool IsStorageAvailable, bool IsStopped);

public sealed class DiagnosticJournal
{
    public const int Capacity = 500;
    private readonly object syncRoot = new();
    private readonly Queue<DiagnosticEvent> events = new(Capacity);
    private long sequence;

    public DiagnosticEvent Append(DateTimeOffset timestamp, DiagnosticSeverity severity, string stage,
        string code, string? errorId, DiagnosticContext? context)
    {
        lock (syncRoot)
        {
            var item = new DiagnosticEvent(++sequence, timestamp, severity, stage, code, errorId, context);
            if (events.Count == Capacity) { events.Dequeue(); }
            events.Enqueue(item);
            return item;
        }
    }

    public ImmutableArray<DiagnosticEvent> GetSnapshot()
    {
        lock (syncRoot) { return [.. events]; }
    }
}

public interface IDiagnosticService : IErrorReporter, IAsyncDisposable
{
    DiagnosticJournal Journal { get; }
    DiagnosticStorageStatus GetStorageStatus();
    // 诊断写入不得因存储不可用而中断业务；错误编号沿用原业务结果。
    void Write(DiagnosticSeverity severity, string stage, string code, DiagnosticContext? context = null, string? errorId = null);
}
