using System.Collections.Immutable;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;

namespace UsbDisplayHost.Core.Devices;

public sealed record Ad102V2ModeRequest(OperationContext Context, DeviceMode Target, byte Token,
    ImmutableArray<byte> Report, PowerState PowerIntent, DeviceMode OriginalMode);
public sealed record Ad102V2ModeAcknowledgement(Ad102V2ModeRequest Request, Ad102V2HidResult Result);
public enum Ad102V2ModeActualResult { TargetReady, OriginalReady }
public sealed record Ad102V2ModeActualState(Ad102V2ModeRequest Request, Ad102V2ModeActualResult Result,
    DeviceObservation Observation);
public sealed record Ad102V2ModeRequeryEvidence(long CapturedTimestamp, Ad102V2ModeTargetEvidence Actual);

// 一个物理 HID 会话持有一个记录；受理确认不是目标模式可操作的完成证明。
public sealed class Ad102V2ModeControlLedger
{
    private readonly object sync = new();
    private readonly DeviceKey deviceKey;
    private readonly long generation;
    private readonly Ad102V2HidCodec codec;
    private readonly TimeProvider clock;
    private readonly IErrorReporter errorReporter;
    public static TimeSpan NormalWaitBudget => TimeSpan.FromSeconds(8);
    public OperationResult<TimeSpan> CaptureRemainingWait(OperationContext context)
    {
        lock (sync)
        {
            if (!MatchesReceipt(context) || !writeStarted)
            { return Failure<TimeSpan>("V2WaitOriginUnavailable", "原模式写入尚未开始或上下文不匹配"); }
            try
            {
                var elapsed = clock.GetElapsedTime(writeStartedTimestamp);
                if (elapsed < TimeSpan.Zero)
                { return Unknown<TimeSpan>("V2WaitClockInvalid", "单调等待时钟返回了无效间隔"); }
                return new OperationResult<TimeSpan>.Success(elapsed >= NormalWaitBudget
                    ? TimeSpan.Zero : NormalWaitBudget - elapsed);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = errorReporter.Report(exception, "V2RemainingWait");
                return new OperationResult<TimeSpan>.Unknown(new(report.ErrorId, "V2WaitUnconfirmed",
                    "原模式等待期限无法核实", "保留原请求，不重置计时或重发"));
            }
        }
    }
    private int issuedTokens;
    private byte reservedObservedToken;
    private bool initialTokenReserved;
    private Ad102V2ModeRequest? pending;
    private bool writeStarted;
    private bool writeRecorded;
    private bool fullWriteConfirmed;
    private bool accepted;
    private bool retired;
    private DeviceObservation? verifiedTarget;
    private long writeStartedTimestamp;

    private Ad102V2ModeControlLedger(DeviceKey deviceKey, long generation, Ad102V2HidCodec codec, TimeProvider clock,
        IErrorReporter errorReporter)
    {
        this.deviceKey = deviceKey; this.generation = generation; this.codec = codec;
        this.clock = clock; this.errorReporter = errorReporter;
    }

    public static OperationResult<Ad102V2ModeControlLedger> Create(DeviceKey? deviceKey, long generation,
        Ad102V2HidCodec? codec, TimeProvider? clock, IErrorReporter? errorReporter) =>
        deviceKey is null || generation <= 0 || codec is null || clock is null || errorReporter is null
        ? Failure<Ad102V2ModeControlLedger>("InvalidV2ControlOwner", "HID 控制记录缺少物理会话归属")
        : new OperationResult<Ad102V2ModeControlLedger>.Success(new(deviceKey, generation, codec, clock, errorReporter));

    public Ad102V2ModeRequest? Pending { get { lock (sync) { return pending; } } }

    public OperationResult<Unit> ReserveObservedToken(byte observedToken)
    {
        lock (sync)
        {
            if (retired || pending is not null || issuedTokens != 0 || initialTokenReserved)
            { return Failure<Unit>("V2InitialTokenUnavailable", "初始应答 token 基线不能重复建立或覆盖已发命令"); }
            reservedObservedToken = observedToken;
            initialTokenReserved = true;
            return new OperationResult<Unit>.Success(new());
        }
    }

