using System.Diagnostics;
using System.Globalization;

namespace UsbDisplayHost.WinUI;

internal sealed class StartupLog
{
    private const string DirectoryName = "UsbDisplayHost.WinUI";
    private readonly string logDirectory = Path.Combine(Path.GetTempPath(), DirectoryName);
    private readonly string logFileName = $"startup-{Guid.NewGuid():N}.log";
    private readonly List<string> entries = [];
    private bool writeFailed;

    public string Entries => string.Join(Environment.NewLine, entries);

    public string StorageStatus => writeFailed
        ? "启动日志写入失败，请检查临时目录权限或剩余空间。"
        : Path.Combine("%TEMP%", DirectoryName, logFileName);

    public void Write(string message)
    {
        string entry = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:O}  {message}");
        entries.Add(entry);

        try
        {
            Directory.CreateDirectory(logDirectory);
            File.AppendAllLines(Path.Combine(logDirectory, logFileName), [entry]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            writeFailed = true;
            Debug.WriteLine($"启动日志写入失败：{exception.GetType().Name}");
        }
    }
}
