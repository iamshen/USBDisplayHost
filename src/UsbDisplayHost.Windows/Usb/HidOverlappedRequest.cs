using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Windows.Usb;

internal enum HidIoDirection { Input, Output }
internal sealed record HidIoOutcome(UsbWriteResult Status, ImmutableArray<byte> Data, bool WasSubmitted);

// 同一文件各有一个读取／写入位置；未知完成的请求保留全部原生资源及位置。
internal sealed class HidOverlappedRequest
{
    private static readonly object ownersSync = new();
    private static readonly Dictionary<(SafeUsbFileHandle, HidIoDirection), HidOverlappedRequest> owners = [];
    private readonly object sync = new();
    private readonly SafeUsbFileHandle file;
    private readonly HidIoDirection direction;
    private readonly byte[] bytes;
    private readonly IErrorReporter reporter;
    private readonly Func<bool> canSubmit;
    private readonly EventWaitHandle completionEvent = new(false, EventResetMode.ManualReset);
    private readonly TaskCompletionSource cancellationSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource delayCancellation = new();
    private CancellationTokenRegistration cancellation;
    private Task<HidIoOutcome>? completion;
    private nint buffer, overlapped;
    private bool fileReference, submitted, cancelRequested, released, knownComplete;
    private bool deadlineExpired;

    private HidOverlappedRequest(SafeUsbFileHandle file, HidIoDirection direction, byte[] bytes, IErrorReporter reporter, Func<bool> canSubmit)
    { this.file = file; this.direction = direction; this.bytes = bytes; this.reporter = reporter; this.canSubmit = canSubmit; }

