using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Windows.Usb;

// Desktop 组合时仅向 Core 会话提供 IDisplayTransport，不向 ViewModel 暴露原生会话。
public sealed class WinUsbDisplayTransport : IDisplayTransport
{
    private readonly object syncRoot = new();
    private readonly WinUsbSessionFactory factory;
    private readonly WinUsbBinding binding;
    private readonly WinUsbIoPolicy policy;
    private readonly DeviceSelectionAdmission admission;
    private readonly IErrorReporter reporter;
    private WinUsbOpenedSession? session;
    private Task<OperationResult<TransportLimits>>? opening;
    private Task? disposal;
    private Task<OperationResult<Unit>>? drainTask;
    private CancellationTokenSource? openingCancellation;
    private AppError? unconfirmedDrain;
    private bool closing, draining;
    public byte? InterfaceNumber { get { lock (syncRoot) { return session?.InterfaceNumber; } } }

    public WinUsbDisplayTransport(DeviceSelectionAdmission admission, WinUsbBinding binding, WinUsbIoPolicy policy, IErrorReporter reporter)
    {
        this.admission = admission; this.binding = binding; this.policy = policy; this.reporter = reporter; factory = new(reporter);
    }

    public Task<OperationResult<TransportLimits>> OpenAsync(DeviceIdentity identity, CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            if (!closing && !draining && unconfirmedDrain is null && session is not null
                && session.Identity == identity && admission.Validity.IsCurrent(admission.Selection))
            { return Task.FromResult<OperationResult<TransportLimits>>(new OperationResult<TransportLimits>.Success(session.Limits)); }
            if (closing || draining || unconfirmedDrain is not null || session is not null || opening is { IsCompleted: false })
            {
                return Task.FromResult<OperationResult<TransportLimits>>(new OperationResult<TransportLimits>.Failure(
                    AppError.Create("UsbOpenUnavailable", "传输正在打开、已经打开或正在退出", "请等待当前操作结束，不重复打开")));
            }
            try
            {
                openingCancellation?.Dispose();
                openingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                opening = OpenOwnedAsync(identity, openingCancellation.Token);
                return opening;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = reporter.Report(exception, "WinUsbOpenAcceptance");
                return Task.FromResult<OperationResult<TransportLimits>>(new OperationResult<TransportLimits>.Failure(
                    new(report.ErrorId, "UsbOpenAcceptanceFailed", "传输打开未能受理", "请检查诊断后重新确认设备")));
            }
        }
    }

    private async Task<OperationResult<TransportLimits>> OpenOwnedAsync(DeviceIdentity identity, CancellationToken cancellationToken)
    {
        var opened = await factory.OpenAsync(admission, identity, binding, policy, cancellationToken).ConfigureAwait(false);
        if (opened is OperationResult<WinUsbOpenedSession>.Success success)
        {
            lock (syncRoot) { session = success.Value; }
            // 即使退出已开始也保留成功所有者，Dispose 会等此打开工作后收敛和释放。
            return new OperationResult<TransportLimits>.Success(success.Value.Limits);
        }
        return opened switch
        {
            OperationResult<WinUsbOpenedSession>.Failure failure => new OperationResult<TransportLimits>.Failure(failure.Error),
            OperationResult<WinUsbOpenedSession>.Unknown unknown => new OperationResult<TransportLimits>.Unknown(unknown.Error),
            _ => new OperationResult<TransportLimits>.Cancelled()
        };
    }

    public Task<UsbWriteResult> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            return !closing && !draining && unconfirmedDrain is null && session is { } active ? active.WriteAsync(data, cancellationToken)
                : Task.FromResult(new UsbWriteResult(data.Length, 0, 6, false, true));
        }
    }

    public Task<OperationResult<ReadOnlyMemory<byte>>> ControlTransferAsync(ControlSetup setup,
        ReadOnlyMemory<byte> payload, int expectedResponseBytes, CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            return !closing && !draining && unconfirmedDrain is null && session is { } active
                ? active.ControlTransferAsync(setup, payload, expectedResponseBytes, cancellationToken)
                : Task.FromResult<OperationResult<ReadOnlyMemory<byte>>>(new OperationResult<ReadOnlyMemory<byte>>.Failure(
                    AppError.Create("UsbControlUnavailable", "USB 控制通道未打开或正在退出", "请连接并核实所选设备")));
        }
    }

    public Task<OperationResult<Unit>> CancelAndDrainAsync(CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            if (draining) { return drainTask!; }
            if (disposal is { IsCompletedSuccessfully: true })
            { return Task.FromResult<OperationResult<Unit>>(new OperationResult<Unit>.Success(new())); }
            draining = true;
            return drainTask = DrainOwnedAsync(opening, openingCancellation);
        }
    }

    private async Task<OperationResult<Unit>> DrainOwnedAsync(Task<OperationResult<TransportLimits>>? open,
        CancellationTokenSource? cancellation)
    {
        try
        {
            // 取消请求不丢弃正在打开的 Task；即使取消回调失败，也先等打开结算。
            Exception? cancellationFailure = null;
            try { if (open is { IsCompleted: false } && cancellation is not null) { await cancellation.CancelAsync().ConfigureAwait(false); } }
            catch (Exception exception) when (exception is not OutOfMemoryException) { cancellationFailure = exception; }
            OperationResult<TransportLimits>? opened = open is null ? null : await open.ConfigureAwait(false);
            if (factory.RequiresProcessRestart || opened is OperationResult<TransportLimits>.Unknown)
            {
                var error = opened is OperationResult<TransportLimits>.Unknown unknown ? unknown.Error
                    : AppError.Create("UsbOwnerUnconfirmed", "原生打开或释放未确认", "请保留传输所有者，不能重复打开");
                lock (syncRoot) { unconfirmedDrain = error; }
                return new OperationResult<Unit>.Unknown(error);
            }
            WinUsbOpenedSession? owned;
            lock (syncRoot) { owned = session; }
            var drained = owned is null ? new OperationResult<Unit>.Success(new())
                : await owned.CancelAndDrainAsync(CancellationToken.None).ConfigureAwait(false);
            if (drained is not OperationResult<Unit>.Success)
            {
                var error = drained switch
                {
                    OperationResult<Unit>.Unknown unknown => unknown.Error,
                    OperationResult<Unit>.Failure failure => failure.Error,
                    _ => AppError.Create("UsbDrainUnconfirmed", "取消未证明原生请求已经结束", "请保留传输所有者")
                };
                lock (syncRoot) { unconfirmedDrain = error; }
                return new OperationResult<Unit>.Unknown(error);
            }
            if (cancellationFailure is not null) { return DrainFailure(cancellationFailure); }
            lock (syncRoot) { unconfirmedDrain = null; }
            return new OperationResult<Unit>.Success(new());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return DrainFailure(exception); }
        finally { lock (syncRoot) { draining = false; } }
    }

    private OperationResult<Unit> DrainFailure(Exception exception)
    {
        var report = reporter.Report(exception, "WinUsbTransportDrain");
        var error = new AppError(report.ErrorId, "UsbTransportDrainUnconfirmed", "传输打开或取消收敛未确认，所有者已保留",
            "请检查诊断，不要重发请求或打开替代会话");
        lock (syncRoot) { unconfirmedDrain = error; }
        return new OperationResult<Unit>.Unknown(error);
    }

    public ValueTask DisposeAsync()
    {
        lock (syncRoot)
        {
            closing = true;
            disposal ??= DisposeOwnedAsync();
            return new ValueTask(disposal);
        }
    }

    private async Task DisposeOwnedAsync()
    {
        var drained = await CancelAndDrainAsync(CancellationToken.None).ConfigureAwait(false);
        if (drained is not OperationResult<Unit>.Success)
        { throw new IOException("WinUSB transport drain remains unconfirmed."); }
        WinUsbOpenedSession? owned;
        lock (syncRoot) { owned = session; }
        if (owned is not null) { await owned.DisposeAsync().ConfigureAwait(false); }
        if (factory.RequiresProcessRestart) { throw new InvalidOperationException("WinUSB owner release remains unconfirmed."); }
        lock (syncRoot) { session = null; openingCancellation?.Dispose(); openingCancellation = null; }
    }
}
