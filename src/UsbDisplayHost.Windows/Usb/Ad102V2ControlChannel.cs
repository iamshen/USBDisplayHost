using System.Runtime.CompilerServices;
using System.Threading.Channels;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;

namespace UsbDisplayHost.Windows.Usb;

// 返回命令受理和只读查询结果；模式完成仍由重枚举目标证据交付。
public sealed class Ad102V2ControlChannel : IControlChannel
{
    private readonly object sync = new();
    private readonly Ad102V2ModeController controller;
    private readonly Func<OperationContext, bool> isCurrent;
    private readonly Func<OperationContext, OperationResult<PowerState>> capturePowerIntent;
    private readonly IErrorReporter reporter;
    private readonly SemaphoreSlim observationSlots = new(16, 16);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<ControlObservation> observations = Channel.CreateBounded<ControlObservation>(new BoundedChannelOptions(16)
    { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly Dictionary<OperationContext, Task> lateWatchers = [];
    private Task<OperationResult<ControlObservation>>? execution;
    private Task<OperationResult<Unit>>? closing;
    private bool stopping;
    private int observing;

    private Ad102V2ControlChannel(Ad102V2ModeController controller, Func<OperationContext, bool> isCurrent,
        Func<OperationContext, OperationResult<PowerState>> capturePowerIntent, IErrorReporter reporter)
    { this.controller = controller; this.isCurrent = isCurrent; this.capturePowerIntent = capturePowerIntent; this.reporter = reporter; }

    public static OperationResult<Ad102V2ControlChannel> Create(Ad102V2ModeController controller,
        Func<OperationContext, bool> isCurrent, Func<OperationContext, OperationResult<PowerState>> capturePowerIntent,
        IErrorReporter reporter)
    {
        if (controller is null || isCurrent is null || capturePowerIntent is null || reporter is null)
        { return Failure<Ad102V2ControlChannel>("V2ChannelServicesMissing", "控制通道缺少原控制所有者、上下文或电源意图服务"); }
        var channel = new Ad102V2ControlChannel(controller, isCurrent, capturePowerIntent, reporter);
        if (controller.TryAttachChannelOwner(channel)) { return new OperationResult<Ad102V2ControlChannel>.Success(channel); }
        channel.observationSlots.Dispose(); channel.lifetime.Dispose();
        return Failure<Ad102V2ControlChannel>("V2ChannelAlreadyOwned", "原模式控制已接入通道、正在执行或已失效");
    }

    public Task<OperationResult<ControlObservation>> ExecuteAsync(ControlRequest request, CancellationToken token)
    {
        lock (sync)
        {
            if (stopping || execution is { IsCompleted: false })
            { return Task.FromResult(Failure<ControlObservation>("V2ChannelBusy", "通道停止或已有控制执行者，未受理新命令")); }
            return execution = ExecuteOwnedAsync(request, token);
        }
    }

    private async Task<OperationResult<ControlObservation>> ExecuteOwnedAsync(ControlRequest request, CancellationToken token)
    {
        // 受理任务先交由所有者保存，关闭流程才能等待它完成。
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        int ownedSlots = 0;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            if (cancellation.IsCancellationRequested) { return new OperationResult<ControlObservation>.Cancelled(); }
            if (request is null || !Matches(request.Context) || !isCurrent(request.Context))
            { return Failure<ControlObservation>("V2ChannelContextExpired", "控制请求不属于当前物理会话或操作"); }
            if (request.Command is not (DeviceCommand.SwitchMode or DeviceCommand.QueryState))
            { return Failure<ControlObservation>("V2ChannelCommandUnsupported", "当前适配器尚无已核实的亮度或电源控制契约"); }
            if (!observationSlots.Wait(0))
            { return Failure<ControlObservation>("V2ChannelObservationsFull", "控制结果尚未消费，未受理新的设备操作"); }
            ownedSlots++;
            Task<OperationResult<Ad102V2ModeAcknowledgement>>? lateAcknowledgement = null;
            OperationResult<ControlObservation> result;
            if (request.Command == DeviceCommand.SwitchMode)
            {
                if (request.Value != (int)DeviceMode.Theme && request.Value != (int)DeviceMode.SecondaryDisplay)
                { return Failure<ControlObservation>("V2ChannelTargetInvalid", "模式目标不是主题或副屏"); }
                var power = capturePowerIntent(request.Context);
                if (power is not OperationResult<PowerState>.Success intent)
                {
                    return power switch
                    {
                        OperationResult<PowerState>.Failure failure => new OperationResult<ControlObservation>.Failure(failure.Error),
                        OperationResult<PowerState>.Unknown unknown => new OperationResult<ControlObservation>.Unknown(unknown.Error),
                        _ => new OperationResult<ControlObservation>.Cancelled()
                    };
                }
                if (!isCurrent(request.Context)) { return Failure<ControlObservation>("V2ChannelContextChanged", "电源意图捕获后操作已失效"); }
                lock (sync)
                {
                    if (lateWatchers.ContainsKey(request.Context))
                    { return Failure<ControlObservation>("V2ChannelOperationStillObserved", "原操作仍在等待迟到应答，不能复用操作上下文"); }
                }
                // 在发送前预留迟到结果位置，未知返回后也不依赖 UI 空闲来保存应答。
                if (!observationSlots.Wait(0))
                { return Failure<ControlObservation>("V2ChannelLateObservationFull", "迟到控制结果没有可用位置，未发送模式命令"); }
                ownedSlots++;
                var response = await controller.SwitchAsync(request.Context, (DeviceMode)request.Value, intent.Value,
                    cancellation.Token).ConfigureAwait(false);
                if (response is OperationResult<Ad102V2ModeAcknowledgement>.Unknown)
                { lateAcknowledgement = controller.CaptureAcknowledgement(request.Context); }
                result = response switch
                {
                    OperationResult<Ad102V2ModeAcknowledgement>.Success actual when actual.Value.Result == Ad102V2HidResult.Accepted =>
                        new OperationResult<ControlObservation>.Success(new(request.Context, true, false,
                            DeviceMode.Unknown, PowerState.Unknown, null, null)),
                    OperationResult<Ad102V2ModeAcknowledgement>.Success actual => Failure<ControlObservation>(
                        $"V2Mode{actual.Value.Result}", "固件明确拒绝模式请求，未确认切换"),
                    OperationResult<Ad102V2ModeAcknowledgement>.Failure failure => new OperationResult<ControlObservation>.Failure(failure.Error),
                    OperationResult<Ad102V2ModeAcknowledgement>.Unknown unknown => new OperationResult<ControlObservation>.Unknown(unknown.Error),
                    _ => new OperationResult<ControlObservation>.Cancelled()
                };
            }
            else
            {
                if (request.Value != 0) { return Failure<ControlObservation>("V2StateQueryValueInvalid", "状态查询不接受控制值"); }
                var queried = await controller.QueryFeatureAsync(cancellation.Token).ConfigureAwait(false);
                result = queried switch
                {
                    OperationResult<HidFeatureReceipt>.Success actual => new OperationResult<ControlObservation>.Success(
                        new(request.Context, true, true, actual.Value.Codec.Feature.MaintenanceBusy
                            || actual.Value.Codec.Feature.CurrentMode != actual.Value.Codec.Feature.TargetMode
                            ? DeviceMode.Unknown : actual.Value.Codec.Feature.CurrentMode switch
                        { Ad102V2Mode.Theme => DeviceMode.Theme, Ad102V2Mode.Screen => DeviceMode.SecondaryDisplay, _ => DeviceMode.Unknown },
                            PowerState.Unknown, null, null)),
                    OperationResult<HidFeatureReceipt>.Failure failure => new OperationResult<ControlObservation>.Failure(failure.Error),
                    OperationResult<HidFeatureReceipt>.Unknown unknown => new OperationResult<ControlObservation>.Unknown(unknown.Error),
                    _ => new OperationResult<ControlObservation>.Cancelled()
                };
            }
            if (Volatile.Read(ref stopping) || !isCurrent(request.Context))
            { result = new OperationResult<ControlObservation>.Unknown(AppError.Create("V2ChannelExpiredBeforeDelivery",
                "控制结果交付前操作已失效", "核实当前会话，旧结果不能完成新操作")); }
            var observation = result switch
            {
                OperationResult<ControlObservation>.Success actual => actual.Value,
                OperationResult<ControlObservation>.Failure failure => new(request.Context, false, false, DeviceMode.Unknown, PowerState.Unknown, null, failure.Error),
                OperationResult<ControlObservation>.Unknown unknown => new(request.Context, false, false, DeviceMode.Unknown, PowerState.Unknown, null, unknown.Error),
                _ => null
            };
            if (observation is not null)
            {
                if (!observations.Writer.TryWrite(observation))
                { return new OperationResult<ControlObservation>.Unknown(AppError.Create("V2ChannelPublicationFailed",
                    "控制结果分发未确认", "保留原请求并核实状态，不重复发送")); }
                ownedSlots--;
                if (lateAcknowledgement is not null)
                {
                    lock (sync)
                    {
                        if (!stopping && !lateWatchers.ContainsKey(request.Context))
                        {
                            lateWatchers.Add(request.Context, ObserveLateAsync(request.Context, lateAcknowledgement));
                            ownedSlots--;
                        }
                    }
                }
            }
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "V2ControlChannelExecute");
            return new OperationResult<ControlObservation>.Unknown(new(report.ErrorId, "V2ChannelExecuteFailed",
                "控制结果待确认", "保留原会话和请求，查看诊断，不自动重发"));
        }
        finally { if (ownedSlots != 0) { observationSlots.Release(ownedSlots); } }
    }

