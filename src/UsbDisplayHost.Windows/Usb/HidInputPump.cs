using System.Collections.Immutable;
using System.Threading.Channels;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Windows.Usb;

public sealed record HidInputSnapshot(DeviceKey DeviceKey, long SessionGeneration, long Version,
    long CapturedTimestamp, Ad102V2Status? Status, AppError? Error);

// 控制应答先同步交给控制所有者，再合并供界面使用的最新状态。
public sealed class HidInputPump : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly HidOpenedSession session;
    private readonly DeviceKey deviceKey;
    private readonly long generation;
    private readonly TimeProvider clock;
    private readonly IErrorReporter reporter;
    private readonly Action<Ad102V2Status, ImmutableArray<byte>> controlReportHandler;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<HidInputSnapshot> snapshots = Channel.CreateBounded<HidInputSnapshot>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly HashSet<string> invalidCodes = [];
    private Task? reading;
    private Task? faultCancellation;
    private Task<OperationResult<Unit>>? closing;
    private HidInputSnapshot? current;
    private AppError? terminalError;
    private long rejectedReports, version;
    private bool stopping;

    private HidInputPump(HidOpenedSession session, DeviceKey deviceKey, long generation, TimeProvider clock,
        IErrorReporter reporter, Action<Ad102V2Status, ImmutableArray<byte>> controlReportHandler)
    {
        this.session = session; this.deviceKey = deviceKey; this.generation = generation; this.clock = clock;
        this.reporter = reporter; this.controlReportHandler = controlReportHandler;
    }

    // 仅对调用方显式交付的已核验会话开始读取；不发现、打开或自动接管设备。
    public static OperationResult<HidInputPump> Create(HidOpenedSession session, DeviceKey deviceKey, long generation,
        TimeProvider clock, IErrorReporter reporter, Action<Ad102V2Status, ImmutableArray<byte>> controlReportHandler)
    {
        if (session is null || deviceKey is null || generation <= 0 || clock is null || reporter is null || controlReportHandler is null
            || DeviceKey.FromSerialNumber(session.Identity.VendorId, session.Identity.SerialNumber)
                is not OperationResult<DeviceKey>.Success actual || actual.Value != deviceKey)
        { return Failure("InvalidHidInputOwner", "HID 读取缺少已核验会话、物理归属或必要服务"); }
        HidInputPump? owner = null;
        try
        {
            owner = new(session, deviceKey, generation, clock, reporter, controlReportHandler);
            if (!session.TryAttachInputOwner(owner, owner.InvalidateControlTrust))
            { owner.lifetime.Dispose(); return Failure("HidInputAlreadyOwned", "HID 读取位置已被占用或原会话不能接入"); }
            owner.reading = Task.Run(owner.ReadLoopAsync);
            return new OperationResult<HidInputPump>.Success(owner);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "HidInputStartup");
            // 已接入的所有者仍由会话保留，调用方关闭原会话；不创建替代读取器。
            owner?.Fail(new(report.ErrorId, "HidInputStartupFailed", "HID 读取器启动失败", "关闭原会话并保留诊断"));
            return new OperationResult<HidInputPump>.Failure(new(report.ErrorId, "HidInputStartupFailed",
                "HID 读取器启动失败", "关闭原会话并保留诊断，不创建替代读取器"));
        }
    }

    public HidInputSnapshot? Current => Volatile.Read(ref current);
    public DeviceKey DeviceKey => deviceKey;
    public long SessionGeneration => generation;
    public AppError? TerminalError => Volatile.Read(ref terminalError);
    public long RejectedReports => Interlocked.Read(ref rejectedReports);
    public ChannelReader<HidInputSnapshot> Reader => snapshots.Reader;
    public Task Completion => reading ?? Task.CompletedTask;

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var receipt = await session.ReadOwnedAsync(this, lifetime.Token).ConfigureAwait(false);
                if (lifetime.IsCancellationRequested) { break; }
                if (!session.IsCurrent)
                {
                    Fail(session.TrustError ?? AppError.Create("HidInputSessionExpired", "HID 发现凭据已失效，旧报告未交付",
                        "关闭原会话，重新检测并明确选择设备"));
                    break;
                }
                if (!receipt.Status.IsCompletionKnown || receipt.Status.NativeError is not null)
                {
                    var report = reporter.Report(new IOException($"HID input failed: native={receipt.Status.NativeError}, known={receipt.Status.IsCompletionKnown}."), "HidInputRead");
                    Fail(new(report.ErrorId, "HidInputUnavailable", "HID 状态读取中断，当前状态已失效",
                        "停止控制并关闭原会话，核实设备与诊断后再连接"));
                    break;
                }
                var decoded = receipt.Status.IsComplete && receipt.Data.Length == receipt.Status.CompletedBytes
                    ? session.Codec.DecodeInput(receipt.Data.AsSpan())
                    : new OperationResult<Ad102V2Status>.Failure(AppError.Create("HidShortInput",
                        "HID 输入报告不完整", "核对报告描述符，不补齐报告或更新设备状态"));
                if (decoded is OperationResult<Ad102V2Status>.Failure invalid)
                {
                    Interlocked.Increment(ref rejectedReports);
                    if (invalidCodes.Add(invalid.Error.Code))
                    { reporter.Report(new InvalidDataException(invalid.Error.Reason), "HidInputRejected"); }
                    // 坏报告不更新状态或控制记录；限速避免错误设备持续占用线程与诊断。
                    await Task.Delay(TimeSpan.FromMilliseconds(100), clock, lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                var status = ((OperationResult<Ad102V2Status>.Success)decoded).Value;
                long captured = clock.GetTimestamp();
                lock (sync) { if (stopping) { break; } }
                // 不持有分发锁回调控制层，避免关闭流程与控制锁形成反向锁序。
                controlReportHandler(status, receipt.Data);
                lock (sync)
                {
                    if (stopping) { break; }
                    if (version == long.MaxValue) { throw new InvalidOperationException("HID input version exhausted."); }
                    var snapshot = new HidInputSnapshot(deviceKey, generation, ++version, captured, status, null);
                    Volatile.Write(ref current, snapshot);
                    snapshots.Writer.TryWrite(snapshot);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "HidInputLoop");
            Fail(new(report.ErrorId, "HidInputLoopFailed", "HID 读取或控制应答处理失败，当前状态已失效",
                "停止控制并关闭原会话，保留诊断编号"));
        }
        finally
        {
            lock (sync)
            {
                if (terminalError is null)
                {
                    stopping = true;
                    if (version < long.MaxValue) { version++; }
                    var ended = new HidInputSnapshot(deviceKey, generation, version, 0, null, null);
                    Volatile.Write(ref current, ended);
                    snapshots.Writer.TryWrite(ended);
                }
                snapshots.Writer.TryComplete();
            }
        }
    }

    private void Fail(AppError error)
    {
        lock (sync)
        {
            stopping = true;
            Volatile.Write(ref terminalError, error);
            if (version < long.MaxValue) { version++; }
            var snapshot = new HidInputSnapshot(deviceKey, generation, version, 0, null, error);
            Volatile.Write(ref current, snapshot);
            snapshots.Writer.TryWrite(snapshot);
        }
    }

    private void InvalidateControlTrust(AppError error)
    {
        Fail(error);
        lock (sync) { faultCancellation ??= CancelAfterFaultAsync(); }
    }

    private async Task CancelAfterFaultAsync()
    {
        try { await lifetime.CancelAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { reporter.Report(exception, "HidInputFaultCancellation"); }
    }

    public Task<OperationResult<Unit>> CloseAsync()
    {
        lock (sync)
        {
            stopping = true;
            return closing ??= CloseOwnedAsync();
        }
    }

    private async Task<OperationResult<Unit>> CloseOwnedAsync()
    {
        await Task.Yield();
        try
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            if (reading is not null) { await reading.ConfigureAwait(false); }
            var result = await session.CloseAsync().ConfigureAwait(false);
            Task? originalCancellation;
            lock (sync) { originalCancellation = faultCancellation; }
            if (originalCancellation is not null) { await originalCancellation.ConfigureAwait(false); }
            // 会话关闭无论成功或未知，都不继续将旧状态当作当前设备数据。
            Volatile.Write(ref current, null);
            if (result is OperationResult<Unit>.Success) { lifetime.Dispose(); }
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "HidInputClose");
            Fail(new(report.ErrorId, "HidInputCloseUnconfirmed", "HID 读取器关闭未确认，保留原所有者",
                "保留诊断，不重新打开接口"));
            return new OperationResult<Unit>.Unknown(TerminalError!);
        }
        finally { snapshots.Writer.TryComplete(); }
    }

    public async ValueTask DisposeAsync() => _ = await CloseAsync().ConfigureAwait(false);

    private static OperationResult<HidInputPump> Failure(string code, string reason) =>
        new OperationResult<HidInputPump>.Failure(AppError.Create(code, reason, "确认原会话归属与读取所有者，不重复创建"));
}
