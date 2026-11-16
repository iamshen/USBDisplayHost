namespace UsbDisplayHost.Desktop.Services;

public interface IExportDestinationPicker
{
    Task<string?> PickAsync(CancellationToken cancellationToken);
}