    private async Task ObserveLateAsync(OperationContext context, Task<OperationResult<Ad102V2ModeAcknowledgement>> original)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        bool published = false;
        try
        {
            // 取消仅结束观察；原应答任务和原生 I/O 仍由控制所有者保留。
            var response = await original.WaitAsync(lifetime.Token).ConfigureAwait(false);
            if (!Matches(context) || !isCurrent(context) || !controller.CanDeliverAcknowledgement(context)) { return; }
            ControlObservation? observation = response switch
            {
                OperationResult<Ad102V2ModeAcknowledgement>.Success actual when actual.Value.Request.Context != context => null,
                OperationResult<Ad102V2ModeAcknowledgement>.Success actual when actual.Value.Result == Ad102V2HidResult.Accepted =>
                    new(context, true, false, DeviceMode.Unknown, PowerState.Unknown, null, null),
                OperationResult<Ad102V2ModeAcknowledgement>.Success actual => new(context, false, false, DeviceMode.Unknown,
                    PowerState.Unknown, null, AppError.Create($"V2Mode{actual.Value.Result}",
                        "固件迟到应答明确拒绝原模式请求", "核实原操作结果，不将它关联到新操作")),
                OperationResult<Ad102V2ModeAcknowledgement>.Failure failure =>
                    new(context, false, false, DeviceMode.Unknown, PowerState.Unknown, null, failure.Error),
                OperationResult<Ad102V2ModeAcknowledgement>.Unknown unknown =>
                    new(context, false, false, DeviceMode.Unknown, PowerState.Unknown, null, unknown.Error),
                _ => null
            };
            lock (sync)
            {
                if (!stopping && observation is not null) { published = observations.Writer.TryWrite(observation); }
            }
            if (observation is not null && !published && !lifetime.IsCancellationRequested)
            { reporter.Report(new IOException("Reserved late HID observation could not be published."), "V2LateObservationPublish"); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { reporter.Report(exception, "V2LateObservation"); }
        finally
        {
            if (!published) { observationSlots.Release(); }
            lock (sync) { lateWatchers.Remove(context); }
        }
    }

    public async IAsyncEnumerable<ControlObservation> ObserveAsync([EnumeratorCancellation] CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref observing, 1, 0) != 0) { yield break; }
        try
        {
            while (true)
            {
                ControlObservation observation;
                try
                {
                    if (!await observations.Reader.WaitToReadAsync(token).ConfigureAwait(false)) { break; }
                    if (!observations.Reader.TryRead(out observation!)) { continue; }
                    observationSlots.Release();
                    if (!isCurrent(observation.Context))
                    {
                        observation = observation with { Accepted = false, Completed = false, Mode = DeviceMode.Unknown,
                            Power = PowerState.Unknown, Brightness = null, Error = AppError.Create("V2ObservationExpired",
                                "控制结果消费时操作已失效", "核实当前会话，不用旧结果完成新操作") };
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { reporter.Report(exception, "V2ControlChannelObserve"); break; }
                yield return observation;
            }
        }
        finally { Volatile.Write(ref observing, 0); }
    }

    private bool Matches(OperationContext context) => context.DeviceKey == controller.Input.DeviceKey
        && context.SessionGeneration == controller.Input.SessionGeneration && context.OperationId != Guid.Empty
        && context.ContentRevision >= 0;

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
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            var result = await controller.CloseAsync().ConfigureAwait(false);
            Task<OperationResult<ControlObservation>>? original;
            lock (sync) { original = execution; }
            if (original is not null) { await original.ConfigureAwait(false); }
            Task[] originalWatchers;
            lock (sync) { originalWatchers = lateWatchers.Values.ToArray(); }
            await Task.WhenAll(originalWatchers).ConfigureAwait(false);
            if (result is OperationResult<Unit>.Success) { lifetime.Dispose(); }
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "V2ControlChannelClose");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "V2ChannelCloseUnconfirmed",
                "控制通道关闭未确认，原所有者保留", "保留诊断，不创建替代通道"));
        }
        finally { observations.Writer.TryComplete(); }
    }

    public async ValueTask DisposeAsync() => _ = await CloseAsync().ConfigureAwait(false);

    private static OperationResult<T> Failure<T>(string code, string reason) =>
        new OperationResult<T>.Failure(AppError.Create(code, reason, "核实当前上下文、能力和原请求，不自动重试"));
}