    public OperationResult<Ad102V2ModeRequest> Prepare(OperationContext context, DeviceMode target, PowerState powerIntent)
    {
        lock (sync)
        {
            if (retired || pending is not null || !initialTokenReserved || !MatchesOwner(context))
            { return Failure<Ad102V2ModeRequest>("V2ControlUnavailable", "HID 会话已失效、缺少初始应答基线或仍有待核实控制"); }
            if (powerIntent is not (PowerState.On or PowerState.Off))
            { return Failure<Ad102V2ModeRequest>("V2PowerIntentUnavailable", "切换前电源意图尚未确认"); }
            var feature = codec.Feature;
            DeviceMode originalMode = feature.CurrentMode switch
            {
                Ad102V2Mode.Theme => DeviceMode.Theme,
                Ad102V2Mode.Screen => DeviceMode.SecondaryDisplay,
                _ => DeviceMode.Unknown
            };
            if (feature.MaintenanceBusy || feature.CurrentMode != feature.TargetMode
                || originalMode == DeviceMode.Unknown || target == originalMode)
            { return Failure<Ad102V2ModeRequest>("V2OriginalModeUnavailable", "原模式未稳定核实或请求与原模式相同"); }
            if (issuedTokens == byte.MaxValue)
            { return Failure<Ad102V2ModeRequest>("V2TokenExhausted", "本物理会话的 HID token 已耗尽，不能回绕复用"); }
            int nextToken = issuedTokens + 1;
            if (nextToken == reservedObservedToken) { nextToken++; }
            if (nextToken >= Ad102V2HidCodec.StatusProbeToken)
            { return Failure<Ad102V2ModeRequest>("V2TokenExhausted", "本物理会话剩余 token 与旧应答基线冲突，不能复用"); }
            byte token = (byte)nextToken;
            byte[] bytes = new byte[Ad102V2HidCodec.ReportBytes];
            var encoded = codec.WriteModeSwitch(target, token, bytes);
            if (encoded is OperationResult<int>.Failure rejected)
            { return new OperationResult<Ad102V2ModeRequest>.Failure(rejected.Error); }
            issuedTokens = nextToken;
            pending = new(context, target, token, ImmutableArray.CreateRange(bytes), powerIntent, originalMode);
            writeStarted = writeRecorded = fullWriteConfirmed = accepted = false;
            return new OperationResult<Ad102V2ModeRequest>.Success(pending);
        }
    }

    public OperationResult<Unit> BeginWrite(OperationContext context)
    {
        lock (sync)
        {
            if (!MatchesPending(context) || writeStarted)
            { return Failure<Unit>("V2WriteUnavailable", "HID 控制不能重复发送或使用过期操作"); }
            try { writeStartedTimestamp = clock.GetTimestamp(); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = errorReporter.Report(exception, "V2ModeWaitStart");
                return new OperationResult<Unit>.Failure(new(report.ErrorId, "V2ClockUnavailable",
                    "无法建立模式切换等待时刻，未批准发送 HID 控制", "查看诊断并核实时钟服务后重试"));
            }
            writeStarted = true;
            return new OperationResult<Unit>.Success(new());
        }
    }

    public OperationResult<Unit> RecordWrite(OperationContext context, UsbWriteResult result)
    {
        lock (sync)
        {
            if (!MatchesReceipt(context) || !writeStarted || writeRecorded)
            { return Failure<Unit>("V2WriteReceiptExpired", "HID 写入收据不属于当前在途操作"); }
            // 未知完成可由原 I/O 所有者补交最终收据，但 BeginWrite 始终禁止第二次发送。
            writeRecorded = result.IsCompletionKnown;
            fullWriteConfirmed = result.RequestedBytes == Ad102V2HidCodec.ReportBytes && result.IsComplete;
            return fullWriteConfirmed ? new OperationResult<Unit>.Success(new())
                : Unknown<Unit>("V2WriteUnconfirmed", "HID 写入未完整确认，保留原请求并禁止重发");
        }
    }

