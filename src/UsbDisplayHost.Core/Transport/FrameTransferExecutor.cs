using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Core.Transport;

// 每个物理会话一个执行者。传输及控制仍由同一会话所有者串行授予，不能绕过此所有者插入命令。
public sealed partial class FrameTransferExecutor
{
    private readonly DeviceKey deviceKey;
    private readonly long sessionGeneration;
    private readonly DeviceCapabilities capabilities;
    private readonly FirmwareJpegProfile profile;
    private readonly TransportLimits limits;
    private readonly IDisplayTransport transport;
    private readonly IJpegFrameValidator validator;
    private readonly IFrameParserRecovery? recovery;
    private readonly IFrameSessionGuard guard;
    private readonly IErrorReporter errorReporter;
    private readonly TimeProvider timeProvider;
    private FrameTransferStatus status = new(FrameTransferState.RecoveryRequired, 0, 0, null);
    private int busy;
    private int candidateValidationBusy;
    // 不确认内核收敛时保留强所有权；此执行者必须由会话保留，不得假装已完成释放。
    private byte[]? retainedBuffer;
    private bool recoveryIoUnconfirmed;
    private bool automaticRecoveryUsed;

    public FrameTransferExecutor(DeviceKey deviceKey, long sessionGeneration, DeviceCapabilities capabilities,
        FirmwareJpegProfile profile, TransportLimits limits, IDisplayTransport transport,
        IJpegFrameValidator validator, IFrameParserRecovery? recovery, IFrameSessionGuard guard,
        IErrorReporter errorReporter, TimeProvider timeProvider)
    {
        this.deviceKey = deviceKey;
        this.sessionGeneration = sessionGeneration;
        this.capabilities = capabilities;
        this.profile = profile;
        this.limits = limits;
        this.transport = transport;
        this.validator = validator;
        this.recovery = recovery;
        this.guard = guard;
        this.errorReporter = errorReporter;
        this.timeProvider = timeProvider;
    }

    public FrameTransferStatus Current => Volatile.Read(ref status);
    public DeviceKey DeviceKey => deviceKey;
    public long SessionGeneration => sessionGeneration;
    internal TimeProvider Clock => timeProvider;
    internal IDisplayTransport Transport => transport;
    internal IFrameParserRecovery? Recovery => recovery;
    internal DeviceCapabilities Capabilities => capabilities;

