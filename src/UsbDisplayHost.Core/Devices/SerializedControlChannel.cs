using System.Threading.Channels;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;

namespace UsbDisplayHost.Core.Devices;

public sealed class SerializedControlChannel : IControlChannel
{
    private const int Capacity = 16;
    private readonly object sync = new();
    private readonly IControlChannel channel;
    private readonly Func<OperationContext, bool> isCurrent;
    private readonly IErrorReporter reporter;
    private readonly Channel<Entry> requests = Channel.CreateBounded<Entry>(new BoundedChannelOptions(Capacity)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly HashSet<OperationContext> ownedContexts = [];
    private readonly Task worker;
    private bool stopping;
    private AppError? executionFault;
    private Task<OperationResult<Unit>>? closing;

    public bool IsBusy { get { lock (sync) { return ownedContexts.Count != 0 || closing is { IsCompleted: false }; } } }

    private SerializedControlChannel(IControlChannel channel, Func<OperationContext, bool> isCurrent,
        IErrorReporter reporter)
    {
        this.channel = channel;
        this.isCurrent = isCurrent;
        this.reporter = reporter;
        worker = RunAsync();
    }

    // 调用方交出通道的独占所有权；队列不自行打开设备，也不创建替代通道。
    public static OperationResult<SerializedControlChannel> Create(IControlChannel channel,
        Func<OperationContext, bool> isCurrent, IErrorReporter reporter)
    {
        if (channel is null || isCurrent is null || reporter is null)
        { return new OperationResult<SerializedControlChannel>.Failure(Error("ControlQueueDependencies", "控制队列依赖不完整")); }
        return new OperationResult<SerializedControlChannel>.Success(new(channel, isCurrent, reporter));
    }

    public Task<OperationResult<ControlObservation>> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (request is null || request.Context.DeviceKey is null || request.Context.OperationId == Guid.Empty
                || request.Context.SessionGeneration < 0 || request.Context.ContentRevision < 0 || !Enum.IsDefined(request.Command))
            { return Rejected("InvalidQueuedControl", "控制请求的上下文或命令无效"); }
            if (cancellationToken.IsCancellationRequested)
            { return Task.FromResult<OperationResult<ControlObservation>>(new OperationResult<ControlObservation>.Cancelled()); }
            if (!isCurrent(request.Context)) { return Rejected("ExpiredQueuedControl", "控制请求所属操作已失效"); }
            lock (sync)
            {
                if (stopping) { return Rejected("ControlQueueClosing", "控制队列正在退出，不再受理请求"); }
                if (executionFault is not null)
                { return Task.FromResult<OperationResult<ControlObservation>>(new OperationResult<ControlObservation>.Failure(executionFault)); }
                if (ownedContexts.Count >= Capacity) { return Rejected("ControlQueueFull", "控制队列已满，未发送请求"); }
                if (!ownedContexts.Add(request.Context)) { return Rejected("DuplicateQueuedControl", "同一操作已有控制请求，未重复受理"); }
                Entry? entry = null;
                try
                {
                    entry = new Entry(request, cancellationToken);
                    if (!requests.Writer.TryWrite(entry))
                    {
                        ownedContexts.Remove(request.Context);
                        entry.Dispose();
                        return Rejected("ControlQueueUnavailable", "控制队列不可用，未发送请求");
                    }
                    return entry.Reply.Task;
                }
                catch
                {
                    ownedContexts.Remove(request.Context);
                    entry?.Dispose();
                    throw;
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "ControlQueueAdmission");
            return Task.FromResult<OperationResult<ControlObservation>>(new OperationResult<ControlObservation>.Failure(
                new(report.ErrorId, "ControlQueueAdmissionFailed", "控制请求未进入队列", "保留诊断，核实当前操作")));
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var entry in requests.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    if (!entry.TryBegin()) { continue; }
                    bool stop;
                    AppError? fault;
                    lock (sync) { stop = stopping; fault = executionFault; }
                    if (stop)
                    { entry.Reply.TrySetResult(new OperationResult<ControlObservation>.Cancelled()); continue; }
                    if (fault is not null)
                    { entry.Reply.TrySetResult(new OperationResult<ControlObservation>.Failure(fault)); continue; }
                    if (entry.Token.IsCancellationRequested)
                    { entry.Reply.TrySetResult(new OperationResult<ControlObservation>.Cancelled()); continue; }
                    if (!isCurrent(entry.Request.Context))
                    {
                        entry.Reply.TrySetResult(new OperationResult<ControlObservation>.Failure(
                            Error("ExpiredQueuedControl", "排队期间操作已失效，未发送请求")));
                        continue;
                    }
                    entry.Submitted = true;
                    var result = await channel.ExecuteAsync(entry.Request, entry.Token).ConfigureAwait(false);
                    if (result is null || result is OperationResult<ControlObservation>.Success success
                        && (success.Value is null || success.Value.Context != entry.Request.Context || !isCurrent(entry.Request.Context)))
                    {
                        result = new OperationResult<ControlObservation>.Unknown(
                            Error("QueuedControlDeliveryUnconfirmed", "控制结果缺失或所属操作已变化，实际结果待确认"));
                    }
                    entry.Reply.TrySetResult(result);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    var report = reporter.Report(exception, "ControlQueueExecution");
                    var error = new AppError(report.ErrorId, "ControlQueueExecutionUnconfirmed",
                        "控制执行异常，原通道结果待确认", "保留原所有者和诊断，不自动重发");
                    if (entry.Submitted) { lock (sync) { executionFault ??= error; } }
                    entry.Reply.TrySetResult(entry.Submitted
                        ? new OperationResult<ControlObservation>.Unknown(error)
                        : new OperationResult<ControlObservation>.Failure(error));
                }
                finally
                {
                    lock (sync) { ownedContexts.Remove(entry.Request.Context); }
                    DisposeEntry(entry);
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "ControlQueueWorker");
            var error = new AppError(report.ErrorId, "ControlQueueWorkerFailed", "控制调度停止，未执行的请求已拒绝", "保留原通道并正常关闭");
            lock (sync) { stopping = true; executionFault ??= error; requests.Writer.TryComplete(); }
            while (requests.Reader.TryRead(out var entry))
            {
                entry.Reply.TrySetResult(new OperationResult<ControlObservation>.Failure(error));
                lock (sync) { ownedContexts.Remove(entry.Request.Context); }
                DisposeEntry(entry);
            }
        }
    }

    public async IAsyncEnumerable<ControlObservation> ObserveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IAsyncEnumerator<ControlObservation>? enumerator = null;
        try { enumerator = channel.ObserveAsync(cancellationToken).GetAsyncEnumerator(cancellationToken); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { reporter.Report(exception, "ControlQueueObservationStart"); }
        if (enumerator is null) { yield break; }
        try
        {
            while (true)
            {
                ControlObservation? observation;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) { break; }
                    observation = enumerator.Current;
                    if (observation is null) { throw new InvalidOperationException("Missing control observation"); }
                    if (!isCurrent(observation.Context))
                    {
                        observation = observation with { Accepted = false, Completed = false, Mode = DeviceMode.Unknown,
                            Power = PowerState.Unknown, Brightness = null, Error = Error("ExpiredControlObservation", "观察所属操作已失效") };
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { reporter.Report(exception, "ControlQueueObservation"); break; }
                yield return observation;
            }
        }
        finally
        {
            try { await enumerator.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { reporter.Report(exception, "ControlQueueObservationClose"); }
        }
    }

    public Task<OperationResult<Unit>> CloseAsync()
    {
        lock (sync)
        {
            stopping = true;
            requests.Writer.TryComplete();
            return closing ??= CloseOwnedAsync();
        }
    }

    private async Task<OperationResult<Unit>> CloseOwnedAsync()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            // 先关闭原通道，允许原生所有者取消并收敛当前请求，随后等待调度退出。
            var result = await channel.CloseAsync().ConfigureAwait(false);
            await worker.ConfigureAwait(false);
            return result ?? new OperationResult<Unit>.Unknown(Error("ControlQueueCloseMissing", "原控制通道未交付关闭结果"));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "ControlQueueClose");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "ControlQueueCloseUnconfirmed",
                "控制队列关闭未确认，原所有者仍保留", "保留诊断，不创建替代通道"));
        }
    }

    public async ValueTask DisposeAsync() => _ = await CloseAsync().ConfigureAwait(false);

    private void DisposeEntry(Entry entry)
    {
        try { entry.Dispose(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { reporter.Report(exception, "ControlQueueCancellationRelease"); }
    }

    private static AppError Error(string code, string reason) => AppError.Create(code, reason,
        "核实当前会话和原请求，不自动重试或报告设备已就绪");

    private static Task<OperationResult<ControlObservation>> Rejected(string code, string reason) =>
        Task.FromResult<OperationResult<ControlObservation>>(new OperationResult<ControlObservation>.Failure(Error(code, reason)));

    private sealed class Entry : IDisposable
    {
        private int state;
        private readonly CancellationTokenRegistration cancellation;
        public Entry(ControlRequest request, CancellationToken token)
        {
            Request = request;
            Token = token;
            cancellation = token.UnsafeRegister(static value => ((Entry)value!).CancelQueued(), this);
        }
        public ControlRequest Request { get; }
        public CancellationToken Token { get; }
        public bool Submitted { get; set; }
        public TaskCompletionSource<OperationResult<ControlObservation>> Reply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TryBegin() => Interlocked.CompareExchange(ref state, 1, 0) == 0;
        private void CancelQueued()
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
            { Reply.TrySetResult(new OperationResult<ControlObservation>.Cancelled()); }
        }
        public void Dispose() => cancellation.Dispose();
    }
}
