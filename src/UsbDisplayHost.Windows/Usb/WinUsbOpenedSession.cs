using Microsoft.Win32.SafeHandles;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Windows.Usb;

// 一个物理会话最多一个原生请求；Dispose 必须先确认内核及取消回调收敛。
public sealed class WinUsbOpenedSession : IAsyncDisposable
{
    private readonly object syncRoot = new();
    private readonly SafeUsbFileHandle file;
    private readonly SafeWinUsbHandle primary;
    private readonly SafeWinUsbHandle? associated;
    private readonly Action<WinUsbOpenedSession, bool> released;
    private readonly Action revokeIdentity;
    private Task? disposal;
    private readonly WinUsbIoPolicy policy;
    private readonly IErrorReporter reporter;
    private WinUsbOverlappedRequest? active;
    private Task<NativeIoOutcome>? activeTask;
    private bool closing, draining;
    private Task<OperationResult<Unit>>? drainTask;
    private UsbWriteResult? lastIo;

    internal WinUsbOpenedSession(SafeUsbFileHandle file, SafeWinUsbHandle primary, SafeWinUsbHandle? associated,
        DeviceIdentity identity, byte interfaceNumber, byte alternateSetting, byte bulkOutPipe, TransportLimits limits,
        Action<WinUsbOpenedSession, bool> released, Action revokeIdentity, WinUsbIoPolicy policy, IErrorReporter reporter)
    {
        this.file = file; this.primary = primary; this.associated = associated;
        Identity = identity; InterfaceNumber = interfaceNumber; AlternateSetting = alternateSetting;
        BulkOutPipe = bulkOutPipe; Limits = limits;
        this.released = released;
        this.revokeIdentity = revokeIdentity;
        this.policy = policy; this.reporter = reporter;
    }

    public DeviceIdentity Identity { get; }
    public byte InterfaceNumber { get; }
    public byte AlternateSetting { get; }
    public byte BulkOutPipe { get; }
    public TransportLimits Limits { get; }
    public UsbWriteResult? LastIo => Volatile.Read(ref lastIo);

    public async Task<UsbWriteResult> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (data.Length <= 0 || data.Length > Math.Min(245760, Limits.MaximumTransferBytes))
        {
            return new(data.Length, 0, 87, false, true);
        }
        var result = await RunNativeAsync(data, null, policy.WriteTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        return result.Status;
    }

    public async Task<OperationResult<ReadOnlyMemory<byte>>> ControlTransferAsync(ControlSetup setup,
        ReadOnlyMemory<byte> payload, int expectedResponseBytes, CancellationToken cancellationToken)
    {
        bool input = (setup.RequestType & 0x80) != 0;
        int recipient = setup.RequestType & 31;
        int length = input ? expectedResponseBytes : payload.Length;
        if (recipient > 2 || (setup.RequestType & 0x60) == 0x60 || length is < 0 or > 4096
            || (input && (payload.Length != 0 || expectedResponseBytes == 0))
            || (!input && expectedResponseBytes != 0)
            || (recipient == 1 && (setup.Index & 255) != InterfaceNumber))
        {
            return ControlFailure("InvalidControlRequest", "EP0 方向、长度或接口字段无效");
        }
        var packet = new WinUsbNative.SetupPacket { RequestType = setup.RequestType, Request = setup.Request,
            Value = setup.Value, Index = setup.Index, Length = (ushort)length };
        var result = await RunNativeAsync(input ? new byte[length] : payload, packet,
            policy.ControlTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        if (!result.Status.IsCompletionKnown)
        {
            return new OperationResult<ReadOnlyMemory<byte>>.Unknown(AppError.Create("ControlIoUnconfirmed",
                "EP0 完成未知，保留原生请求", "先收敛控制请求，不要重复命令"));
        }
        if (result.Status.WasCancelled && !result.Status.WasTimedOut)
        {
            return result.WasSubmitted
                ? new OperationResult<ReadOnlyMemory<byte>>.Unknown(AppError.Create("ControlCancelledAfterSubmission",
                    "EP0 已取消，但设备效果未知", "核实实际设备状态后再决定是否重试"))
                : new OperationResult<ReadOnlyMemory<byte>>.Cancelled();
        }
        if (result.Status.NativeError is not null || result.Status.CompletedBytes != length)
        {
            if (!input && result.WasSubmitted)
            {
                return new OperationResult<ReadOnlyMemory<byte>>.Unknown(AppError.Create("ControlWriteUnconfirmed",
                    "EP0 写请求未完整完成，设备效果未知", "先核实设备状态，不要直接重复命令"));
            }
            return ControlFailure(result.Status.WasTimedOut ? "ControlTimeout" : "ControlIncomplete",
                "EP0 请求失败或实际传输长度不完整");
        }
        return new OperationResult<ReadOnlyMemory<byte>>.Success(result.Data);
    }

    private async Task<NativeIoOutcome> RunNativeAsync(ReadOnlyMemory<byte> data, WinUsbNative.SetupPacket? setup,
        int timeout, CancellationToken token)
    {
        WinUsbOverlappedRequest request;
        Task<NativeIoOutcome> task;
        try
        {
            lock (syncRoot)
            {
                if (token.IsCancellationRequested) { return new(new(data.Length, 0, 995, true, true), []); }
                if (closing || draining || active is not null) { return new(new(data.Length, 0, 170, false, true), []); }
                var target = setup is { RequestType: var requestType } && (requestType & 31) == 0
                    ? primary : associated ?? primary;
                request = new(file, primary, target, BulkOutPipe, data.ToArray(), setup, reporter);
                active = request;
                activeTask = task = request.RunAsync(timeout, token);
            }
            var result = await task.ConfigureAwait(false);
            if (!result.Status.IsCompletionKnown || result.Status.NativeError is not null) { revokeIdentity(); }
            Volatile.Write(ref lastIo, result.Status);
            lock (syncRoot)
            {
                if (result.Status.IsCompletionKnown && ReferenceEquals(active, request)) { active = null; activeTask = null; }
            }
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reporter.Report(exception, "WinUsbSessionIo");
            revokeIdentity();
            lock (syncRoot) { return new(new(data.Length, 0, 31, false, active is null), []); }
        }
    }

    public Task<OperationResult<Unit>> CancelAndDrainAsync(CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            if (draining) { return drainTask!; }
            draining = true;
            drainTask = DrainOwnedAsync(active, activeTask);
            return drainTask;
        }
    }

