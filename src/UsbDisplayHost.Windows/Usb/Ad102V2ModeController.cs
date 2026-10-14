using System.Collections.Immutable;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;

namespace UsbDisplayHost.Windows.Usb;

// 仅负责原会话发送与应答关联；目标可用须由重枚举和目标核验另行证明。
public sealed class Ad102V2ModeController : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly HidOpenedSession session;
    private readonly Ad102V2ModeControlLedger ledger;
    private readonly TimeProvider clock;
    private readonly IErrorReporter reporter;
    private HidInputPump? input;
    private TaskCompletionSource<OperationResult<Ad102V2ModeAcknowledgement>>? acknowledgement;
    private OperationContext? acknowledgementContext;
    private ImmutableArray<byte> lastAcknowledgement = [];
    private Task<OperationResult<Unit>>? closing;
    private Task<OperationResult<Unit>>? writeReceipt;
    private object? channelOwner;
    private bool executing, retired, baselineEstablished;
    private TaskCompletionSource<Ad102V2Status>? statusQuery;
    private bool statusUnconfirmed;

    public async Task<OperationResult<Ad102V2Status>> QueryStatusAsync(CancellationToken token)
    {
        TaskCompletionSource<Ad102V2Status> reply;
        lock (sync)
        {
            if (session.Codec.Feature.DeclaredCapabilities != 0x15F || retired || executing || statusUnconfirmed
                || statusQuery is not null || ledger.Pending is not null || !session.IsCurrent)
            { return Failure<Ad102V2Status>("V2StatusBusy", "原控制请求尚未结束或状态查询未确认"); }
            statusQuery = reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            var write = await session.WriteOwnedAsync(this, session.Codec.CreateStatusProbe(), token).ConfigureAwait(false);
            if (!write.Status.IsComplete)
            {
                lock (sync) { statusUnconfirmed = write.WasSubmitted; }
                return Failure<Ad102V2Status>("V2StatusWriteFailed", "状态探测写入未完整完成，请重新连接");
            }
            var status = await reply.Task.WaitAsync(TimeSpan.FromSeconds(2), clock, token).ConfigureAwait(false);
            // token 与旧命令冲突时固件仍回传状态；查询不会执行模式或复位。
            return new OperationResult<Ad102V2Status>.Success(status);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            lock (sync) { statusUnconfirmed = true; }
            return new OperationResult<Ad102V2Status>.Unknown(AppError.Create("V2StatusUnconfirmed",
                "状态探测未收到关联报告", "重新连接后核实状态，不把旧报告当作当前读回"));
        }
        finally { lock (sync) { statusQuery = null; } }
    }

    private Ad102V2ModeController(HidOpenedSession session, Ad102V2ModeControlLedger ledger, TimeProvider clock, IErrorReporter reporter)
    { this.session = session; this.ledger = ledger; this.clock = clock; this.reporter = reporter; }

    public static OperationResult<Ad102V2ModeController> Create(HidOpenedSession session, DeviceKey deviceKey, long generation,
        DeviceCapabilities capabilities, TimeProvider clock, IErrorReporter reporter)
    {
        if (session is null || capabilities is null || !capabilities.IsProtocolMatched
            || !capabilities.Commands.Contains(DeviceCommand.SwitchMode)
            || DeviceKey.FromSerialNumber(session.Identity.VendorId, session.Identity.SerialNumber)
                is not OperationResult<DeviceKey>.Success actual || actual.Value != deviceKey)
        { return Failure<Ad102V2ModeController>("V2ModeCapabilityUnverified", "当前会话缺少已验证的模式切换能力或物理归属"); }
        var created = Ad102V2ModeControlLedger.Create(deviceKey, generation, session.Codec, clock, reporter);
        if (created is OperationResult<Ad102V2ModeControlLedger>.Failure invalid)
        { return new OperationResult<Ad102V2ModeController>.Failure(invalid.Error); }
        var owner = new Ad102V2ModeController(session, ((OperationResult<Ad102V2ModeControlLedger>.Success)created).Value, clock, reporter);
        if (!session.TryAttachOutputOwner(owner))
        { return Failure<Ad102V2ModeController>("V2ModeWriterAlreadyOwned", "控制写入已有所有者或会话不能接入"); }
        var reading = HidInputPump.Create(session, deviceKey, generation, clock, reporter, owner.HandleReport);
        if (reading is OperationResult<HidInputPump>.Success valid)
        { owner.input = valid.Value; return new OperationResult<Ad102V2ModeController>.Success(owner); }
        owner.Retire();
        return reading is OperationResult<HidInputPump>.Unknown uncertain
            ? new OperationResult<Ad102V2ModeController>.Unknown(uncertain.Error)
            : Failure<Ad102V2ModeController>("V2ModeInputUnavailable", "控制读取未能接入，写入所有者仍保留在原会话");
    }

    public HidInputPump Input => input!;
    internal bool TryAttachChannelOwner(object owner)
    {
        lock (sync)
        {
            if (channelOwner is not null || retired || executing || ledger.Pending is not null) { return false; }
            channelOwner = owner;
            return true;
        }
    }
    public Ad102V2ModeRequest? Pending => ledger.Pending;
    internal Task<OperationResult<Ad102V2ModeAcknowledgement>>? CaptureAcknowledgement(OperationContext context)
    {
        lock (sync)
        {
            return acknowledgementContext == context && acknowledgement is not null
                && (acknowledgement.Task.IsCompleted || ledger.Pending?.Context == context)
                ? acknowledgement.Task : null;
        }
    }
    internal bool CanDeliverAcknowledgement(OperationContext context)
    {
        lock (sync) { return !retired && acknowledgementContext == context && session.IsCurrent; }
    }
    public Task<OperationResult<HidFeatureReceipt>> QueryFeatureAsync(CancellationToken token)
    {
        lock (sync)
        {
            if (retired || executing)
            {
                return Task.FromResult(Failure<HidFeatureReceipt>("V2ModeFeatureUnavailable",
                    "模式执行者仍在等待或原控制会话已失效，未查询旧 Feature"));
            }
            return session.QueryFeatureOwnedAsync(this, token);
        }
    }
    public OperationResult<Ad102V2ModeAcknowledgement>? LastAcknowledgement
    {
        get
        {
            lock (sync)
            {
                return acknowledgement?.Task.IsCompletedSuccessfully == true ? acknowledgement.Task.Result : null;
            }
        }
    }

    public async Task<OperationResult<Ad102V2ModeAcknowledgement>> SwitchAsync(OperationContext context, DeviceMode target,
        PowerState powerIntent, CancellationToken token)
    {
        var currentStatus = await QueryStatusAsync(token).ConfigureAwait(false);
        if (currentStatus is not OperationResult<Ad102V2Status>.Success queriedStatus)
        { return Failure<Ad102V2ModeAcknowledgement>("V2ModeStatusUnavailable", "切换前状态查询未完成"); }
        Ad102V2ModeRequest? request = null;
        CancellationTokenSource? deadlineCancellation = null;
        bool entered = false, beganWrite = false;
        try
        {
            Task<OperationResult<Ad102V2ModeAcknowledgement>> observed;
            lock (sync)
            {
                if (token.IsCancellationRequested) { return new OperationResult<Ad102V2ModeAcknowledgement>.Cancelled(); }
                if (retired || executing || !session.IsCurrent || input?.TerminalError is not null || input?.Completion.IsCompleted != false)
                { return Failure<Ad102V2ModeAcknowledgement>("V2ModeControlUnavailable", "控制会话已失效、读取中断或已有切换执行者"); }
                var baseline = queriedStatus.Value;
                if (baseline.MaintenanceBusy || baseline.Switching
                    || baseline.CurrentMode != baseline.TargetMode || baseline.CurrentMode != session.Codec.Feature.CurrentMode)
                { return Failure<Ad102V2ModeAcknowledgement>("V2ModeInputBaselineUnavailable", "尚无当前稳定 Input 基线，未批准模式控制"); }
                if (!baselineEstablished)
                {
                    var reserved = ledger.ReserveObservedToken(baseline.LastToken);
                    if (reserved is OperationResult<Unit>.Failure invalidBaseline)
                    { return new OperationResult<Ad102V2ModeAcknowledgement>.Failure(invalidBaseline.Error); }
                    baselineEstablished = true;
                }
                var prepared = ledger.Prepare(context, target, powerIntent);
                if (prepared is OperationResult<Ad102V2ModeRequest>.Failure invalid)
                { return new OperationResult<Ad102V2ModeAcknowledgement>.Failure(invalid.Error); }
                request = ((OperationResult<Ad102V2ModeRequest>.Success)prepared).Value;
                executing = entered = true;
                lastAcknowledgement = [];
                acknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
                acknowledgementContext = context;
                observed = acknowledgement.Task;
            }
            deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var deadline = Task.Delay(Ad102V2ModeControlLedger.NormalWaitBudget, clock, deadlineCancellation.Token);
            Task<OperationResult<Unit>> receiptTask;
            lock (sync)
            {
                if (retired || token.IsCancellationRequested)
                {
                    ledger.CancelBeforeWrite(context);
                    return new OperationResult<Ad102V2ModeAcknowledgement>.Cancelled();
                }
                var started = ledger.BeginWrite(context);
                if (started is OperationResult<Unit>.Failure failed)
                { ledger.CancelBeforeWrite(context); return new OperationResult<Ad102V2ModeAcknowledgement>.Failure(failed.Error); }
                beganWrite = true;
                writeReceipt = receiptTask = WriteAndRecordAsync(request, token);
            }
            await Task.WhenAny(receiptTask, deadline, input!.Completion).ConfigureAwait(false);
            if (!receiptTask.IsCompleted)
            { return Unknown("V2ModeWriteStillOwned", "等待窗口已结束或读取中断，原模式写入仍由控制所有者持有"); }
            var recorded = await receiptTask.ConfigureAwait(false);
            if (recorded is not OperationResult<Unit>.Success)
            { return Unknown("V2ModeWriteUnconfirmed", "原模式写入未完整确认，禁止重发并等待原 I/O 收敛"); }
            await Task.WhenAny(observed, deadline, input!.Completion).ConfigureAwait(false);
            if (observed.IsCompleted)
            {
                var result = await observed.ConfigureAwait(false);
                lock (sync)
                {
                    return retired || !session.IsCurrent
                        ? Unknown("V2ModeOwnerExpiredBeforeDelivery", "控制结果交付前原会话已失效") : result;
                }
            }
            return Unknown(token.IsCancellationRequested ? "V2ModeCancelledAfterWrite" : "V2ModeAcknowledgementUnknown",
                "模式命令已进入发送流程，但受理结果待确认；不能据此报告模式完成");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "V2ModeExecute");
            return new OperationResult<Ad102V2ModeAcknowledgement>.Unknown(new(report.ErrorId, "V2ModeExecuteFailed",
                "模式控制未完成确认", "保留原会话及请求，核实实际状态，不自动重发"));
        }
        finally
        {
            if (deadlineCancellation is not null)
            {
                try { await deadlineCancellation.CancelAsync().ConfigureAwait(false); deadlineCancellation.Dispose(); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { reporter.Report(exception, "V2ModeWaitCleanup"); }
            }
            if (entered)
            {
                lock (sync)
                {
                    if (!beganWrite && request is not null) { ledger.CancelBeforeWrite(context); }
                    executing = false;
                }
            }
        }
    }

    private async Task<OperationResult<Unit>> WriteAndRecordAsync(Ad102V2ModeRequest request, CancellationToken token)
    {
        try
        {
            var receipt = await session.WriteOwnedAsync(this, request.Report.AsMemory(), token).ConfigureAwait(false);
            lock (sync)
            {
                var recorded = ledger.RecordWrite(request.Context, receipt.Status);
                if (!lastAcknowledgement.IsDefaultOrEmpty && !retired) { ObserveLocked(lastAcknowledgement); }
                return recorded;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "V2ModeWriteReceipt");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "V2ModeWriteReceiptUnconfirmed",
                "模式写入收据处理未确认，原请求保留", "收敛原 I/O 并核实设备，不重发命令"));
        }
    }

    private void HandleReport(Ad102V2Status status, ImmutableArray<byte> bytes)
    {
        lock (sync)
        {
            if (status.LastToken == Ad102V2HidCodec.StatusProbeToken)
            { statusQuery?.TrySetResult(status); }
            var pending = ledger.Pending;
            if (retired || pending is null || status.LastHidCommand != 0x31 || status.LastToken != pending.Token
                || status.LastHidResult == Ad102V2HidResult.None) { return; }
            lastAcknowledgement = bytes;
            ObserveLocked(bytes);
        }
    }

    private void ObserveLocked(ImmutableArray<byte> bytes)
    {
        if (ledger.Pending is not { } pending) { return; }
        var result = ledger.ObserveAcknowledgement(pending.Context, bytes.AsSpan());
        if (result is OperationResult<Ad102V2ModeAcknowledgement>.Success) { acknowledgement?.TrySetResult(result); }
        // 早到 ACK 的未知结果保留在 ledger，完整写收据到达后重新核对同一报告。
    }

    public void Retire()
    {
        lock (sync)
        {
            retired = true;
            ledger.Retire();
            acknowledgement?.TrySetResult(Unknown("V2ModeOwnerRetired", "原 HID 会话已失效，不能用旧应答完成控制"));
        }
    }

    public OperationResult<Ad102V2ModeActualState> VerifyAfterTimeout(Ad102V2ModeRequeryEvidence evidence,
        Func<OperationContext, bool> isCurrent) => ledger.VerifyAfterTimeout(evidence, isCurrent, reporter);

    public OperationResult<TimeSpan> CaptureRemainingWait(OperationContext context) => ledger.CaptureRemainingWait(context);

    public OperationResult<DeviceObservation> VerifyTarget(Ad102V2ModeTargetEvidence evidence,
        Func<OperationContext, bool> isCurrent) => ledger.VerifyTarget(evidence, isCurrent, reporter);

    public Task<OperationResult<Unit>> CloseAsync()
    {
        lock (sync)
        {
            Retire();
            return closing ??= CloseOwnedAsync();
        }
    }

    private async Task<OperationResult<Unit>> CloseOwnedAsync()
    {
        await Task.Yield();
        try
        {
            var result = input is null ? await session.CloseAsync().ConfigureAwait(false) : await input.CloseAsync().ConfigureAwait(false);
            Task<OperationResult<Unit>>? originalWrite;
            lock (sync) { originalWrite = writeReceipt; }
            if (originalWrite is not null) { await originalWrite.ConfigureAwait(false); }
            lock (sync)
            {
                if (ledger.Pending is { } request && session.LastWriteReceipt is { } receipt)
                { ledger.RecordWrite(request.Context, receipt.Status); }
            }
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "V2ModeClose");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "V2ModeCloseUnconfirmed",
                "模式控制所有者关闭未确认", "保留原会话及诊断，不创建替代控制所有者"));
        }
    }

    public async ValueTask DisposeAsync() => _ = await CloseAsync().ConfigureAwait(false);

    private static OperationResult<T> Failure<T>(string code, string reason) =>
        new OperationResult<T>.Failure(AppError.Create(code, reason, "保留原会话，核实当前能力和控制归属"));

    private static OperationResult<Ad102V2ModeAcknowledgement> Unknown(string code, string reason) =>
        new OperationResult<Ad102V2ModeAcknowledgement>.Unknown(AppError.Create(code, reason,
            "收敛原写入并重新枚举核实同设备，不重复命令或反向补偿"));
}
