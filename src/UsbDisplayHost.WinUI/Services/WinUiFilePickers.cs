using Microsoft.UI;
using Microsoft.Windows.Storage.Pickers;
using UsbDisplayHost.Desktop.Services;

namespace UsbDisplayHost.WinUI.Services;

internal sealed class WinUiFilePickers(Func<WindowId?> getWindow) : IImagePicker, IExportDestinationPicker
{
    public async Task<string?> PickImageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (getWindow() is not { } id) { return null; }
        var picker = new FileOpenPicker(id)
        {
            Title = "选择主题图片",
            FileTypeFilter = { ".png", ".jpg", ".jpeg" }
        };
        var selected = await picker.PickSingleFileAsync().AsTask(cancellationToken);
        return selected?.Path;
    }

    public async Task<string?> PickAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (getWindow() is not { } id) { return null; }
        var picker = new FileSavePicker(id)
        {
            Title = "导出诊断",
            SuggestedFileName = $"USBDisplay-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            DefaultFileExtension = ".zip"
        };
        picker.FileTypeChoices.Add("ZIP 诊断文件", [".zip"]);
        var selected = await picker.PickSaveFileAsync().AsTask(cancellationToken);
        return selected?.Path;
    }
}
