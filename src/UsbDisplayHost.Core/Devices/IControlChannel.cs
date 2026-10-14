using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;

namespace UsbDisplayHost.Core.Devices;

public sealed record ControlRequest(OperationContext Context, DeviceCommand Command, int Value);
public sealed record ControlObservation(OperationContext Context, bool Accepted, bool Completed,
    DeviceMode Mode, PowerState Power, int? Brightness, AppError? Error);

public interface IControlChannel : IAsyncDisposable
{
    Task<OperationResult<ControlObservation>> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken);
    IAsyncEnumerable<ControlObservation> ObserveAsync(CancellationToken cancellationToken);
    // 只有成功结果才能证明原请求及通道资源已收敛；DisposeAsync 不提供该证明。
    Task<OperationResult<Unit>> CloseAsync();
}