    public OperationResult<Ad102V2ModeAcknowledgement> ObserveAcknowledgement(OperationContext context,
        ReadOnlySpan<byte> report)
    {
        lock (sync)
        {
            if (!MatchesPending(context) || !writeStarted)
            { return Failure<Ad102V2ModeAcknowledgement>("V2AcknowledgementExpired", "HID 应答不属于当前操作或会话"); }
            var decoded = codec.DecodeInput(report);
            if (decoded is OperationResult<Ad102V2Status>.Failure invalid)
            { return new OperationResult<Ad102V2ModeAcknowledgement>.Failure(invalid.Error); }
            var status = ((OperationResult<Ad102V2Status>.Success)decoded).Value;
            if (status.LastHidCommand != 0x31 || status.LastToken != pending!.Token
                || status.LastHidResult == Ad102V2HidResult.None)
            { return Failure<Ad102V2ModeAcknowledgement>("V2AcknowledgementUnrelated", "HID 最近应答与原模式请求不匹配"); }
            if (status.LastHidResult == Ad102V2HidResult.Accepted)
            {
                // 应答可能早于本机完成收据；受理事实不能因等待收敛而被遗忘。
                accepted = true;
                var target = pending.Target == DeviceMode.Theme ? Ad102V2Mode.Theme : Ad102V2Mode.Screen;
                if (status.TargetMode != target)
                { return Unknown<Ad102V2ModeAcknowledgement>("V2TargetMismatch", "HID 已受理应答的目标与原请求不一致"); }
                if (!fullWriteConfirmed)
                { return Unknown<Ad102V2ModeAcknowledgement>("V2NativeWriteUnconfirmed", "已收到 HID 应答，但原生写入尚未确认收敛"); }
                // 保留请求供重枚举／目标核验；不在收到 ACK 后释放或报告已切换。
                return new OperationResult<Ad102V2ModeAcknowledgement>.Success(new(pending, status.LastHidResult));
            }
            if (accepted)
            {
                return Unknown<Ad102V2ModeAcknowledgement>("V2AcknowledgementConflict",
                    "原请求已确认受理，后续拒绝应答不能证明设备未执行");
            }
            if (!fullWriteConfirmed)
            { return Unknown<Ad102V2ModeAcknowledgement>("V2NativeWriteUnconfirmed", "已收到 HID 应答，但原生写入尚未确认收敛"); }
            var rejected = new Ad102V2ModeAcknowledgement(pending, status.LastHidResult);
            pending = null;
            return new OperationResult<Ad102V2ModeAcknowledgement>.Success(rejected);
        }
    }

    public OperationResult<Unit> CancelBeforeWrite(OperationContext context)
    {
        lock (sync)
        {
            if (!MatchesPending(context) || writeStarted)
            { return Failure<Unit>("V2CancellationUnconfirmed", "HID 写入已开始或操作已失效，不能当作未发送取消"); }
            pending = null;
            return new OperationResult<Unit>.Success(new());
        }
    }

    public void Retire()
    {
        // 失效只拒绝后续使用，保留待核实请求；不取消 I/O 或宣称原生资源已经释放。
        lock (sync) { retired = true; }
    }

