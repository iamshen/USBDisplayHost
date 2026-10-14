using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;

namespace UsbDisplayHost.Core.Transport;

public sealed record ThemeInitializationReceipt(OperationContext Context, PowerState PowerIntent,
    bool ProbeVerified, bool OnSequenceCompleted, int CompletedCommands);

public sealed class ThemeDataPlaneInitializer(IDisplayTransport transport, DeviceCapabilities capabilities,
    ThemeDataPlaneContract contract, Func<OperationContext, bool> isCurrent, IErrorReporter reporter)
{
    public IDisplayTransport Transport => transport;
    public DeviceCapabilities Capabilities => capabilities;
    private int busy;
    private bool ioUnconfirmed;

    public async Task<OperationResult<ThemeInitializationReceipt>> InitializeAsync(OperationContext context,
        PowerState powerIntent, int brightness, FrameParserRecoveryReceipt? parserClearance, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) { return Failure("ThemeInitializationBusy", "已有初始化或收敛工作在执行"); }
        int completedCommands = 0;
        bool attempted = false;
        bool writeAttempted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (ioUnconfirmed || context.DeviceKey is null || context.SessionGeneration <= 0
                || context.OperationId == Guid.Empty || context.ContentRevision < 0
                || powerIntent is not (PowerState.On or PowerState.Off) || brightness is < 0 or > 100
                || !capabilities.IsProtocolMatched || capabilities.Width != 1920 || capabilities.Height != 1080
                || !StringComparer.Ordinal.Equals(capabilities.FirmwareVersion, contract.FirmwareVersion) || !isCurrent(context))
            {
                return Failure("ThemeInitializationUnavailable", "会话、能力或冻结契约无效，未开始初始化");
            }
            if (powerIntent == PowerState.Off && !contract.ProbeSafeWhileOff)
            {
                // 此收据只表示没有执行数据面初始化，不声称观察到设备电源或 ready。
                return new OperationResult<ThemeInitializationReceipt>.Success(new(context, powerIntent, false, false, 0));
            }
            attempted = true;
            var response = await transport.ControlTransferAsync(contract.Probe, ReadOnlyMemory<byte>.Empty,
                contract.Expected.Length, token).ConfigureAwait(false);
            if (response is not OperationResult<ReadOnlyMemory<byte>>.Success probe)
            {
                return await AbortAsync(response switch
                {
                    OperationResult<ReadOnlyMemory<byte>>.Failure failure => failure.Error,
                    OperationResult<ReadOnlyMemory<byte>>.Unknown unknown => unknown.Error,
                    _ => AppError.Create("ThemeProbeCancelled", "EP0 核验取消，未继续初始化", "核实设备状态后重试")
                }, response is OperationResult<ReadOnlyMemory<byte>>.Unknown).ConfigureAwait(false);
            }
            if (!contract.MatchesProbe(probe.Value.Span, capabilities.Width, capabilities.Height))
            {
                return await AbortAsync(AppError.Create("ThemeProbeMismatch", "EP0 ready 位或尺寸与冻结契约不匹配",
                    "请核对固件版本及响应字段，不继续发送"), false).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (!isCurrent(context)) { return Failure("ThemeInitializationExpired", "EP0 核验期间会话已失效"); }
            if (contract.EdidProbe is { } edidSetup)
            {
                var edidResponse = await transport.ControlTransferAsync(edidSetup, ReadOnlyMemory<byte>.Empty,
                    ThemeDataPlaneContract.EdidBlockBytes, token).ConfigureAwait(false);
                if (edidResponse is not OperationResult<ReadOnlyMemory<byte>>.Success edid)
                {
                    return await AbortAsync(edidResponse switch
                    {
                        OperationResult<ReadOnlyMemory<byte>>.Failure failure => failure.Error,
                        OperationResult<ReadOnlyMemory<byte>>.Unknown unknown => unknown.Error,
                        _ => AppError.Create("ThemeEdidCancelled", "EDID 核验取消，未继续初始化", "核实设备状态后重试")
                    }, edidResponse is OperationResult<ReadOnlyMemory<byte>>.Unknown).ConfigureAwait(false);
                }
                if (!contract.MatchesEdid(edid.Value.Span, capabilities.Width, capabilities.Height))
                {
                    return await AbortAsync(AppError.Create("ThemeEdidMismatch", "EDID 结构、校验和或目标尺寸不匹配",
                        "请核对固件描述符，不继续发送"), false).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                if (!isCurrent(context)) { return Failure("ThemeInitializationExpired", "EDID 核验期间会话已失效"); }
            }
            if (powerIntent == PowerState.Off)
            {
                return new OperationResult<ThemeInitializationReceipt>.Success(new(context, powerIntent, true, false, 0));
            }
            if (parserClearance is null || parserClearance.DeviceKey != context.DeviceKey
                || parserClearance.SessionGeneration != context.SessionGeneration || parserClearance.OperationId != context.OperationId
                || !parserClearance.ParserCleared || !parserClearance.PreservedPowerIntent
                || !StringComparer.Ordinal.Equals(parserClearance.FirmwareVersion, capabilities.FirmwareVersion)
                || string.IsNullOrWhiteSpace(parserClearance.EvidenceReference))
            {
                return Failure("ParserClearanceRequired", "没有当前会话的 parser 清理确认，未发送亮屏命令");
            }
            (AfDisplayCommand Command, byte Value)[] sequence =
            [
                (AfDisplayCommand.ColorFormat, contract.ColorFormat),
                (AfDisplayCommand.WidthHigh, (byte)(capabilities.Width >> 8)),
                (AfDisplayCommand.WidthLow, (byte)capabilities.Width),
                (AfDisplayCommand.HeightHigh, (byte)(capabilities.Height >> 8)),
                (AfDisplayCommand.HeightLow, (byte)capabilities.Height),
                (AfDisplayCommand.Backlight, (byte)brightness),
                (AfDisplayCommand.AutoRlx, 0), (AfDisplayCommand.Mjpeg, 1), (AfDisplayCommand.BlankMode, 1)
            ];
            byte[] command = new byte[AfCommandEncoder.CommandBytes];
            foreach (var item in sequence)
            {
                token.ThrowIfCancellationRequested();
                if (!isCurrent(context))
                {
                    return await AbortAsync(AppError.Create("ThemeInitializationExpired", "初始化期间会话已失效",
                        "停止剩余命令并重新连接所选设备"), writeAttempted).ConfigureAwait(false);
                }
                var encoded = AfCommandEncoder.TryWrite(item.Command, item.Value, command);
                if (encoded is OperationResult<int>.Failure rejected)
                {
                    return await AbortAsync(rejected.Error, writeAttempted).ConfigureAwait(false);
                }
                writeAttempted = true;
                var result = await transport.WriteAsync(command, token).ConfigureAwait(false);
                if (!result.IsComplete || result.RequestedBytes != command.Length)
                {
                    return await AbortAsync(AppError.Create("ThemeHandshakeIncomplete", "亮屏初始化命令未完整完成",
                        "核实设备状态，不发送 JPEG 或重试剩余命令"), true).ConfigureAwait(false);
                }
                completedCommands++;
            }
            if (!isCurrent(context))
            {
                return await AbortAsync(AppError.Create("ThemeHandshakeLate", "初始化完成时会话已失效",
                    "核实所选设备状态后重新连接"), true).ConfigureAwait(false);
            }
            return new OperationResult<ThemeInitializationReceipt>.Success(new(context, powerIntent, true, true, completedCommands));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!attempted) { return new OperationResult<ThemeInitializationReceipt>.Cancelled(); }
            return await AbortAsync(AppError.Create("ThemeInitializationCancelled", "初始化中断，未继续发送",
                "核实实际设备状态后处理"), writeAttempted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "ThemeDataPlaneInitialization");
            var error = new AppError(report.ErrorId, "ThemeInitializationFailed", "主题初始化失败", "核实实际设备状态后处理");
            return attempted ? await AbortAsync(error, writeAttempted).ConfigureAwait(false)
                : new OperationResult<ThemeInitializationReceipt>.Failure(error);
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    public async Task<OperationResult<Unit>> DrainAsync()
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
        {
            return new OperationResult<Unit>.Failure(AppError.Create("ThemeInitializationBusy", "初始化尚未结束", "等待初始化结束后收敛"));
        }
        try { return await DrainOwnedAsync().ConfigureAwait(false); }
        finally { Volatile.Write(ref busy, 0); }
    }

    private async Task<OperationResult<ThemeInitializationReceipt>> AbortAsync(AppError error, bool effectsPossible)
    {
        var drained = await DrainOwnedAsync().ConfigureAwait(false);
        if (drained is not OperationResult<Unit>.Success || effectsPossible)
        {
            return new OperationResult<ThemeInitializationReceipt>.Unknown(error);
        }
        return new OperationResult<ThemeInitializationReceipt>.Failure(error);
    }

    private async Task<OperationResult<Unit>> DrainOwnedAsync()
    {
        try
        {
            var result = await transport.CancelAndDrainAsync(CancellationToken.None).ConfigureAwait(false);
            ioUnconfirmed = result is not OperationResult<Unit>.Success;
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ioUnconfirmed = true;
            var report = reporter.Report(exception, "ThemeInitializationDrain");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "ThemeInitializationDrainFailed",
                "初始化 I/O 收敛未确认，保留传输所有者", "请退出应用后重新连接设备"));
        }
    }

    private static OperationResult<ThemeInitializationReceipt> Failure(string code, string reason) =>
        new OperationResult<ThemeInitializationReceipt>.Failure(AppError.Create(code, reason, "请核实设备和固件契约，不自动重试"));
}
