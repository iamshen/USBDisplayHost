using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Sessions;

public sealed record DeviceObservation(DeviceKey DeviceKey, DeviceIdentity Identity, DeviceCapabilities Capabilities,
    DeviceMode Mode, PowerState Power, StreamingState Streaming, int? ObservedBrightness,
    bool DriverVerified, bool SecondaryDisplayPathVerified);

// 仅由应用用例在首帧排队或任何设备控制之前返回；不证明已发送画面可以撤销。
public sealed class DeviceRejectionProof
{
    internal DeviceRejectionProof(OperationContext context) { Context = context; }
    public OperationContext Context { get; }
}

// 用例负责渲染、传输与持久化；协调器只授予冻结上下文和发布实际观察。
public interface IDeviceSessionWork
{
    Task<OperationResult<DeviceObservation>> ExecuteAsync(OperationContext context, CancellationToken cancellationToken);
    // 包括内核 I/O 和取消回调收敛；完成前不得释放缓冲或让下一个操作开始。
    Task<OperationResult<Unit>> DrainAsync();
    // 未提供凭据的失败仍视为可能影响设备；不能依据错误编号猜测是否执行过 I/O。
    DeviceRejectionProof? RejectionBeforeIo => null;
}

public sealed class DeviceSnapshotSubscription : IAsyncDisposable
{
    private readonly Action detach;
    internal DeviceSnapshotSubscription(System.Threading.Channels.ChannelReader<DeviceSnapshot> reader, Action detach)
    {
        Reader = reader;
        this.detach = detach;
    }
    public System.Threading.Channels.ChannelReader<DeviceSnapshot> Reader { get; }
    private int disposed;
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) { detach(); }
        return ValueTask.CompletedTask;
    }
}
