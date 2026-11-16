using System.Diagnostics;
using System.Globalization;

namespace UsbDisplayHost.WinUI;

internal sealed class StartupLog : IDisposable
{
    private const string DirectoryName = "UsbDisplayHost.WinUI";
    private const int MaximumLogCount = 5;
    private readonly string logDirectory = Path.Combine(Path.GetTempPath(), DirectoryName);
    private readonly List<string> entries = [];
    private StreamWriter? writer;
    private string? logFileName;
    private bool writeFailed;

    public string Entries => string.Join(Environment.NewLine, entries);

    public string StorageStatus => writeFailed || logFileName is null
        ? "启动日志写入失败，请检查临时目录权限或剩余空间。"
        : Path.Combine("%TEMP%", DirectoryName, logFileName);

    public void Write(string message)
    {
        string entry = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:O}  {message}");
        entries.Add(entry);

        try
        {
            writer ??= OpenLog();
            writer.WriteLine(entry);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            writeFailed = true;
            Debug.WriteLine($"启动日志写入失败：{exception.GetType().Name}");
        }
    }

    private StreamWriter OpenLog()
    {
        Directory.CreateDirectory(logDirectory);
        var slots = Enumerable.Range(1, MaximumLogCount)
            .Select(index => Path.Combine(logDirectory, $"startup-{index}.log"))
            .OrderBy(File.GetLastWriteTimeUtc);

        foreach (string slot in slots)
        {
            try
            {
                // 持有写句柄直到退出，使其他样板实例跳过仍在使用的日志。
                var stream = new FileStream(slot, FileMode.Create, FileAccess.Write, FileShare.Read);
                logFileName = Path.GetFileName(slot);
                return new StreamWriter(stream) { AutoFlush = true };
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) == 32)
            {
                continue;
            }
        }

        throw new IOException("启动日志均在使用中。");
    }

    public void Dispose()
    {
        try { writer?.Dispose(); }
        catch (IOException exception)
        {
            Debug.WriteLine($"启动日志关闭失败：{exception.GetType().Name}");
        }
    }
}
