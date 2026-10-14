using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Transport;

namespace UsbDisplayHost.Core.Sessions;

public sealed class ProtocolControlWork(
    Func<OperationContext, CancellationToken, Task<OperationResult<DeviceObservation>>> execute,
    Func<Task<OperationResult<Unit>>> drain,
    FrameBoundaryScheduler? scheduler = null,
    bool resumeFrames = false,
    FrameTransferExecutor? recovery = null) : IDeviceSessionWork, IFrameBoundaryWork
{
    private OperationResult<DeviceObservation>? result;
    private Task<OperationResult<Unit>>? draining;
    public bool AllowsFrameStreaming => resumeFrames && result is OperationResult<DeviceObservation>.Success
        { Value.Mode: DeviceMode.Theme, Value.Power: PowerState.On };

    public async Task<OperationResult<DeviceObservation>> ExecuteAsync(OperationContext context, CancellationToken token)
    {
        if (scheduler is null) { return result = await execute(context, token).ConfigureAwait(false); }
        var completed = await scheduler.ScheduleControlAsync(context, this, token).ConfigureAwait(false);
        return completed switch
        {
            OperationResult<Unit>.Success => result!,
            OperationResult<Unit>.Failure failure => new OperationResult<DeviceObservation>.Failure(failure.Error),
            OperationResult<Unit>.Unknown unknown => new OperationResult<DeviceObservation>.Unknown(unknown.Error),
            _ => new OperationResult<DeviceObservation>.Cancelled()
        };
    }

    async Task<OperationResult<Unit>> IFrameBoundaryWork.ExecuteAsync(OperationContext context, CancellationToken token)
    {
        result = await execute(context, token).ConfigureAwait(false);
        if (result is OperationResult<DeviceObservation>.Success && recovery is not null)
        {
            var recovered = await recovery.RecoverAsync(context, token).ConfigureAwait(false);
            if (recovered is not OperationResult<Unit>.Success) { return recovered; }
        }
        return result switch
        {
            OperationResult<DeviceObservation>.Success => new OperationResult<Unit>.Success(new()),
            OperationResult<DeviceObservation>.Failure failure => new OperationResult<Unit>.Failure(failure.Error),
            OperationResult<DeviceObservation>.Unknown unknown => new OperationResult<Unit>.Unknown(unknown.Error),
            _ => new OperationResult<Unit>.Cancelled()
        };
    }
    public Task<OperationResult<Unit>> DrainAsync() => draining ??= drain();
}