    internal static OperationResult<HidOverlappedRequest> Create(SafeUsbFileHandle file, HidIoDirection direction,
        int reportBytes, ReadOnlyMemory<byte> output, IErrorReporter reporter, Func<bool> canSubmit)
    {
        if (file is null || file.IsInvalid || file.IsClosed || reporter is null || canSubmit is null || !Enum.IsDefined(direction)
            || reportBytes is < 1 or > 4096
            || direction == HidIoDirection.Input && !output.IsEmpty
            || direction == HidIoDirection.Output && output.Length != reportBytes)
        { return Failed("InvalidHidIoRequest", "HID 句柄、实际报告长度或方向无效"); }
        HidOverlappedRequest? request = null;
        try
        {
            lock (ownersSync)
            {
                if (owners.Count >= 32 || owners.ContainsKey((file, direction)))
                { return Failed("HidIoOwnerOccupied", "HID 方向已有请求或全局请求达到限额"); }
                request = new(file, direction, direction == HidIoDirection.Input ? new byte[reportBytes] : output.ToArray(), reporter, canSubmit);
                owners.Add((file, direction), request);
            }
            file.DangerousAddRef(ref request.fileReference);
            request.buffer = Marshal.AllocHGlobal(reportBytes);
            Marshal.Copy(request.bytes, 0, request.buffer, reportBytes);
            request.overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<HidIoNative.OverlappedData>());
            Marshal.StructureToPtr(new HidIoNative.OverlappedData
                { Event = request.completionEvent.SafeWaitHandle.DangerousGetHandle() }, request.overlapped, false);
            return new OperationResult<HidOverlappedRequest>.Success(request);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "HidIoAllocation");
            try { request?.ReleaseResources(); }
            catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
            {
                var cleanupReport = reporter.Report(cleanup, "HidIoAllocationCleanup");
                return new OperationResult<HidOverlappedRequest>.Unknown(new(cleanupReport.ErrorId, "HidIoAllocationCleanupUnconfirmed",
                    "HID 请求准备失败且资源清理未确认", "保留原所有者与诊断，不创建替代请求"));
            }
            return new OperationResult<HidOverlappedRequest>.Failure(new(report.ErrorId, "HidIoAllocationFailed",
                "HID 请求资源未能准备，未执行读写", "查看诊断并等待原所有者收敛"));
        }
    }

    internal async Task<HidIoOutcome> RunAsync(int timeoutMilliseconds, TimeProvider clock, CancellationToken token)
    {
        Task delay;
        Task<HidIoOutcome> owner;
        bool acceptanceFailed = false;
        lock (sync)
        {
            if (completion is not null || released) { return Outcome(0, 87, false, knownComplete); }
            if ((timeoutMilliseconds is < 1 or > 60000
                && !(direction == HidIoDirection.Input && timeoutMilliseconds == Timeout.Infinite)) || clock is null)
            {
                knownComplete = true;
                completion = FinishWithoutSubmissionAsync(Outcome(0, 87, false, true));
                owner = completion;
                delay = Task.CompletedTask;
                acceptanceFailed = true;
            }
            else try
            {
                delay = Task.Delay(timeoutMilliseconds == Timeout.Infinite ? Timeout.InfiniteTimeSpan
                    : TimeSpan.FromMilliseconds(timeoutMilliseconds), clock, delayCancellation.Token);
                cancellation = token.Register(static state => ((HidOverlappedRequest)state!).RequestCancellation(), this);
                completion = CompleteOwnedAsync(Execute);
                owner = completion;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                reporter.Report(exception, "HidIoAcceptance");
                knownComplete = !submitted;
                completion = FinishWithoutSubmissionAsync(Outcome(0, 31, false, knownComplete));
                owner = completion;
                delay = Task.CompletedTask;
                acceptanceFailed = true;
            }
        }
        if (acceptanceFailed) { return await owner.ConfigureAwait(false); }
        var finished = await Task.WhenAny(owner, delay, cancellationSignal.Task).ConfigureAwait(false);
        if (owner.IsCompleted) { return await owner.ConfigureAwait(false); }
        if (finished == delay && delay.IsCanceled) { return await owner.ConfigureAwait(false); }
        lock (sync) { deadlineExpired |= finished == delay; }
        RequestCancellation();
        // 仅结束本次观察；原 completion 工作、内存及文件引用仍由静态所有者持有。
        return Outcome(0, finished == delay ? 1460 : 995, finished != delay, false, finished == delay);
    }

    internal Task<HidIoOutcome> ReapAsync()
    {
        lock (sync)
        {
            RequestCancellation();
            if (completion is null)
            {
                knownComplete = true;
                completion = FinishWithoutSubmissionAsync(Outcome(0, 995, true, true));
                return completion;
            }
            if (completion.IsCompleted && !released)
            { completion = submitted ? CompleteOwnedAsync(CompleteNative)
                : FinishWithoutSubmissionAsync(Outcome(0, 31, false, knownComplete)); }
            return completion;
        }
    }

    private async Task<HidIoOutcome> FinishWithoutSubmissionAsync(HidIoOutcome result)
    {
        // 注册回调后受理失败也须等待该回调结束，再释放其可访问的资源。
        await Task.Yield();
        return await FinishAsync(result).ConfigureAwait(false);
    }

    private async Task<HidIoOutcome> CompleteOwnedAsync(Func<HidIoOutcome> execute)
    {
        HidIoOutcome result;
        try { result = await Task.Run(execute).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reporter.Report(exception, "HidIoExecution");
            lock (sync) { knownComplete |= !submitted; result = Outcome(0, 31, false, knownComplete); }
        }
        return await FinishAsync(result).ConfigureAwait(false);
    }

    private async Task<HidIoOutcome> FinishAsync(HidIoOutcome result)
    {
        try
        {
            await cancellation.DisposeAsync().ConfigureAwait(false);
            delayCancellation.Cancel();
            if (knownComplete) { ReleaseResources(); }
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reporter.Report(exception, "HidIoCompletionCleanup");
            return Outcome(0, 31, false, false);
        }
    }

    private HidIoOutcome Execute()
    {
        lock (sync)
        {
            if (cancelRequested) { knownComplete = true; return Outcome(0, 995, true, true); }
            if (!canSubmit()) { knownComplete = true; return Outcome(0, 1167, false, true); }
            submitted = true;
            bool accepted = direction == HidIoDirection.Input
                ? HidIoNative.ReadFile(file, buffer, (uint)bytes.Length, 0, overlapped)
                : HidIoNative.WriteFile(file, buffer, (uint)bytes.Length, 0, overlapped);
            int error = accepted ? 0 : Marshal.GetLastPInvokeError();
            if (!accepted && error != 997)
            { knownComplete = true; return Outcome(0, error, error == 995, true); }
        }
        return CompleteNative();
    }

    private HidIoOutcome CompleteNative()
    {
        bool succeeded = HidIoNative.GetOverlappedResult(file, overlapped, out uint transferred, true);
        int resultError = succeeded ? 0 : Marshal.GetLastPInvokeError();
        lock (sync)
        {
            knownComplete = succeeded || resultError is not (996 or 997) && completionEvent.WaitOne(0);
            if (transferred > bytes.Length) { return Outcome(0, 13, false, knownComplete); }
            int completed = succeeded ? (int)transferred : 0;
            if (succeeded && direction == HidIoDirection.Input && completed > 0)
            { Marshal.Copy(buffer, bytes, 0, completed); }
            return Outcome(completed, succeeded ? null : resultError, resultError == 995, knownComplete);
        }
    }

    internal void RequestCancellation()
    {
        lock (sync)
        {
            cancelRequested = true;
            cancellationSignal.TrySetResult();
            if (!submitted || knownComplete || released) { return; }
            try
            {
                if (!HidIoNative.CancelIoEx(file, overlapped))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error != 1168) { reporter.Report(new Win32Exception(error), "HidIoCancellation"); }
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { reporter.Report(exception, "HidIoCancellation"); }
        }
    }

    private HidIoOutcome Outcome(int completed, int? error, bool cancelled, bool known, bool timedOut = false) =>
        new(new(bytes.Length, completed, error, cancelled, known, timedOut || deadlineExpired && error is not null),
            direction == HidIoDirection.Input && known && error is null
                ? ImmutableArray.CreateRange(bytes.AsSpan(0, completed).ToArray()) : [], submitted);

    private void ReleaseResources()
    {
        lock (sync)
        {
            if (released) { return; }
            released = true;
            if (overlapped != 0) { Marshal.FreeHGlobal(overlapped); overlapped = 0; }
            if (buffer != 0) { Marshal.FreeHGlobal(buffer); buffer = 0; }
            completionEvent.Dispose();
            delayCancellation.Dispose();
            if (fileReference) { file.DangerousRelease(); fileReference = false; }
            lock (ownersSync) { owners.Remove((file, direction)); }
        }
    }

    private static OperationResult<HidOverlappedRequest> Failed(string code, string reason) =>
        new OperationResult<HidOverlappedRequest>.Failure(AppError.Create(code, reason,
            "核实实际 collection 并等待当前读写收敛，不重复提交请求"));
}
