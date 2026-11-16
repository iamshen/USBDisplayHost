namespace UsbDisplayHost.Desktop.Services;

public interface IImagePicker
{
    Task<string?> PickImageAsync(CancellationToken cancellationToken);
}
