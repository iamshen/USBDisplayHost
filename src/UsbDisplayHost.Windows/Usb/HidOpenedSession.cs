using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Windows.Usb;

public sealed record HidReportReceipt(UsbWriteResult Status, ImmutableArray<byte> Data, bool WasSubmitted);
public sealed record HidFeatureReceipt(Ad102V2HidCodec Codec, long CapturedTimestamp);

// 文件只由核验过同一身份、collection 和 Feature 的探测所有者交付。
public sealed class HidOpenedSession : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly SafeUsbFileHandle file;
    private readonly IErrorReporter reporter;
    private readonly Action released;
    private readonly Func<bool> isCurrent;
    private readonly TimeProvider clock;
    private readonly int timeoutMilliseconds;
    private HidOverlappedRequest? input, output;
    private Task<HidIoOutcome>? inputTask, outputTask;
    private Task<OperationResult<Unit>>? drain, close;
    private AppError? resourceUnknown;
    private HidReportReceipt? lastWriteReceipt;
    private object? inputOwner;
    private Action<AppError>? invalidateInput;
    private object? outputOwner;
    private Task<OperationResult<HidFeatureReceipt>>? featureTask;
    private AppError? controlTrustError;
    private bool draining, closing, queryingFeature;

    internal HidOpenedSession(SafeUsbFileHandle file, DeviceIdentity identity, Ad102V2HidCodec codec,
        TimeProvider clock, int timeoutMilliseconds, IErrorReporter reporter, Action released, Func<bool> isCurrent)
    {
        this.file = file; Identity = identity; Codec = codec; this.clock = clock;
        this.timeoutMilliseconds = timeoutMilliseconds; this.reporter = reporter; this.released = released;
        this.isCurrent = isCurrent;
    }

    public DeviceIdentity Identity { get; }
    public Ad102V2HidCodec Codec { get; }
    public HidReportReceipt? LastWriteReceipt => Volatile.Read(ref lastWriteReceipt);
    internal AppError? TrustError => Volatile.Read(ref controlTrustError);
    internal bool IsCurrent => !Volatile.Read(ref closing) && TrustError is null && isCurrent();

    internal bool TryAttachOutputOwner(object owner)
    {
        lock (sync)
        {
            if (outputOwner is not null || output is not null || closing || draining || queryingFeature
                || resourceUnknown is not null || controlTrustError is not null) { return false; }
            outputOwner = owner;
            return true;
        }
    }

    internal bool TryAttachInputOwner(object owner, Action<AppError> onInvalidated)
    {
        lock (sync)
        {
            if (inputOwner is not null || input is not null || closing || draining
                || resourceUnknown is not null || controlTrustError is not null) { return false; }
            inputOwner = owner;
            invalidateInput = onInvalidated;
            return true;
        }
    }

    internal Task<HidReportReceipt> ReadOwnedAsync(object owner, CancellationToken token) =>
        RunAsync(HidIoDirection.Input, default, token, owner);

    public Task<OperationResult<HidFeatureReceipt>> QueryFeatureAsync(CancellationToken token) =>
        QueryFeatureAsync(null, token);

    internal Task<OperationResult<HidFeatureReceipt>> QueryFeatureOwnedAsync(object owner, CancellationToken token) =>
        QueryFeatureAsync(owner, token);

    private Task<OperationResult<HidFeatureReceipt>> QueryFeatureAsync(object? owner, CancellationToken token)
    {
        try
        {
            lock (sync)
            {
                if (token.IsCancellationRequested)
                { return Task.FromResult<OperationResult<HidFeatureReceipt>>(new OperationResult<HidFeatureReceipt>.Cancelled()); }
                if (resourceUnknown is { } unknown)
                { return Task.FromResult<OperationResult<HidFeatureReceipt>>(new OperationResult<HidFeatureReceipt>.Unknown(unknown)); }
                if (controlTrustError is { } invalidated)
                { return Task.FromResult<OperationResult<HidFeatureReceipt>>(new OperationResult<HidFeatureReceipt>.Failure(invalidated)); }
                if (closing || draining || queryingFeature || output is not null || !ReferenceEquals(outputOwner, owner))
                { return FeatureFailure("HidFeatureBusy", "控制写入或 Feature 查询仍由原所有者持有"); }
                if (!isCurrent()) { return FeatureFailure("HidFeatureSessionExpired", "HID 发现凭据已失效，未查询旧接口"); }
                queryingFeature = true;
                return featureTask = QueryFeatureOwnedCoreAsync(token);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "HidFeatureAdmission");
            return Task.FromResult<OperationResult<HidFeatureReceipt>>(new OperationResult<HidFeatureReceipt>.Failure(
                new(report.ErrorId, "HidFeatureAdmissionFailed", "HID Feature 查询未能受理", "保留诊断并核实会话归属")));
        }
    }

    private async Task<OperationResult<HidFeatureReceipt>> QueryFeatureOwnedCoreAsync(CancellationToken token)
    {
        try
        {
            // 不取消等待任务：同步驱动调用返回并完成解析后，才结束文件所有权。
            return await Task.Run<OperationResult<HidFeatureReceipt>>(() =>
            {
                token.ThrowIfCancellationRequested();
                if (!IsCurrent)
                { return new OperationResult<HidFeatureReceipt>.Failure(AppError.Create("HidFeatureSessionExpired",
                    "Feature 原生查询前会话已经失效", "重新检测并核实当前接口")); }
                byte[] report = new byte[Ad102V2HidCodec.ReportBytes];
                Array.Fill(report, (byte)0xA5);
                report[0] = 3;
                if (!HidIdentityNative.HidD_GetFeature(file, report, (uint)report.Length))
                { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
                long captured = clock.GetTimestamp();
                token.ThrowIfCancellationRequested();
                var decoded = Ad102V2HidCodec.Create(report);
                if (decoded is OperationResult<Ad102V2HidCodec>.Failure invalid)
                {
                    InvalidateControlTrust(invalid.Error);
                    return new OperationResult<HidFeatureReceipt>.Failure(invalid.Error);
                }
                var actual = ((OperationResult<Ad102V2HidCodec>.Success)decoded).Value;
                if (actual.Feature.DeclaredCapabilities != Codec.Feature.DeclaredCapabilities)
                {
                    var error = AppError.Create("HidFeatureCapabilitiesChanged",
                        "Feature 能力声明与打开时不一致，控制能力已失效", "关闭原会话，重新核实描述符和固件");
                    InvalidateControlTrust(error);
                    return new OperationResult<HidFeatureReceipt>.Failure(error);
                }
                if (!IsCurrent)
                { return new OperationResult<HidFeatureReceipt>.Failure(AppError.Create("HidFeatureExpiredBeforeDelivery",
                    "Feature 交付前发现凭据已经失效", "重新枚举并核实同一设备，不使用旧结果")); }
                return new OperationResult<HidFeatureReceipt>.Success(new(actual, captured));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new OperationResult<HidFeatureReceipt>.Cancelled(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "HidSessionFeatureQuery");
            return new OperationResult<HidFeatureReceipt>.Failure(new(report.ErrorId, "HidSessionFeatureFailed",
                "HID Feature 重查失败，状态未更新", "保留原控制结果并查看诊断，不自动重发模式命令"));
        }
        finally { lock (sync) { queryingFeature = false; } }
    }

    private static Task<OperationResult<HidFeatureReceipt>> FeatureFailure(string code, string reason) =>
        Task.FromResult<OperationResult<HidFeatureReceipt>>(new OperationResult<HidFeatureReceipt>.Failure(
            AppError.Create(code, reason, "等待原请求收敛并核实当前文件归属，不重复查询或控制")));

    private void InvalidateControlTrust(AppError error)
    {
        Volatile.Write(ref controlTrustError, error);
        // 不持有会话锁通知读取所有者，避免旧快照在等下一个 Input 时继续有效。
        Volatile.Read(ref invalidateInput)?.Invoke(error);
    }

    public Task<HidReportReceipt> ReadAsync(CancellationToken token) => RunAsync(HidIoDirection.Input, default, token);

    public Task<HidReportReceipt> WriteModeReportAsync(ReadOnlyMemory<byte> report, CancellationToken token) =>
        WriteReportAsync(report, token, null);

    internal Task<HidReportReceipt> WriteOwnedAsync(object owner, ReadOnlyMemory<byte> report, CancellationToken token) =>
        WriteReportAsync(report, token, owner);

    private Task<HidReportReceipt> WriteReportAsync(ReadOnlyMemory<byte> report, CancellationToken token, object? writerOwner)
    {
        try
        {
            if (report.Length != Ad102V2HidCodec.ReportBytes)
            { return Task.FromResult(new HidReportReceipt(new(report.Length, 0, 87, false, true), [], false)); }
            // 验证与原生写入使用同一私有快照，调用方后续改动不能替换已核对命令。
            byte[] frozen = report.ToArray();
            var bytes = frozen.AsSpan();
            bool mode = bytes[1] == 0x31 && bytes[2] <= 1 && bytes[3] is > 0 and < 255;
            bool probe = bytes[1] == Ad102V2HidCodec.StatusProbeCommand && bytes[2] == 0
                && bytes[3] == Ad102V2HidCodec.StatusProbeToken;
            if (bytes[0] != 2 || !(mode || probe)
                || bytes[4..].ContainsAnyExcept((byte)0))
            { return Task.FromResult(new HidReportReceipt(new(report.Length, 0, 87, false, true), [], false)); }
            return RunAsync(HidIoDirection.Output, frozen, token, writerOwner);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reporter.Report(exception, "HidModeReportPreparation");
            return Task.FromResult(Receipt(31, false, true));
        }
    }

    private async Task<HidReportReceipt> RunAsync(HidIoDirection direction, ReadOnlyMemory<byte> bytes, CancellationToken token,
        object? ioOwner = null)
    {
        HidOverlappedRequest? request = null;
        try
        {
            Task<HidIoOutcome> task;
            lock (sync)
            {
                if (token.IsCancellationRequested) { return Receipt(995, true, true); }
                if (!ReferenceEquals(direction == HidIoDirection.Input ? inputOwner : outputOwner, ioOwner))
                { return Receipt(170, false, true); }
                if (!IsCurrent) { return Receipt(1167, false, true); }
                if (resourceUnknown is not null) { return Receipt(31, false, false); }
                if (closing || draining || direction == HidIoDirection.Output && queryingFeature
                    || (direction == HidIoDirection.Input ? input : output) is not null)
                { return Receipt(170, false, true); }
                var created = HidOverlappedRequest.Create(file, direction, Ad102V2HidCodec.ReportBytes, bytes, reporter,
                    () => !Volatile.Read(ref draining) && IsCurrent);
                if (created is OperationResult<HidOverlappedRequest>.Unknown uncertain)
                { resourceUnknown = uncertain.Error; return Receipt(31, false, false); }
                if (created is not OperationResult<HidOverlappedRequest>.Success valid) { return Receipt(31, false, true); }
                request = valid.Value;
                // 先保存请求，受理异常也不能遗失原生资源所有者。
                if (direction == HidIoDirection.Input) { input = request; }
                else { output = request; Volatile.Write(ref lastWriteReceipt, null); }
                task = request.RunAsync(direction == HidIoDirection.Input ? Timeout.Infinite : timeoutMilliseconds, clock, token);
                if (direction == HidIoDirection.Input) { inputTask = task; }
                else { outputTask = task; }
            }
            var result = await task.ConfigureAwait(false);
            if (direction == HidIoDirection.Output)
            { RecordWriteReceipt(request, result); }
            if (result.Status.IsCompletionKnown) { Clear(direction, request); }
            return new(result.Status, result.Data, result.WasSubmitted);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reporter.Report(exception, "HidSessionIo");
            return new(new(Ad102V2HidCodec.ReportBytes, 0, 31, false, request is null), [], request is not null);
        }
    }

    private static HidReportReceipt Receipt(int error, bool cancelled, bool known) =>
        new(new(Ad102V2HidCodec.ReportBytes, 0, error, cancelled, known), [], false);

    private void RecordWriteReceipt(HidOverlappedRequest request, HidIoOutcome result)
    {
        lock (sync)
        {
            // 原观察任务的迟到续体不能覆盖收敛后的最终收据或下一条请求。
            if (!ReferenceEquals(output, request)
                || lastWriteReceipt is { Status.IsCompletionKnown: true } && !result.Status.IsCompletionKnown) { return; }
            Volatile.Write(ref lastWriteReceipt, new(result.Status, result.Data, result.WasSubmitted));
        }
    }

    private void Clear(HidIoDirection direction, HidOverlappedRequest request)
    {
        lock (sync)
        {
            if (direction == HidIoDirection.Input && ReferenceEquals(input, request)) { input = null; inputTask = null; }
            if (direction == HidIoDirection.Output && ReferenceEquals(output, request)) { output = null; outputTask = null; }
        }
    }

    public Task<OperationResult<Unit>> CancelAndDrainAsync()
    {
        lock (sync)
        {
            if (draining) { return drain!; }
            draining = true;
            return drain = DrainAsync(input, inputTask, output, outputTask, featureTask);
        }
    }

    private async Task<OperationResult<Unit>> DrainAsync(HidOverlappedRequest? read, Task<HidIoOutcome>? readTask,
        HidOverlappedRequest? write, Task<HidIoOutcome>? writeTask, Task<OperationResult<HidFeatureReceipt>>? query)
    {
        // 先取消两个方向，不能让等待读取阻止写入取消。
        await Task.Yield();
        try
        {
            read?.RequestCancellation(); write?.RequestCancellation();
            var results = await Task.WhenAll(ReapAsync(read, readTask), ReapAsync(write, writeTask)).ConfigureAwait(false);
            if (query is not null) { await query.ConfigureAwait(false); }
            if (write is not null && results[1] is { } actualWrite) { RecordWriteReceipt(write, actualWrite); }
            if (resourceUnknown is { } unknown) { return new OperationResult<Unit>.Unknown(unknown); }
            if (results.Any(result => result is not null && !result.Status.IsCompletionKnown))
            { return Unconfirmed("HidDrainUnconfirmed", "HID 原生读写尚未确认结束"); }
            if (read is not null) { Clear(HidIoDirection.Input, read); }
            if (write is not null) { Clear(HidIoDirection.Output, write); }
            return new OperationResult<Unit>.Success(new());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "HidSessionDrain");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "HidDrainFailed",
                "HID 收敛未确认，保留会话所有者", "保留诊断，不重复打开接口或提交控制命令"));
        }
        finally { lock (sync) { draining = false; } }
    }

    private static async Task<HidIoOutcome?> ReapAsync(HidOverlappedRequest? request, Task<HidIoOutcome>? task)
    {
        if (request is null) { return null; }
        var result = task is null ? null : await task.ConfigureAwait(false);
        return result is { Status.IsCompletionKnown: true } ? result : await request.ReapAsync().ConfigureAwait(false);
    }

    public Task<OperationResult<Unit>> CloseAsync()
    {
        lock (sync)
        {
            closing = true;
            return close ??= CloseOwnedAsync();
        }
    }

    private async Task<OperationResult<Unit>> CloseOwnedAsync()
    {
        try
        {
            var drained = await CancelAndDrainAsync().ConfigureAwait(false);
            if (drained is not OperationResult<Unit>.Success) { return drained; }
            await Task.Run(file.Dispose).ConfigureAwait(false);
            if (!file.ReleaseConfirmed)
            {
                var report = reporter.Report(file.ReleaseError ?? new IOException("HID file close unconfirmed."), "HidSessionClose");
                return new OperationResult<Unit>.Unknown(new(report.ErrorId, "HidCloseUnconfirmed",
                    "HID 文件关闭未确认，保留路径所有者", "保留诊断并退出应用，不创建替代会话"));
            }
            released();
            return new OperationResult<Unit>.Success(new());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "HidSessionClose");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "HidCloseFailed",
                "HID 会话关闭未确认", "保留原所有者及诊断，不重新打开接口"));
        }
    }

    public async ValueTask DisposeAsync() => _ = await CloseAsync().ConfigureAwait(false);

    private static OperationResult<Unit> Unconfirmed(string code, string reason) =>
        new OperationResult<Unit>.Unknown(AppError.Create(code, reason, "保留原所有者，不提前释放句柄或重复打开"));
}
