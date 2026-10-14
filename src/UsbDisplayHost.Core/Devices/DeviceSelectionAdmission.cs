namespace UsbDisplayHost.Core.Devices;

public interface IDeviceSelectionValidity
{
    // 只核对内存中的发现版本／通知代次；不得执行 I/O 或回调协调器。
    bool IsCurrent(DeviceSelection selection);
}

public sealed record DeviceSelectionAdmission(DeviceSelection Selection, IDeviceSelectionValidity Validity);