    private async Task<OperationResult<Unit>> DrainOwnedAsync(WinUsbOverlappedRequest? request, Task<NativeIoOutcome>? task)
    {
        try
        {
            // 不以调用方取消提前返回；该 API 的责任就是结束正在持有内存的内核请求。
            if (request is null || task is null) { return new OperationResult<Unit>.Success(new Unit()); }
            request.RequestCancellation();
            var result = await task.ConfigureAwait(false);
            if (!result.Status.IsCompletionKnown) { result = await request.ReapAsync().ConfigureAwait(false); }
            Volatile.Write(ref lastIo, result.Status);
            if (!result.Status.IsCompletionKnown)
            {
                return new OperationResult<Unit>.Unknown(AppError.Create("UsbDrainUnconfirmed",
                    "WinUSB 内核请求尚未确认结束", "保留会话所有者并退出应用后重新连接设备"));
            }
            lock (syncRoot)
            {
                if (ReferenceEquals(active, request)) { active = null; activeTask = null; }
            }
            return new OperationResult<Unit>.Success(new Unit());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "WinUsbSessionDrain");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "UsbDrainFailed",
                "WinUSB 收敛未确认，未释放请求", "请退出应用后重新连接所选设备"));
        }
        finally { lock (syncRoot) { draining = false; } }
    }

    private static OperationResult<ReadOnlyMemory<byte>> ControlFailure(string code, string reason) =>
        new OperationResult<ReadOnlyMemory<byte>>.Failure(AppError.Create(code, reason, "请核实固件控制契约和设备状态"));

    public ValueTask DisposeAsync()
    {
        lock (syncRoot)
        {
            closing = true;
            revokeIdentity();
            disposal ??= DisposeOwnedAsync();
            return new ValueTask(disposal);
        }
    }

    private async Task DisposeOwnedAsync()
    {
        bool confirmed = false;
        try
        {
            var drained = await CancelAndDrainAsync(CancellationToken.None).ConfigureAwait(false);
            if (drained is not OperationResult<Unit>.Success) { throw new InvalidOperationException("WinUSB I/O remains unconfirmed."); }
            await Task.Run(() =>
            {
                    associated?.Dispose();
                    if (associated is { ReleaseConfirmed: false }) { throw new IOException("Associated WinUSB release remains unconfirmed.", associated.ReleaseError); }
                    primary.Dispose();
                    if (!primary.ReleaseConfirmed) { throw new IOException("WinUSB release remains unconfirmed.", primary.ReleaseError); }
                    file.Dispose();
                    if (!file.ReleaseConfirmed)
                    {
                        throw new IOException("WinUSB file release remains unconfirmed.", file.ReleaseError);
                    }
                    confirmed = true;
            }).ConfigureAwait(false);
        }
        finally { released(this, confirmed); }
    }
}
