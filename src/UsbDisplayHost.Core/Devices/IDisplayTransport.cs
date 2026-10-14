using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Devices;

public sealed record TransportLimits(int MaximumPacketBytes, int MaximumTransferBytes);
public readonly record struct ControlSetup(byte RequestType, byte Request, ushort Value, ushort Index);
public sealed record UsbWriteResult(int RequestedBytes, int CompletedBytes, int? NativeError,
    bool WasCancelled, bool IsCompletionKnown, bool WasTimedOut = false)
{
    public bool IsComplete => IsCompletionKnown && !WasCancelled && !WasTimedOut && NativeError is null
        && RequestedBytes > 0 && CompletedBytes == RequestedBytes;
}

public interface IDisplayTransport : IAsyncDisposable
{
    Task<OperationResult<TransportLimits>> OpenAsync(DeviceIdentity identity, CancellationToken cancellationToken);
    // 适配器按已冻结策略设置写超时；实际完成长度、超时和取消不能折算为完整写入。
    // IsCompletionKnown=false 时可能仍持有该内存，必须经 CancelAndDrainAsync 确认后才可重用。
    Task<UsbWriteResult> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
    Task<OperationResult<ReadOnlyMemory<byte>>> ControlTransferAsync(
        ControlSetup setup, ReadOnlyMemory<byte> payload, int expectedResponseBytes, CancellationToken cancellationToken);
    // 返回前须完成取消与内核请求收敛，调用方才能释放帧缓冲。
    Task<OperationResult<Unit>> CancelAndDrainAsync(CancellationToken cancellationToken);
}
