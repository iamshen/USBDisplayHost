namespace UsbDisplayHost.Desktop.Services;

public interface IImagePicker
{
    string? PickImage();
}

public sealed class ImagePicker(Func<System.Windows.Window?> owner) : IImagePicker
{
    public string? PickImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "PNG/JPEG 图片|*.png;*.jpg;*.jpeg",
            CheckFileExists = true,
            Multiselect = false,
            Title = "选择主题图片"
        };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }
}
