using System.Windows;
using Microsoft.Win32;

namespace UsbDisplayHost.Desktop.Services;

public interface IExportDestinationPicker
{
    string? Pick();
}

public sealed class ExportDestinationPicker(Func<Window?> owner) : IExportDestinationPicker
{
    public string? Pick()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出诊断", Filter = "ZIP 诊断文件|*.zip", DefaultExt = ".zip", AddExtension = true,
            FileName = $"USBDisplay-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip", OverwritePrompt = true
        };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }
}
