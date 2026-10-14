using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Rendering.Scheduling;

internal sealed class LatestRenderScheduler : IAsyncDisposable
{
    private sealed record WorkItem(RenderRequest Request, CancellationToken Cancellation,
        TaskCompletionSource<OperationResult<EncodedFrame>> Completion);

    private readonly object syncRoot = new();
    private readonly AutoResetEvent available = new(false);
    private readonly CancellationTokenSource shutdown = new();
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<RenderRequest, CancellationToken, OperationResult<EncodedFrame>> render;
    private readonly IErrorReporter errorReporter;
    private WorkItem? active;
    private WorkItem? draft;
    private bool stopping;
    private int resourcesDisposed;

    public LatestRenderScheduler(Func<RenderRequest, CancellationToken, OperationResult<EncodedFrame>> render,
        IErrorReporter errorReporter)
    {
        this.render = render;
        this.errorReporter = errorReporter;
        var thread = new Thread(Run) { IsBackground = true, Name = "UsbDisplayHost.Renderer" };
        try
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch
        {
            available.Dispose();
            shutdown.Dispose();
            throw;
        }
    }

    public Task<OperationResult<EncodedFrame>> Enqueue(RenderRequest request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult<OperationResult<EncodedFrame>>(new OperationResult<EncodedFrame>.Cancelled());
        }
        if (!Enum.IsDefined(request.Purpose))
        {
            return Task.FromResult<OperationResult<EncodedFrame>>(new OperationResult<EncodedFrame>.Failure(
                AppError.Create("InvalidRenderPurpose", "渲染来源无效", "请重新打开主题页")));
        }
        var completion = new TaskCompletionSource<OperationResult<EncodedFrame>>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (syncRoot)
        {
            if (stopping)
            {
                completion.SetResult(new OperationResult<EncodedFrame>.Failure(
                    AppError.Create("RendererStopped", "渲染服务已经停止", "请重新打开应用")));
            }
            else
            {
                var work = new WorkItem(request, cancellationToken, completion);
                if (request.Purpose == FramePurpose.Active)
                {
                    active?.Completion.TrySetResult(new OperationResult<EncodedFrame>.Cancelled());
                    active = work;
                }
                else
                {
                    draft?.Completion.TrySetResult(new OperationResult<EncodedFrame>.Cancelled());
                    draft = work;
                }
                available.Set();
            }
        }
        return completion.Task;
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                available.WaitOne();
                while (true)
                {
                    WorkItem? work;
                    lock (syncRoot)
                    {
                        work = active ?? draft;
                        if (work == active) { active = null; }
                        else { draft = null; }
                        if (work is null && stopping) { return; }
                    }
                    if (work is null) { break; }
                    Execute(work);
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "RenderWorker");
            lock (syncRoot)
            {
                stopping = true;
                var failure = new OperationResult<EncodedFrame>.Failure(new AppError(report.ErrorId,
                    "RenderWorkerStopped", "渲染线程已停止", "请重启应用后重试"));
                active?.Completion.TrySetResult(failure);
                draft?.Completion.TrySetResult(failure);
                active = null;
                draft = null;
            }
        }
        finally { stopped.TrySetResult(); }
    }

    private void Execute(WorkItem work)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(work.Cancellation, shutdown.Token);
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            work.Completion.TrySetResult(render(work.Request, cancellation.Token));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            work.Completion.TrySetResult(new OperationResult<EncodedFrame>.Cancelled());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "FrameRender");
            work.Completion.TrySetResult(new OperationResult<EncodedFrame>.Failure(new AppError(
                report.ErrorId, "FrameRenderFailed", "内容生成失败，未生成可用新帧", "请检查素材和主题字段后重试")));
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (syncRoot)
        {
            if (!stopping)
            {
                stopping = true;
                active?.Completion.TrySetResult(new OperationResult<EncodedFrame>.Cancelled());
                draft?.Completion.TrySetResult(new OperationResult<EncodedFrame>.Cancelled());
                active = null;
                draft = null;
                shutdown.Cancel();
                available.Set();
            }
        }
        await stopped.Task.ConfigureAwait(false);
        if (Interlocked.Exchange(ref resourcesDisposed, 1) == 0)
        {
            available.Dispose();
            shutdown.Dispose();
        }
    }
}
