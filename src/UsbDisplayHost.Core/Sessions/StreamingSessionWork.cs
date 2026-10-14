using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Transport;

namespace UsbDisplayHost.Core.Sessions;

// 暂停仅控制主机帧通道；模式和电源仍由实际会话观察器确认。
internal sealed class StreamingSessionWork(DeviceCoordinator coordinator, FrameBoundaryScheduler scheduler,
    IApplicationSessionState session, bool resume, IErrorReporter reporter) : IDeviceSessionWork, IFrameBoundaryWork
{
    private readonly TaskCompletionSource<Unit> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private OperationResult<DeviceObservation>? observation;
    private Task<OperationResult<Unit>>? draining;
    private bool allowsStreaming;
    private int started;
    public bool AllowsFrameStreaming => allowsStreaming;

    public async Task<OperationResult<DeviceObservation>> ExecuteAsync(OperationContext context, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref started, 1, 0) != 0)
        { return Failure("StreamingWorkReused", "该推流操作已经受理"); }
        try
        {
            var result = await scheduler.ScheduleControlAsync(context, this, token).ConfigureAwait(false);
            return result switch
            {
                OperationResult<Unit>.Success => observation ?? Failure("StreamingObservationMissing", "推流边界未提供核实结果"),
                OperationResult<Unit>.Failure failure => new OperationResult<DeviceObservation>.Failure(failure.Error),
                OperationResult<Unit>.Unknown unknown => new OperationResult<DeviceObservation>.Unknown(unknown.Error),
                _ => new OperationResult<DeviceObservation>.Cancelled()
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "StreamingSessionWork");
            return new OperationResult<DeviceObservation>.Unknown(new(report.ErrorId, "StreamingChangeUnconfirmed",
                "推流状态变更未确认", "请保留当前会话并检查诊断，不重复设备操作"));
        }
        finally { finished.TrySetResult(new()); }
    }

    async Task<OperationResult<Unit>> IFrameBoundaryWork.ExecuteAsync(OperationContext context, CancellationToken token)
    {
        if (!coordinator.IsCurrent(context) || scheduler.Executor.Current.State != FrameTransferState.Ready
            || scheduler.Current.IoUnconfirmed || scheduler.FrameRate.RequiresRecovery
            || resume && coordinator.CurrentActiveProfile is { } active
                && (active.Context.DeviceKey != context.DeviceKey || active.Context.SessionGeneration != context.SessionGeneration))
        { return new OperationResult<Unit>.Failure(AppError.Create("StreamingChannelUnavailable",
            "原活动配置或帧通道尚未确认可用", "请核实当前会话，不通过恢复推流清除通道故障")); }
        // 帧边界已经授予；后续取消不能把实际通道变更当作未执行。
        observation = await session.ObserveAsync(context, CancellationToken.None).ConfigureAwait(false);
        if (observation is not OperationResult<DeviceObservation>.Success observed)
        {
            return new OperationResult<Unit>.Unknown(observation switch
            {
                OperationResult<DeviceObservation>.Failure failure => failure.Error,
                OperationResult<DeviceObservation>.Unknown unknown => unknown.Error,
                _ => AppError.Create("StreamingObservationUnconfirmed", "帧边界后的设备核实未完成", "请保留会话并检查诊断")
            });
        }
        var value = observed.Value;
        if (!coordinator.IsCurrent(context) || value.DeviceKey != context.DeviceKey
            || value.Mode != DeviceMode.Theme || value.Power != PowerState.On
            || !value.DriverVerified || value.Capabilities is not { IsProtocolMatched: true }
            || value.Capabilities != scheduler.Executor.Capabilities)
        { return new OperationResult<Unit>.Unknown(AppError.Create("StreamingSessionChanged",
            "原设备或主题电源状态已变化，未确认推流变更", "请核实当前设备，不重发草稿")); }
        observation = new OperationResult<DeviceObservation>.Success(value with
        { Streaming = resume ? StreamingState.Running : StreamingState.Paused });
        allowsStreaming = resume;
        return new OperationResult<Unit>.Success(new());
    }

    Task<OperationResult<Unit>> IFrameBoundaryWork.DrainAsync() => draining ??= DrainObservationAsync();
    private async Task<OperationResult<Unit>> DrainObservationAsync()
    {
        try { return await session.DrainAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "StreamingObservationDrain");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "StreamingDrainUnconfirmed",
                "推流核实 I/O 收敛未确认", "请保留原会话，不受理替代连接"));
        }
    }
    public async Task<OperationResult<Unit>> DrainAsync()
    {
        if (Volatile.Read(ref started) != 0) { await finished.Task.ConfigureAwait(false); }
        return draining is { } accepted ? await accepted.ConfigureAwait(false) : new OperationResult<Unit>.Success(new());
    }
    private static OperationResult<DeviceObservation> Failure(string code, string reason) =>
        new OperationResult<DeviceObservation>.Failure(AppError.Create(code, reason, "请读取当前设备与推流状态"));
}
