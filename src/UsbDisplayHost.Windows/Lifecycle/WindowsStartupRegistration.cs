using Microsoft.Win32;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Windows.Lifecycle;

public sealed class WindowsStartupRegistration(IErrorReporter errorReporter) : IStartupRegistration
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "USBDisplayHost";
    private readonly object syncRoot = new();
    private readonly Guid owner = Guid.NewGuid();

    // 卸载时只移除指向当前应用的自启项。
    public static void RemoveCurrentApplicationEntry()
    {
        string command = Command();
        using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: true);
        object? value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is string text && text == command && key!.GetValueKind(ValueName) == RegistryValueKind.String)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public Task<OperationResult<StartupRegistrationState>> QueryAsync(CancellationToken cancellationToken) => RunAsync(() =>
    {
        string command = Command();
        using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: false);
        object? value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        bool owned = value is null || value is string text && text == command && key!.GetValueKind(ValueName) == RegistryValueKind.String;
        return new OperationResult<StartupRegistrationState>.Success(new StartupRegistrationState(value is not null && owned, owned));
    }, "StartupQuery", cancellationToken);

    public Task<OperationResult<IStartupRegistrationCheckpoint>> ChangeAsync(bool enabled, CancellationToken cancellationToken) =>
        RunAsync<IStartupRegistrationCheckpoint>(() =>
        {
            string command = Command();
            using var key = Registry.CurrentUser.CreateSubKey(RunPath, writable: true);
            object? before = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (before is not null && (before is not string text || text != command || key.GetValueKind(ValueName) != RegistryValueKind.String))
            {
                return new OperationResult<IStartupRegistrationCheckpoint>.Failure(AppError.Create("StartupEntryConflict",
                    "同名启动项与当前应用不匹配，未修改", "请检查已安装版本的自启配置后重试"));
            }
            var checkpoint = new Checkpoint(owner, (string?)before, enabled ? command : null);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Apply(key, checkpoint.After);
                Verify(key, checkpoint.After);
                return new OperationResult<IStartupRegistrationCheckpoint>.Success(checkpoint);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = errorReporter.Report(exception, "StartupChange");
                try
                {
                    object? actual = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (Equals(actual, checkpoint.Before))
                    {
                        return Failure<IStartupRegistrationCheckpoint>(report.ErrorId);
                    }
                    if (!Equals(actual, checkpoint.After)) { return Unknown<IStartupRegistrationCheckpoint>(report.ErrorId); }
                    Apply(key, checkpoint.Before);
                    Verify(key, checkpoint.Before);
                    return Failure<IStartupRegistrationCheckpoint>(report.ErrorId);
                }
                catch (Exception recoveryException) when (recoveryException is not OutOfMemoryException)
                {
                    errorReporter.Report(recoveryException, "StartupChangeRecovery");
                    return Unknown<IStartupRegistrationCheckpoint>(report.ErrorId);
                }
            }
        }, "StartupChangePrepare", cancellationToken);

    public Task<OperationResult<Unit>> RestoreAsync(IStartupRegistrationCheckpoint checkpoint, CancellationToken cancellationToken) =>
        RunAsync<Unit>(() =>
        {
            if (checkpoint is not Checkpoint saved || saved.Owner != owner)
            {
                return new OperationResult<Unit>.Failure(AppError.Create("InvalidStartupCheckpoint",
                    "自启恢复上下文无效", "请重新读取设置和自启状态"));
            }
            using var key = Registry.CurrentUser.CreateSubKey(RunPath, writable: true);
            object? actual = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (Equals(actual, saved.Before)) { return new OperationResult<Unit>.Success(new Unit()); }
            if (!Equals(actual, saved.After))
            {
                return Unknown<Unit>(Guid.NewGuid().ToString("N"));
            }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Apply(key, saved.Before);
                Verify(key, saved.Before);
                return new OperationResult<Unit>.Success(new Unit());
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = errorReporter.Report(exception, "StartupRestore");
                return Unknown<Unit>(report.ErrorId);
            }
        }, "StartupRestorePrepare", cancellationToken);

    private Task<OperationResult<T>> RunAsync<T>(Func<OperationResult<T>> work, string stage, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (syncRoot) { cancellationToken.ThrowIfCancellationRequested(); return work(); }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new OperationResult<T>.Cancelled(); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = errorReporter.Report(exception, stage);
                return Failure<T>(report.ErrorId);
            }
        }, CancellationToken.None);

    private static string Command()
    {
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)
            || !string.Equals(Path.GetFileName(executable), "UsbDisplayHost.Desktop.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("当前启动方式不支持应用自启");
        }
        string command = $"\"{Path.GetFullPath(executable)}\"";
        if (command.Length > 260) { throw new InvalidOperationException("启动命令超过系统支持长度"); }
        return command;
    }

    private static void Apply(RegistryKey key, string? command)
    {
        if (command is null) { key.DeleteValue(ValueName, throwOnMissingValue: false); }
        else { key.SetValue(ValueName, command, RegistryValueKind.String); }
    }
    private static void Verify(RegistryKey key, string? expected)
    {
        object? actual = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (!Equals(actual, expected) || actual is not null && key.GetValueKind(ValueName) != RegistryValueKind.String)
        {
            throw new IOException("自启注册结果无法确认");
        }
    }
    private static OperationResult<T> Failure<T>(string errorId) => new OperationResult<T>.Failure(new AppError(errorId,
        "StartupRegistrationFailed", "自启注册未完成，设置未保存", "请检查当前用户权限和应用安装位置后重试"));
    private static OperationResult<T> Unknown<T>(string errorId) => new OperationResult<T>.Unknown(new AppError(errorId,
        "StartupStateUnconfirmed", "自启实际状态需要确认", "请重新读取设置并检查系统启动项"));
    private sealed record Checkpoint(Guid Owner, string? Before, string? After) : IStartupRegistrationCheckpoint;
}
