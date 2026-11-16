using Microsoft.UI.Xaml;

namespace UsbDisplayHost.WinUI;

public partial class App : Application
{
    private readonly StartupLog log = new();
    private MainWindow? window;

    public App()
    {
        UnhandledException += OnUnhandledException;
        log.Write("启动无设备样板");
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow(log);
        window.Activate();
        log.Write("主窗口已打开");
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        log.Write($"未处理异常 {args.Exception.GetType().Name} (0x{args.Exception.HResult:X8})");
    }
}
