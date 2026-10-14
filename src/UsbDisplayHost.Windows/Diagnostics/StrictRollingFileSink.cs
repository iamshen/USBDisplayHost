using System.Text;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace UsbDisplayHost.Windows.Diagnostics;

// 一个后台写者；轮转先删旧文件，再移动，配额始终包含当前文件。
internal sealed class StrictRollingFileSink(string directory) : ILogEventSink, IDisposable
{
    internal const long MaximumFileBytes = 10 * 1024 * 1024;
    internal const int MaximumFiles = 5;
    private const int MaximumRecordBytes = 64 * 1024;
    private readonly JsonFormatter formatter = new();
    private FileStream? stream;
    private long written;
    private long failures;
    private int storageAvailable = 1;
    private bool needsRecovery;
    private readonly object syncRoot = new();

    public long Written => Interlocked.Read(ref written);
    public long Failures => Interlocked.Read(ref failures);
    public bool StorageAvailable => Volatile.Read(ref storageAvailable) != 0;

    public void Emit(LogEvent logEvent)
    {
        lock (syncRoot) { EmitCore(logEvent); }
    }

    private void EmitCore(LogEvent logEvent)
    {
        try
        {
            using var text = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            formatter.Format(logEvent, text);
            byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
            if (bytes.Length > MaximumRecordBytes) { throw new InvalidDataException("诊断记录超出限定长度"); }
            EnsureOpen();
            if (stream!.Length + bytes.Length > MaximumFileBytes) { Rotate(); }
            long start = stream!.Length;
            try
            {
                stream.Write(bytes);
                stream.Flush();
            }
            catch
            {
                // 写入失败时尽力撤销不完整 JSON 行；无法截断则下一次转存整个受限文件。
                try { stream.SetLength(start); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { needsRecovery = true; }
                throw;
            }
            Interlocked.Increment(ref written);
            Volatile.Write(ref storageAvailable, 1);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Interlocked.Increment(ref failures);
            Volatile.Write(ref storageAvailable, 0);
            CloseStream();
            // 不能递归写日志；诊断页独立读取失败计数。
        }
    }

    private string FilePath(int index) => Path.Combine(directory, $"diagnostic-{index}.jsonl");

    private void EnsureOpen()
    {
        if (stream is not null) { return; }
        Directory.CreateDirectory(directory);
        for (int index = 0; index < MaximumFiles; index++)
        {
            string path = FilePath(index);
            if (File.Exists(path) && new FileInfo(path).Length > MaximumFileBytes)
            {
                // 日志目录仅持有本应用诊断文件，异常超配额输入不继续追加。
                File.Delete(path);
            }
        }
        stream = OpenCurrent();
        if (needsRecovery) { Rotate(); needsRecovery = false; }
    }

    private FileStream OpenCurrent()
    {
        var opened = new FileStream(FilePath(0), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, FileOptions.None);
        try { opened.Seek(0, SeekOrigin.End); return opened; }
        catch { opened.Dispose(); throw; }
    }

    private void Rotate()
    {
        CloseStream();
        File.Delete(FilePath(MaximumFiles - 1));
        for (int index = MaximumFiles - 2; index >= 0; index--)
        {
            string from = FilePath(index);
            if (File.Exists(from)) { File.Move(from, FilePath(index + 1), overwrite: true); }
        }
        stream = OpenCurrent();
    }

    private void CloseStream()
    {
        FileStream? closing = stream;
        stream = null;
        try { closing?.Dispose(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Interlocked.Increment(ref failures);
            Volatile.Write(ref storageAvailable, 0);
        }
    }

    public LogFileSnapshot[] CaptureSnapshot()
    {
        lock (syncRoot)
        {
            var snapshots = new List<LogFileSnapshot>(MaximumFiles);
            try
            {
                for (int index = 0; index < MaximumFiles; index++)
                {
                    string path = FilePath(index);
                    if (!File.Exists(path)) { continue; }
                    var opened = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    try { snapshots.Add(new LogFileSnapshot(opened, Math.Min(opened.Length, MaximumFileBytes))); }
                    catch { opened.Dispose(); throw; }
                }
                return [.. snapshots];
            }
            catch
            {
                foreach (var snapshot in snapshots) { snapshot.Stream.Dispose(); }
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (syncRoot) { CloseStream(); }
    }
}

internal sealed record LogFileSnapshot(FileStream Stream, long Length);
