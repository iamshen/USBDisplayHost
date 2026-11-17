using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Windows.Diagnostics;
using UsbDisplayHost.Windows.Lifecycle;
using UsbDisplayHost.Windows.Storage;
using UsbDisplayHost.Windows.Updates;
using Velopack;

namespace UsbDisplayHost.WinUI;

internal static class Program
{
    public const string ProductId = GitHubAppUpdateService.BetaProductId;
    public const string ProductName = "USB Display Beta";
    public const string ExecutableName = "UsbDisplayHost.WinUI.exe";
    private const string UiOnlyInstanceId = ProductId + ".UiOnly";

    [STAThread]
    private static int Main(string[] args)
    {
        bool uiOnly = args.Contains("--ui-only", StringComparer.Ordinal);
        DiagnosticLog? diagnostics = null;
        try
        {
            // 安装回调处理完毕后才取得实例锁或初始化窗口和设备。
            VelopackApp.Build()
                .SetArgs(args)
                .SetAutoApplyOnStartup(false)
                .OnBeforeUninstallFastCallback(_ =>
                {
                    if (!uiOnly) { WindowsStartupRegistration.RemoveCurrentApplicationEntry(ProductId, ExecutableName); }
                })
                .Run();

            string dataRoot = uiOnly
                ? Environment.GetEnvironmentVariable("USB_DISPLAY_HOST_UI_ONLY_DATA_ROOT")
                    ?? Path.Combine(Path.GetTempPath(), "UsbDisplayHost.Beta.UiOnly", Guid.NewGuid().ToString("N"))
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsbDisplayHost.Beta.Data");
            var paths = new ApplicationDataPaths(dataRoot);
            string? version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion.Split('+')[0];
            diagnostics = new DiagnosticLog(paths, TimeProvider.System, version);
            using var instance = new SingleInstanceGate(diagnostics, uiOnly ? UiOnlyInstanceId : ProductId);
            if (!instance.IsOwner)
            {
                if (instance.ActivateExistingAsync().GetAwaiter().GetResult()) { return 0; }
                ShowMessage("USB Display Beta 已在运行，暂时无法打开窗口。请从托盘打开或稍后重试。");
                return 1;
            }

            diagnostics.Write(DiagnosticSeverity.Information, "Application", uiOnly ? "StartingUiOnly" : "Starting");
            const string updatedArgument = "--updated-to=";
            string? updatedTo = args.FirstOrDefault(arg => arg.StartsWith(updatedArgument, StringComparison.Ordinal))?[updatedArgument.Length..];
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                var queue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
                var application = new App(diagnostics, paths, queue, uiOnly, updatedTo);
                instance.SetActivationHandler(application.RequestActivation);
            });
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = diagnostics?.Report(exception, "ApplicationEntry");
            string detail = report is null ? "诊断日志未创建，请检查数据目录权限和剩余空间。"
                : $"错误编号：{report.ErrorId}";
            ShowMessage($"启动失败，请重启后重试。{detail}", error: true);
            return 1;
        }
        finally
        {
            if (diagnostics is not null)
            {
                try { diagnostics.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { diagnostics.Report(exception, "EntryDiagnosticShutdown"); }
            }
        }
    }

    internal static void ShowMessage(string message, bool error = false) =>
        MessageBoxW(0, message, ProductName, error ? 0x10u : 0x40u);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int MessageBoxW(nint window, string text, string caption, uint flags);
}
