using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Themes;
using UsbDisplayHost.Core.Transport;

namespace UsbDisplayHost.Core.Sessions;

public sealed class CoordinatorFrameGuard(DeviceCoordinator coordinator) : IFrameSessionGuard
{
    public bool CanStartFrame(FrameContext context) => coordinator.ResolveFrameOperation(context)
        is OperationResult<OperationContext>.Success;
    public bool IsLiveSession(DeviceKey deviceKey, long sessionGeneration) => coordinator.IsLiveSession(deviceKey, sessionGeneration);
    public OperationResult<OperationContext> ResolveRecoveryContext(FrameContext context) => coordinator.ResolveFrameOperation(context);
}
