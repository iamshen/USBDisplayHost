using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Transport;

namespace UsbDisplayHost.Core.Sessions;

// 主题连接先核实实际控制状态；观察器和 parser 实现由匹配固件的生产适配器提供。
public sealed class ThemeConnectionSessionWork : IDeviceSessionWork
{
    private readonly DeviceSelectionAdmission admission;
    private readonly DeviceIdentity dataIdentity;
    private readonly long generation;
    private readonly PowerState powerIntent;
    private readonly DeviceCoordinator coordinator;
    private readonly IDisplayTransport transport;
    private readonly IApplicationSessionState observation;
    private readonly IFrameParserRecovery parser;
    private readonly ThemeDataPlaneInitializer initializer;
    private readonly IErrorReporter reporter;
    private readonly object sync = new();
    private readonly TaskCompletionSource<Unit> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<OperationResult<Unit>>? draining;
    private int started;
    private int closingRequested;
    private bool transferred, resourcesClosed;
    private Task<OperationResult<Unit>>? resourceClose;
    private ThemeSessionPipelineBuild? pipelineBuild;
    private TransportLimits? measuredLimits;
    private ThemeInitializationReceipt? confirmedInitialization;
    private FrameParserRecoveryReceipt? confirmedClearance;
    private bool openAttempted, parserAttempted, initializationAttempted, ready;

    public ThemeConnectionSessionWork(DeviceSelectionAdmission admission, DeviceIdentity dataIdentity, long generation,
        PowerState powerIntent, DeviceCoordinator coordinator, IDisplayTransport transport,
        IApplicationSessionState observation, IFrameParserRecovery parser, ThemeDataPlaneInitializer initializer, IErrorReporter reporter)
    {
        if (admission is null || dataIdentity is null || generation <= 0 || powerIntent is not (PowerState.On or PowerState.Off)
            || coordinator is null || transport is null || observation is null || parser is null || initializer is null || reporter is null
            || !ReferenceEquals(initializer.Transport, transport) || !admission.Selection.Interfaces.Contains(dataIdentity))
        { throw new ArgumentException("Theme connection dependencies must belong to the same selected session."); }
        this.admission = admission; this.dataIdentity = dataIdentity; this.generation = generation; this.powerIntent = powerIntent;
        this.coordinator = coordinator; this.transport = transport; this.observation = observation; this.parser = parser;
        this.initializer = initializer; this.reporter = reporter;
    }

