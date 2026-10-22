using UsbDisplayHost.Windows.Diagnostics;
using UsbDisplayHost.Windows.Lifecycle;
using UsbDisplayHost.Windows.Storage;
using UsbDisplayHost.Core.Diagnostics;
using Velopack;

namespace UsbDisplayHost.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 安装回调先于单实例和设备服务处理。
        VelopackApp.Build()
            .SetArgs(args)
            .SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => WindowsStartupRegistration.RemoveCurrentApplicationEntry())
            .Run();

        IErrorReporter errorReporter = new BootstrapErrorReporter();
        DiagnosticLog? diagnostics = null;

        try
        {
            diagnostics = new DiagnosticLog(new ApplicationDataPaths(), TimeProvider.System);
            errorReporter = diagnostics;
            using var instance = new SingleInstanceGate(errorReporter);
            if (!instance.IsOwner)
            {
                if (!instance.ActivateExistingAsync().GetAwaiter().GetResult())
                {
                    System.Windows.MessageBox.Show("USB Display 已在运行，但暂时无法唤起窗口。请稍后重试或从托盘打开。",
                        "USB Display", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    return 1;
                }
                return 0;
            }
            diagnostics.Write(DiagnosticSeverity.Information, "Application", "Starting");
            var application = new App(diagnostics, args.Contains("--ui-only", StringComparer.Ordinal));
            instance.SetActivationHandler(application.RequestActivation);
            return application.Run();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "ApplicationEntry");
            try
            {
                System.Windows.MessageBox.Show(
                    $"应用无法初始化，请重启后重试。错误编号：{report.ErrorId}"
                    + (report.IsPending ? "。诊断记录已提交后台写入" : report.IsRecorded ? "" : "。错误记录未保存，请检查磁盘空间和目录权限"),
                    "USB Display", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            catch (Exception presentationException) when (presentationException is not OutOfMemoryException)
            {
                // 提示失败只提交诊断，不在同一错误路径再次打开对话框。
                errorReporter.Report(presentationException, "EntryFailurePresentation");
            }
            return 1;
        }
        finally
        {
            // 正常退出已由生命周期等待落盘；系统结束不在启动线程无限等待日志 I/O。
            if (diagnostics is not null)
            {
                try { diagnostics.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); }
                catch (Exception shutdownException) when (shutdownException is not OutOfMemoryException)
                {
                    // 日志已开始停止；尽力保留错误编号，不重启写者或弹出退出对话框。
                    errorReporter.Report(shutdownException, "EntryDiagnosticShutdown");
                }
            }
        }
    }
}