    // 仅在连接工作已核实实际初始化和最终控制读回后，由同会话组合工厂转交一次。
    internal OperationResult<Unit> AdmitConnectionInitialization(ThemeInitializationReceipt initialization,
        FrameParserRecoveryReceipt? clearance)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
        { return Failed<Unit>("FrameInitializationAdmissionBusy", "帧执行器已开始操作，不能转交连接初始化"); }
        try
        {
            if (Current.State != FrameTransferState.RecoveryRequired || Current.UsbCompletedFrames != 0
                || Current.ConfirmedPacketBytes != 0 || retainedBuffer is not null || recoveryIoUnconfirmed
                || !capabilities.IsProtocolMatched || !StringComparer.Ordinal.Equals(profile.FirmwareVersion, capabilities.FirmwareVersion)
                || !guard.IsLiveSession(deviceKey, sessionGeneration)
                || initialization.Context.DeviceKey != deviceKey || initialization.Context.SessionGeneration != sessionGeneration
                || initialization.Context.OperationId == Guid.Empty || initialization.Context.ContentRevision < 0
                || initialization.PowerIntent != PowerState.On || !initialization.ProbeVerified || !initialization.OnSequenceCompleted
                || initialization.CompletedCommands != 9 || clearance is null
                || clearance.DeviceKey != deviceKey || clearance.SessionGeneration != sessionGeneration
                || clearance.OperationId != initialization.Context.OperationId || !clearance.ParserCleared || !clearance.PreservedPowerIntent
                || !StringComparer.Ordinal.Equals(clearance.FirmwareVersion, capabilities.FirmwareVersion)
                || string.IsNullOrWhiteSpace(clearance.EvidenceReference))
            { return Failed<Unit>("FrameConnectionInitializationMismatch", "连接初始化或 parser 收据不属于当前未使用的帧执行器"); }
            Publish(FrameTransferState.Ready, null, 0, 0);
            return new OperationResult<Unit>.Success(new());
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    // 候选准备不排队、不更改传输状态、不执行 USB 或 parser 恢复；发送入口仍再次校验。
    public async Task<OperationResult<Unit>> ValidateCandidateAsync(EncodedFrame frame, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref candidateValidationBusy, 1, 0) != 0)
        {
            return Failed<Unit>("CandidateValidationBusy", "已有候选首帧正在校验");
        }
        try { return await ValidateFrameAsync(frame, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new OperationResult<Unit>.Cancelled();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "CandidateFrameValidation");
            return new OperationResult<Unit>.Failure(new(report.ErrorId, "CandidateFrameValidationFailed",
                "候选首帧校验未完成，未提交发送", "请检查主题素材与固件兼容记录"));
        }
        finally { Volatile.Write(ref candidateValidationBusy, 0); }
    }

    private async Task<OperationResult<Unit>> ValidateFrameAsync(EncodedFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Matches(frame.Context) || !capabilities.IsProtocolMatched || limits.MaximumPacketBytes <= 0
            || !StringComparer.Ordinal.Equals(profile.FirmwareVersion, capabilities.FirmwareVersion)
            || frame.Purpose != FramePurpose.Active || frame.FrameNumber < 0
            || frame.Width != 1920 || frame.Height != 1080 || frame.Width != capabilities.Width || frame.Height != capabilities.Height
            || frame.Jpeg.IsDefaultOrEmpty || frame.Jpeg.Length > Math.Min(FrameFragmentPlan.MaximumFrameBytes, capabilities.MaximumFrameBytes))
        {
            return Failed<Unit>("FrameSessionMismatch", "设备帧与当前已验证会话不匹配");
        }
        if (!guard.CanStartFrame(frame.Context)) { return Failed<Unit>("StaleFrame", "帧已过期或当前状态不允许推流"); }
        var validated = await validator.ValidateAsync(frame, capabilities, profile, cancellationToken).ConfigureAwait(false);
        if (validated is OperationResult<JpegFrameInfo>.Failure invalid) { return new OperationResult<Unit>.Failure(invalid.Error); }
        if (validated is OperationResult<JpegFrameInfo>.Unknown unknown) { return new OperationResult<Unit>.Unknown(unknown.Error); }
        if (validated is not OperationResult<JpegFrameInfo>.Success info) { return new OperationResult<Unit>.Cancelled(); }
        if (info.Value.Width != frame.Width || info.Value.Height != frame.Height || !profile.Accepts(info.Value.Format))
        {
            return Failed<Unit>("FrameValidationMismatch", "JPEG 校验结果与受理帧不一致");
        }
        var plan = FrameFragmentPlan.Create(frame.Jpeg.Length, capabilities.MaximumFrameBytes,
            capabilities.MaximumTransferBytes, limits.MaximumTransferBytes);
        if (plan is OperationResult<FrameFragmentPlan>.Failure invalidPlan) { return new OperationResult<Unit>.Failure(invalidPlan.Error); }
        cancellationToken.ThrowIfCancellationRequested();
        return guard.CanStartFrame(frame.Context) ? new OperationResult<Unit>.Success(new Unit())
            : Failed<Unit>("StaleFrame", "JPEG 校验期间帧所属会话或操作已过期");
    }

    public async Task<OperationResult<FrameTransferReceipt>> SendAsync(EncodedFrame frame,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
        {
            return Failed<FrameTransferReceipt>("FrameTransferBusy", "已有帧或恢复操作在执行");
        }
        bool attemptedWrite = false;
        bool nativeOutstanding = false;
        long started = 0;
        try
        {
            started = timeProvider.GetTimestamp();
            cancellationToken.ThrowIfCancellationRequested();
            if (Current.State != FrameTransferState.Ready || retainedBuffer is not null)
            {
                return Failed<FrameTransferReceipt>("FrameChannelNotReady", "帧通道尚未确认清理完成");
            }
            var validated = await ValidateFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            if (validated is OperationResult<Unit>.Failure invalid) { return new OperationResult<FrameTransferReceipt>.Failure(invalid.Error); }
            if (validated is OperationResult<Unit>.Unknown unknown) { return new OperationResult<FrameTransferReceipt>.Unknown(unknown.Error); }
            if (validated is not OperationResult<Unit>.Success) { return new OperationResult<FrameTransferReceipt>.Cancelled(); }
            var planned = FrameFragmentPlan.Create(frame.Jpeg.Length, capabilities.MaximumFrameBytes,
                capabilities.MaximumTransferBytes, limits.MaximumTransferBytes);
            if (planned is OperationResult<FrameFragmentPlan>.Failure badPlan) { return new OperationResult<FrameTransferReceipt>.Failure(badPlan.Error); }
            var plan = ((OperationResult<FrameFragmentPlan>.Success)planned).Value;
            cancellationToken.ThrowIfCancellationRequested();
            if (!guard.CanStartFrame(frame.Context)) { return Failed<FrameTransferReceipt>("StaleFrame", "帧已过期或当前状态不允许推流"); }
            retainedBuffer = new byte[plan.TransferBytes];
            var cursor = plan.CreateCursor();
            int packetCount = 0;
            long packetBytes = 0;
            while (!cursor.IsComplete)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!guard.IsLiveSession(deviceKey, sessionGeneration))
                {
                    return await AbortAsync(AppError.Create("FrameSessionLost", "会话已失效，停止剩余分片",
                        "请重新连接所选设备"), nativeOutstanding, attemptedWrite, frame.Context, false).ConfigureAwait(false);
                }
                var sliced = cursor.TryWriteNext(frame.Jpeg.AsSpan(), retainedBuffer);
                if (sliced is OperationResult<FrameFragment>.Failure badFragment)
                {
                    return await AbortAsync(badFragment.Error, nativeOutstanding, attemptedWrite, frame.Context, false).ConfigureAwait(false);
                }
                var fragment = ((OperationResult<FrameFragment>.Success)sliced).Value;
                attemptedWrite = nativeOutstanding = true;
                var written = await transport.WriteAsync(retainedBuffer.AsMemory(0, fragment.PacketBytes), cancellationToken).ConfigureAwait(false);
                nativeOutstanding = !written.IsCompletionKnown;
                if (!written.IsComplete || written.RequestedBytes != fragment.PacketBytes)
                {
                    var error = AppError.Create("FrameWriteIncomplete", "分片写入未完整完成，已停止该帧",
                        "等待帧通道恢复；无法确认清理时请重新插拔所选设备");
                    long? completedBytes = written.IsCompletionKnown && written.CompletedBytes >= 0
                        && written.CompletedBytes <= fragment.PacketBytes ? packetBytes + written.CompletedBytes : null;
                    RecordFrame(frame.Context, started, completedBytes, "FrameWriteIncomplete", error.ErrorId);
                    return await AbortAsync(error, nativeOutstanding, true, frame.Context,
                        (!written.WasCancelled || written.WasTimedOut) && !cancellationToken.IsCancellationRequested).ConfigureAwait(false);
                }
                packetCount++;
                packetBytes += fragment.PacketBytes;
                Publish(Current.State, null, 0, fragment.PacketBytes);
            }
            // USB 尾片完整写入即产生传输收据；这里不宣称呈现确认或更新活动配置。
            Publish(FrameTransferState.Ready, null, 1, 0);
            automaticRecoveryUsed = false;
            retainedBuffer = null;
            RecordFrame(frame.Context, started, packetBytes, "UsbFrameCompleted");
            return new OperationResult<FrameTransferReceipt>.Success(new(frame.Context, frame.FrameNumber,
                frame.Jpeg.Length, packetCount, packetBytes, timeProvider.GetTimestamp()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!attemptedWrite) { return new OperationResult<FrameTransferReceipt>.Cancelled(); }
            return await AbortAsync(AppError.Create("FrameCancelledAfterWrite", "发送已中断，设备可能持有部分帧",
                "确认帧通道清理后再发送完整帧"), nativeOutstanding, true, frame.Context, false).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "FrameTransfer",
                FrameAssociation(frame.Context, started, null, "FrameTransferFailed"));
            return await AbortAsync(new AppError(report.ErrorId, "FrameTransferFailed", "设备帧发送失败",
                "确认通道恢复；无法确认时请重新插拔所选设备"), nativeOutstanding, attemptedWrite, frame.Context, false).ConfigureAwait(false);
        }
        finally
        {
            if (!attemptedWrite) { retainedBuffer = null; }
            Volatile.Write(ref busy, 0);
        }
    }

    // 显式连接／恢复操作调用；首次发送也必须先取得 parser 清理确认。
    public async Task<OperationResult<Unit>> RecoverAsync(OperationContext context, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) { return Failed<Unit>("FrameTransferBusy", "已有帧或恢复操作在执行"); }
        try
        {
            var drained = await DrainOwnedIoAsync().ConfigureAwait(false);
            if (drained is not OperationResult<Unit>.Success) { return drained; }
            automaticRecoveryUsed = false;
            return await RecoverParserAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    public async Task<OperationResult<Unit>> DrainAsync()
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) { return Failed<Unit>("FrameTransferBusy", "发送尚未结束，不能提前释放通道"); }
        try { return await DrainOwnedIoAsync().ConfigureAwait(false); }
        finally { Volatile.Write(ref busy, 0); }
    }

    private async Task<OperationResult<FrameTransferReceipt>> AbortAsync(AppError error, bool nativeOutstanding,
        bool attemptedWrite, FrameContext context, bool allowRecovery)
    {
        if (!attemptedWrite) { return new OperationResult<FrameTransferReceipt>.Failure(error); }
        Publish(FrameTransferState.RecoveryRequired, error, 0, 0);
        if (nativeOutstanding)
        {
            var drained = await DrainOwnedIoAsync().ConfigureAwait(false);
            if (drained is not OperationResult<Unit>.Success)
            {
                return new OperationResult<FrameTransferReceipt>.Unknown(Current.Error ?? error);
            }
        }
        else { retainedBuffer = null; }
        if (allowRecovery && recovery is not null && !automaticRecoveryUsed)
        {
            automaticRecoveryUsed = true;
            try
            {
                if (guard.CanStartFrame(context))
                {
                    var resolved = guard.ResolveRecoveryContext(context);
                    if (resolved is OperationResult<OperationContext>.Success operation)
                    {
                        await RecoverParserAsync(operation.Value, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = errorReporter.Report(exception, "FrameRecoveryGuard");
                Publish(FrameTransferState.RecoveryRequired, new(report.ErrorId, "FrameRecoveryFailed",
                    "帧通道恢复条件无法确认", "请重新插拔所选设备"), 0, 0);
            }
        }
        if (Current.State == FrameTransferState.IoUnconfirmed)
        {
            return new OperationResult<FrameTransferReceipt>.Unknown(Current.Error ?? error);
        }
        return new OperationResult<FrameTransferReceipt>.Failure(error);
    }

    private async Task<OperationResult<Unit>> DrainOwnedIoAsync()
    {
        if (recoveryIoUnconfirmed)
        {
            var recoveredDrain = await DrainRecoveryIoAsync().ConfigureAwait(false);
            if (recoveredDrain is not OperationResult<Unit>.Success) { return recoveredDrain; }
        }
        if (retainedBuffer is null) { return new OperationResult<Unit>.Success(new Unit()); }
        try
        {
            var result = await transport.CancelAndDrainAsync(CancellationToken.None).ConfigureAwait(false);
            if (result is OperationResult<Unit>.Success)
            {
                retainedBuffer = null;
                Publish(FrameTransferState.RecoveryRequired, Current.Error, 0, 0);
                return result;
            }
            AppError error = result switch
            {
                OperationResult<Unit>.Failure failure => failure.Error,
                OperationResult<Unit>.Unknown unknown => unknown.Error,
                _ => AppError.Create("FrameDrainCancelled", "内核请求收敛未确认", "请退出并重新连接设备")
            };
            Publish(FrameTransferState.IoUnconfirmed, error, 0, 0);
            return new OperationResult<Unit>.Unknown(error);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "FrameTransferDrain");
            var error = new AppError(report.ErrorId, "FrameDrainFailed", "无法确认内核请求结束，保留缓冲所有者",
                "请退出应用后重新连接设备");
            Publish(FrameTransferState.IoUnconfirmed, error, 0, 0);
            return new OperationResult<Unit>.Unknown(error);
        }
    }

    private async Task<OperationResult<Unit>> RecoverParserAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var result = await TryRecoverParserAsync(context, cancellationToken).ConfigureAwait(false);
        if (recoveryIoUnconfirmed)
        {
            var drained = await DrainRecoveryIoAsync().ConfigureAwait(false);
            if (drained is not OperationResult<Unit>.Success) { return drained; }
        }
        if (result is OperationResult<Unit>.Success)
        {
            try
            {
                if (!guard.IsLiveSession(deviceKey, sessionGeneration))
                {
                    return RecoveryFailure("ParserRecoverySessionLost", "清理期间会话已失效，不能继续发送");
                }
                Publish(FrameTransferState.Ready, null, 0, 0);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = errorReporter.Report(exception, "FrameRecoveryGuard");
                var error = new AppError(report.ErrorId, "FrameRecoveryGuardFailed", "无法核实恢复后的当前会话", "请重新连接所选设备");
                Publish(FrameTransferState.RecoveryRequired, error, 0, 0);
                return new OperationResult<Unit>.Failure(error);
            }
        }
        return result;
    }

    private async Task<OperationResult<Unit>> TryRecoverParserAsync(OperationContext context, CancellationToken cancellationToken)
    {
        Publish(FrameTransferState.RecoveryRequired, Current.Error, 0, 0);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (recovery is null || context.DeviceKey != deviceKey || context.SessionGeneration != sessionGeneration
                || context.OperationId == Guid.Empty || context.ContentRevision < 0 || !capabilities.IsProtocolMatched
                || !StringComparer.Ordinal.Equals(profile.FirmwareVersion, capabilities.FirmwareVersion)
                || !guard.IsLiveSession(deviceKey, sessionGeneration))
            {
                return RecoveryFailure("ParserRecoveryUnavailable", "没有可核实的当前固件帧清理路径");
            }
            recoveryIoUnconfirmed = true;
            var recovered = await recovery.RecoverAsync(context, cancellationToken).ConfigureAwait(false);
            if (recovered is OperationResult<FrameParserRecoveryReceipt>.Success success
                && success.Value.DeviceKey == deviceKey && success.Value.SessionGeneration == sessionGeneration
                && success.Value.OperationId == context.OperationId && success.Value.ParserCleared && success.Value.PreservedPowerIntent
                && StringComparer.Ordinal.Equals(success.Value.FirmwareVersion, capabilities.FirmwareVersion)
                && !string.IsNullOrWhiteSpace(success.Value.EvidenceReference)
                && guard.IsLiveSession(deviceKey, sessionGeneration))
            {
                return new OperationResult<Unit>.Success(new Unit());
            }
            AppError error = recovered switch
            {
                OperationResult<FrameParserRecoveryReceipt>.Failure failure => failure.Error,
                OperationResult<FrameParserRecoveryReceipt>.Unknown unknown => unknown.Error,
                _ => AppError.Create("ParserRecoveryUnconfirmed", "设备帧清理结果未确认", "请重新插拔所选设备")
            };
            Publish(FrameTransferState.RecoveryRequired, error, 0, 0);
            return new OperationResult<Unit>.Failure(error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RecoveryFailure("ParserRecoveryCancelled", "设备帧清理已取消，仍禁止发送");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "FrameParserRecovery");
            var error = new AppError(report.ErrorId, "ParserRecoveryFailed", "设备帧清理失败", "请重新插拔所选设备");
            Publish(FrameTransferState.RecoveryRequired, error, 0, 0);
            return new OperationResult<Unit>.Failure(error);
        }
    }

    private async Task<OperationResult<Unit>> DrainRecoveryIoAsync()
    {
        try
        {
            var drained = await recovery!.DrainAsync().ConfigureAwait(false);
            if (drained is OperationResult<Unit>.Success)
            {
                recoveryIoUnconfirmed = false;
                Publish(FrameTransferState.RecoveryRequired, Current.Error, 0, 0);
                return drained;
            }
            var error = drained switch
            {
                OperationResult<Unit>.Failure failure => failure.Error,
                OperationResult<Unit>.Unknown unknown => unknown.Error,
                _ => AppError.Create("ParserRecoveryDrainUnconfirmed", "帧清理 I/O 收敛未确认", "请退出应用后重新连接设备")
            };
            Publish(FrameTransferState.IoUnconfirmed, error, 0, 0);
            return new OperationResult<Unit>.Unknown(error);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "FrameRecoveryDrain");
            var error = new AppError(report.ErrorId, "ParserRecoveryDrainFailed", "帧清理 I/O 未结束，保留恢复所有者",
                "请退出应用后重新连接设备");
            Publish(FrameTransferState.IoUnconfirmed, error, 0, 0);
            return new OperationResult<Unit>.Unknown(error);
        }
    }

    private OperationResult<Unit> RecoveryFailure(string code, string reason)
    {
        var error = AppError.Create(code, reason, "请核对固件清理契约或重新插拔所选设备");
        Publish(FrameTransferState.RecoveryRequired, error, 0, 0);
        return new OperationResult<Unit>.Failure(error);
    }

    private bool Matches(FrameContext context) => context.DeviceKey == deviceKey && sessionGeneration > 0
        && context.SessionGeneration == sessionGeneration && context.OperationId != Guid.Empty && context.ContentRevision >= 0;

    private void Publish(FrameTransferState state, AppError? error, long frames, long bytes)
    {
        var before = Current;
        Volatile.Write(ref status, new(state, before.UsbCompletedFrames + frames, before.ConfirmedPacketBytes + bytes, error));
    }

    private static OperationResult<T> Failed<T>(string code, string reason) =>
        new OperationResult<T>.Failure(AppError.Create(code, reason, "请等待当前操作结束或重新连接所选设备"));
}