    public OperationResult<Ad102V2ModeActualState> VerifyAfterTimeout(Ad102V2ModeRequeryEvidence evidence,
        Func<OperationContext, bool> isCurrent, IErrorReporter reporter)
    {
        if (reporter is null || isCurrent is null)
        { return Unknown<Ad102V2ModeActualState>("V2RequeryServicesUnavailable", "模式重查缺少当前上下文或诊断服务"); }
        Ad102V2ModeRequest request;
        try
        {
            lock (sync)
            {
                if (!retired || pending is null || !writeStarted || !writeRecorded || !fullWriteConfirmed
                    || verifiedTarget is not null || evidence is null || evidence.Actual is null)
                { return Unknown<Ad102V2ModeActualState>("V2RequeryUnavailable", "原控制未收敛或没有实际重查结果"); }
                long now = clock.GetTimestamp();
                if (clock.GetElapsedTime(writeStartedTimestamp, evidence.CapturedTimestamp) < NormalWaitBudget
                    || clock.GetElapsedTime(evidence.CapturedTimestamp, now) < TimeSpan.Zero)
                { return Unknown<Ad102V2ModeActualState>("V2RequeryTooEarly", "重查结果早于等待期限或时间来源无效"); }
                request = pending;
            }
            if (evidence.Actual.Observation?.Mode == request.Target)
            {
                var target = VerifyTarget(evidence.Actual, isCurrent, reporter);
                return target switch
                {
                    OperationResult<DeviceObservation>.Success success =>
                        new OperationResult<Ad102V2ModeActualState>.Success(new(request,
                            Ad102V2ModeActualResult.TargetReady, success.Value)),
                    OperationResult<DeviceObservation>.Failure failure => new OperationResult<Ad102V2ModeActualState>.Failure(failure.Error),
                    OperationResult<DeviceObservation>.Cancelled => new OperationResult<Ad102V2ModeActualState>.Cancelled(),
                    OperationResult<DeviceObservation>.Unknown unknown => new OperationResult<Ad102V2ModeActualState>.Unknown(unknown.Error),
                    _ => Unknown<Ad102V2ModeActualState>("V2RequeryUnknown", "目标模式状态尚未确认")
                };
            }
            var original = Ad102V2ModeTargetVerifier.VerifyOriginal(request, evidence.Actual, isCurrent, reporter);
            if (original is not OperationResult<DeviceObservation>.Success originalReady)
            {
                return original is OperationResult<DeviceObservation>.Unknown unknown
                    ? new OperationResult<Ad102V2ModeActualState>.Unknown(unknown.Error)
                    : Unknown<Ad102V2ModeActualState>("V2OriginalUnknown", "原模式和目标模式均未确认可用");
            }
            lock (sync)
            {
                if (!ReferenceEquals(request, pending) || verifiedTarget is not null || !fullWriteConfirmed)
                { return Unknown<Ad102V2ModeActualState>("V2RequeryChanged", "重查交付前模式请求或结算状态变化"); }
                // 原态仍可用是观察结果，不是回滚证明；保留请求接受之后实际到达的目标证据。
                return new OperationResult<Ad102V2ModeActualState>.Success(new(request,
                    Ad102V2ModeActualResult.OriginalReady, originalReady.Value));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "V2ModeTimeoutRequery");
            return new OperationResult<Ad102V2ModeActualState>.Unknown(new(report.ErrorId, "V2RequeryFailed",
                "模式超时重查未完成，设备状态待确认", "保留原请求并查看诊断，不自动重发或反向切换"));
        }
    }

    public OperationResult<DeviceObservation> VerifyTarget(Ad102V2ModeTargetEvidence evidence,
        Func<OperationContext, bool> isCurrent, IErrorReporter reporter)
    {
        Ad102V2ModeRequest request;
        lock (sync)
        {
            if (!retired || pending is null || !writeRecorded || !fullWriteConfirmed || verifiedTarget is not null)
            {
                return Unknown<DeviceObservation>("V2TargetVerificationUnavailable",
                    "原控制会话尚未失效、写入未收敛或目标已结算，不能交付切换完成");
            }
            request = pending;
        }
        // 发现与协调器凭据只可读取内存；不在控制记录锁中调用外部核验器。
        var result = Ad102V2ModeTargetVerifier.Verify(request, evidence, isCurrent, reporter);
        if (result is not OperationResult<DeviceObservation>.Success success) { return result; }
        lock (sync)
        {
            if (!retired || !ReferenceEquals(request, pending) || !fullWriteConfirmed || verifiedTarget is not null)
            { return Unknown<DeviceObservation>("V2TargetVerificationChanged", "目标核验期间原请求或结算状态已变化"); }
            verifiedTarget = success.Value;
            // 保留旧请求与 token 历史；此处不关闭句柄或授权创建替代原生所有者。
            return result;
        }
    }

    private bool MatchesOwner(OperationContext context) => context.DeviceKey == deviceKey
        && context.SessionGeneration == generation && context.OperationId != Guid.Empty && context.ContentRevision >= 0;
    private bool MatchesPending(OperationContext context) => !retired && MatchesOwner(context) && pending?.Context == context;
    // 失效禁止新控制和旧 ACK，但不能阻止原 I/O 所有者提交真实最终完成。
    private bool MatchesReceipt(OperationContext context) => MatchesOwner(context) && pending?.Context == context;
    private static OperationResult<T> Failure<T>(string code, string reason) =>
        new OperationResult<T>.Failure(AppError.Create(code, reason, "保留原会话并核实状态，不自动重发控制"));
    private static OperationResult<T> Unknown<T>(string code, string reason) =>
        new OperationResult<T>.Unknown(AppError.Create(code, reason, "等待实际 I/O 收敛及目标状态核实，不复用 token"));
}