    public async Task<OperationResult<DeviceObservation>> ExecuteAsync(OperationContext context, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref started, 1, 0) != 0)
        { return Failed("ThemeConnectionWorkReused", "该连接工作已经执行，不能重用"); }
        try
        {
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(context)) { return Failed("ThemeConnectionExpired", "连接上下文或发现凭据已变化"); }
            var prepared = await observation.PrepareConnectionAsync(context, token).ConfigureAwait(false);
            if (prepared is not OperationResult<Unit>.Success) { return Convert(prepared); }
            var before = await observation.ObserveAsync(context, token).ConfigureAwait(false);
            var controlDrain = await observation.DrainAsync().ConfigureAwait(false);
            if (controlDrain is not OperationResult<Unit>.Success) { return Convert(UnknownDrain(controlDrain)); }
            if (before is not OperationResult<DeviceObservation>.Success actual) { return before; }
            if (!Matches(actual.Value) || actual.Value.Power != powerIntent
                || powerIntent == PowerState.On && actual.Value.ObservedBrightness is null)
            { return Failed("ThemeConnectionControlUnverified", "实际主题模式、电源、亮度或能力与连接意图不一致，未打开数据面"); }
            if (!IsCurrent(context)) { return Failed("ThemeConnectionChangedBeforeOpen", "控制核验后连接上下文已变化"); }
            openAttempted = true;
            var opened = await transport.OpenAsync(dataIdentity, token).ConfigureAwait(false);
            if (opened is not OperationResult<TransportLimits>.Success limits) { return Convert(opened); }
            measuredLimits = limits.Value;
            if (limits.Value.MaximumPacketBytes is <= 0 or > 1024 || limits.Value.MaximumTransferBytes <= 11)
            { return Failed("ThemeConnectionTransferLimitMismatch", "实际传输限额不满足已核实能力，未初始化主题"); }
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(context)) { return Failed("ThemeConnectionChangedAfterOpen", "打开结束后连接上下文已变化，未初始化主题"); }
            FrameParserRecoveryReceipt? clearance = null;
            if (powerIntent == PowerState.On)
            {
                parserAttempted = true;
                var recovered = await parser.RecoverAsync(context, token).ConfigureAwait(false);
                var recoveredDrain = await parser.DrainAsync().ConfigureAwait(false);
                if (recoveredDrain is not OperationResult<Unit>.Success) { return Convert(UnknownDrain(recoveredDrain)); }
                if (recovered is not OperationResult<FrameParserRecoveryReceipt>.Success cleared) { return Convert(recovered); }
                clearance = cleared.Value;
            }
            initializationAttempted = true;
            // Off 分支不使用亮度发送命令；0 只是非亮屏路径的未使用参数，不是设备读回或默认亮度。
            var initialized = await initializer.InitializeAsync(context, powerIntent, actual.Value.ObservedBrightness ?? 0, clearance, token).ConfigureAwait(false);
            var initializedDrain = await initializer.DrainAsync().ConfigureAwait(false);
            if (initializedDrain is not OperationResult<Unit>.Success) { return Convert(UnknownDrain(initializedDrain)); }
            if (initialized is not OperationResult<ThemeInitializationReceipt>.Success receipt) { return Convert(initialized); }
            if (receipt.Value.Context != context || receipt.Value.PowerIntent != powerIntent
                || powerIntent == PowerState.On && (!receipt.Value.ProbeVerified || !receipt.Value.OnSequenceCompleted))
            { return Unconfirmed("ThemeConnectionInitializationUnconfirmed", "主题初始化收据未能核实本次连接"); }
            if (!IsCurrent(context)) { return Unconfirmed("ThemeConnectionChangedAfterInitialization", "初始化后的连接上下文已变化"); }
            var after = await observation.ObserveAsync(context, token).ConfigureAwait(false);
            if (after is not OperationResult<DeviceObservation>.Success observed) { return Convert(after); }
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(context) || !Matches(observed.Value) || observed.Value.Power != powerIntent
                || powerIntent == PowerState.On && observed.Value.ObservedBrightness != actual.Value.ObservedBrightness)
            { return Unconfirmed("ThemeConnectionReadbackMismatch", "初始化后的实际状态未匹配本次连接意图"); }
            confirmedInitialization = receipt.Value;
            confirmedClearance = clearance;
            ready = true;
            return after;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return openAttempted ? Unconfirmed("ThemeConnectionCancelledAfterOpen", "打开或初始化期间已取消，实际资源仍须收敛")
                : new OperationResult<DeviceObservation>.Cancelled();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "ThemeConnectionWork");
            return new OperationResult<DeviceObservation>.Unknown(new(report.ErrorId, "ThemeConnectionUnconfirmed",
                "主题连接未能确认，原所有者已保留", "请等待收敛并检查诊断，不重复打开或发送初始化命令"));
        }
        finally { finished.TrySetResult(new()); }
    }

    private bool IsCurrent(OperationContext context) => Volatile.Read(ref closingRequested) == 0 && context.DeviceKey == admission.Selection.Key
        && context.SessionGeneration == generation && coordinator.IsCurrent(context)
        && coordinator.Current.Connection is ConnectionState.Opening or ConnectionState.Recovering
        && admission.Validity.IsCurrent(admission.Selection);

    private bool Matches(DeviceObservation value)
    {
        var expected = initializer.Capabilities;
        return expected.IsProtocolMatched && value is not null && value.DeviceKey == admission.Selection.Key && admission.Selection.Interfaces.Contains(value.Identity)
            && value.Mode == DeviceMode.Theme && value.DriverVerified && value.Capabilities is { IsProtocolMatched: true } actual
            && actual.ProtocolVersion == expected.ProtocolVersion && actual.FirmwareVersion == expected.FirmwareVersion
            && actual.Width == expected.Width && actual.Height == expected.Height
            && actual.MaximumFrameBytes == expected.MaximumFrameBytes && actual.MaximumTransferBytes == expected.MaximumTransferBytes
            && actual.Source == expected.Source && actual.Commands.SetEquals(expected.Commands)
            && actual.HasPresentationFeedback == expected.HasPresentationFeedback && value.ObservedBrightness is not (< 0 or > 100)
            && !(value.Streaming == StreamingState.Running && value.Power != PowerState.On)
            && Enum.IsDefined(value.Streaming) && value.Streaming != StreamingState.Faulted;
    }

    public Task<OperationResult<Unit>> DrainAsync()
    {
        lock (sync)
        {
            if (Volatile.Read(ref started) == 0)
            { return Task.FromResult<OperationResult<Unit>>(new OperationResult<Unit>.Failure(AppError.Create("ThemeConnectionNotStarted",
                "连接工作尚未执行，不能报告收敛", "请通过协调器受理连接工作"))); }
            return draining ??= DrainCoreAsync();
        }
    }
    private async Task<OperationResult<Unit>> DrainCoreAsync()
    {
        await finished.Task.ConfigureAwait(false);
        try
        {
            var stateDrained = await observation.DrainAsync().ConfigureAwait(false);
            if (stateDrained is not OperationResult<Unit>.Success) { return UnknownDrain(stateDrained); }
            if (parserAttempted)
            {
                var parserDrained = await parser.DrainAsync().ConfigureAwait(false);
                if (parserDrained is not OperationResult<Unit>.Success) { return UnknownDrain(parserDrained); }
            }
            if (initializationAttempted)
            {
                var initializedDrain = await initializer.DrainAsync().ConfigureAwait(false);
                if (initializedDrain is not OperationResult<Unit>.Success) { return UnknownDrain(initializedDrain); }
            }
            if (openAttempted)
            {
                var transportDrained = await transport.CancelAndDrainAsync(CancellationToken.None).ConfigureAwait(false);
                if (transportDrained is not OperationResult<Unit>.Success) { return UnknownDrain(transportDrained); }
                if (!ready) { await transport.DisposeAsync().ConfigureAwait(false); }
            }
            if (!ready)
            {
                var controlClosed = await observation.CloseAsync().ConfigureAwait(false);
                if (controlClosed is not OperationResult<Unit>.Success) { return UnknownDrain(controlClosed); }
                resourcesClosed = true;
            }
            return new OperationResult<Unit>.Success(new());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "ThemeConnectionDrain");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "ThemeConnectionDrainUnconfirmed",
                "主题连接资源收敛未确认，所有者已保留", "请保留诊断，不能接管替代会话"));
        }
    }
    internal OperationResult<Unit> TransferTo(ThemeSessionPipeline pipeline)
    {
        lock (sync)
        {
            if (transferred || resourceClose is not null || !ready || resourcesClosed
                || pipelineBuild is null || !ReferenceEquals(pipelineBuild.Pipeline, pipeline)
                || !admission.Validity.IsCurrent(admission.Selection)
                || draining is not { IsCompletedSuccessfully: true, Result: OperationResult<Unit>.Success }
                || pipeline.DeviceKey != admission.Selection.Key || pipeline.SessionGeneration != generation
                || !ReferenceEquals(pipeline.Coordinator, coordinator) || !ReferenceEquals(pipeline.Transport, transport)
                || !ReferenceEquals(pipeline.Observation, observation) || !ReferenceEquals(pipeline.Parser, parser)
                || !ReferenceEquals(pipeline.Capabilities, initializer.Capabilities))
            { return new OperationResult<Unit>.Failure(AppError.Create("ThemeConnectionTransferUnavailable",
                "连接资源尚未确认或管线不属于同一物理会话", "请保留连接所有者，不接管不匹配的资源")); }
            transferred = true;
            return new OperationResult<Unit>.Success(new());
        }
    }

    public Task<OperationResult<ThemeSessionPipeline>> CreatePipelineAsync(ThemePipelineDependencies dependencies)
    {
        lock (sync)
        {
            var current = coordinator.Current;
            if (dependencies is null || dependencies.Renderer is null || dependencies.Profiles is null || dependencies.Assets is null
                || dependencies.Settings is null || dependencies.JpegProfile is null || dependencies.JpegValidator is null
                || dependencies.GetMetrics is null || dependencies.Clock is null
                || dependencies.JpegProfile.FirmwareVersion != initializer.Capabilities.FirmwareVersion
                || pipelineBuild is not null || transferred || resourceClose is not null || !ready || resourcesClosed || measuredLimits is null
                || confirmedInitialization is null
                || draining is not { IsCompletedSuccessfully: true, Result: OperationResult<Unit>.Success }
                || current.DeviceKey != admission.Selection.Key || current.SessionGeneration != generation
                || current.Connection != ConnectionState.Ready || current.PendingOperationId is not null || coordinator.CurrentActiveProfile is not null)
            { return Task.FromResult<OperationResult<ThemeSessionPipeline>>(new OperationResult<ThemeSessionPipeline>.Failure(AppError.Create(
                "ThemePipelineBuildUnavailable", "连接、固件格式或管线依赖尚未满足创建条件", "请使用同一就绪会话及实际兼容记录，不重复构建管线"))); }
            try
            {
                if (!admission.Validity.IsCurrent(admission.Selection))
                { return Task.FromResult<OperationResult<ThemeSessionPipeline>>(new OperationResult<ThemeSessionPipeline>.Failure(AppError.Create(
                    "ThemePipelineSelectionExpired", "管线创建前发现凭据已变化", "请等待原连接关闭后重新检测"))); }
                pipelineBuild = new(admission.Selection.Key, generation, coordinator, transport, observation, parser,
                    initializer.Capabilities, measuredLimits, confirmedInitialization, confirmedClearance, dependencies, reporter);
                var build = pipelineBuild;
                // 后续失效通知不能在连接工作锁内回调协调器，避免与关闭／交接锁顺序相反。
                return Task.Run(() => FinishPipelineBuildAsync(build));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = reporter.Report(exception, "ThemePipelineBuildAcceptance");
                return Task.FromResult<OperationResult<ThemeSessionPipeline>>(new OperationResult<ThemeSessionPipeline>.Failure(new(report.ErrorId,
                    "ThemePipelineBuildAcceptanceFailed", "管线创建未能受理，连接仍由原所有者持有", "请检查诊断并关闭原连接")));
            }
        }
    }
    private async Task<OperationResult<ThemeSessionPipeline>> FinishPipelineBuildAsync(ThemeSessionPipelineBuild build)
    {
        var result = await build.Completion.ConfigureAwait(false);
        if (result is OperationResult<ThemeSessionPipeline>.Success)
        {
            try
            {
                lock (sync)
                {
                    var current = coordinator.Current;
                    if (Volatile.Read(ref closingRequested) != 0 || resourceClose is not null || resourcesClosed
                        || current.DeviceKey != admission.Selection.Key || current.SessionGeneration != generation
                        || current.Connection != ConnectionState.Ready || current.PendingOperationId is not null
                        || !admission.Validity.IsCurrent(admission.Selection))
                    {
                        result = new OperationResult<ThemeSessionPipeline>.Failure(AppError.Create(
                            "ThemePipelineBuildExpired", "管线创建期间连接或发现凭据已变化，未交付管线",
                            "请等待原所有者关闭后重新检测，不接管已失效管线"));
                    }
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = reporter.Report(exception, "ThemePipelineBuildDelivery");
                result = new OperationResult<ThemeSessionPipeline>.Failure(new(report.ErrorId,
                    "ThemePipelineBuildDeliveryFailed", "管线交付核验未完成，资源仍由连接工作持有",
                    "请等待原连接关闭并检查诊断"));
            }
        }
        if (result is not OperationResult<ThemeSessionPipeline>.Success)
        {
            var error = result is OperationResult<ThemeSessionPipeline>.Failure failure ? failure.Error
                : AppError.Create("ThemePipelineBuildUnconfirmed", "主题管线创建未确认", "请关闭原连接并检查诊断");
            coordinator.NotifyDisconnected(admission.Selection.Key, generation, error);
        }
        return result;
    }

    internal Task<OperationResult<Unit>> CloseUnattachedAsync()
    {
        lock (sync)
        {
            if (transferred)
            { return Task.FromResult<OperationResult<Unit>>(new OperationResult<Unit>.Failure(AppError.Create("ThemeConnectionAlreadyTransferred",
                "连接资源已由完整管线接管", "请通过当前管线关闭，不重复释放连接工作"))); }
            Volatile.Write(ref closingRequested, 1);
            return resourceClose ??= Task.Run(CloseResourcesAsync);
        }
    }
    internal void ConfirmExecutionSkipped()
    {
        if (Interlocked.CompareExchange(ref started, 1, 0) == 0) { finished.TrySetResult(new()); }
    }
    private async Task<OperationResult<Unit>> CloseResourcesAsync()
    {
        try
        {
            await finished.Task.ConfigureAwait(false);
            if (pipelineBuild is not null)
            {
                var stopped = await pipelineBuild.StopAsync().ConfigureAwait(false);
                if (stopped is not OperationResult<Unit>.Success) { return UnknownDrain(stopped); }
            }
            var drained = await DrainAsync().ConfigureAwait(false);
            if (drained is not OperationResult<Unit>.Success) { return UnknownDrain(drained); }
            if (resourcesClosed) { return new OperationResult<Unit>.Success(new()); }
            await transport.DisposeAsync().ConfigureAwait(false);
            var closed = await observation.CloseAsync().ConfigureAwait(false);
            if (closed is not OperationResult<Unit>.Success) { return UnknownDrain(closed); }
            resourcesClosed = true;
            return new OperationResult<Unit>.Success(new());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "UnattachedThemeConnectionClose");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "UnattachedThemeConnectionUnconfirmed",
                "未接管连接的资源关闭未确认，所有者已保留", "请保留诊断，不创建替代连接"));
        }
    }
    private static OperationResult<Unit> UnknownDrain(OperationResult<Unit> result) => new OperationResult<Unit>.Unknown(result switch
    {
        OperationResult<Unit>.Failure failure => failure.Error,
        OperationResult<Unit>.Unknown unknown => unknown.Error,
        _ => AppError.Create("ThemeConnectionDrainUnconfirmed", "取消未证明连接资源已收敛", "请保留原所有者")
    });
    private OperationResult<DeviceObservation> Convert<T>(OperationResult<T> result) => result switch
    {
        OperationResult<T>.Failure failure => new OperationResult<DeviceObservation>.Failure(failure.Error),
        OperationResult<T>.Unknown unknown => new OperationResult<DeviceObservation>.Unknown(unknown.Error),
        _ => openAttempted ? Unconfirmed("ThemeConnectionStepCancelled", "打开后的连接步骤已取消，实际状态仍待核实")
            : new OperationResult<DeviceObservation>.Cancelled()
    };
    private static OperationResult<DeviceObservation> Failed(string code, string reason) =>
        new OperationResult<DeviceObservation>.Failure(AppError.Create(code, reason, "请核实实际控制状态、固件契约和当前选择"));
    private static OperationResult<DeviceObservation> Unconfirmed(string code, string reason) =>
        new OperationResult<DeviceObservation>.Unknown(AppError.Create(code, reason, "请等待资源收敛并重新核实设备，不重复初始化"));
}
