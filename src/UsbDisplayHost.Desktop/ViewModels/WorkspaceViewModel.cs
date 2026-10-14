using CommunityToolkit.Mvvm.ComponentModel;

namespace UsbDisplayHost.Desktop.ViewModels;

public sealed partial class WorkspaceViewModel : ObservableObject
{
    [ObservableProperty]
    private string statusText = "未连接设备";

    [ObservableProperty]
    private string errorText = string.Empty;
}
