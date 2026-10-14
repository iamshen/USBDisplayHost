using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;

namespace UsbDisplayHost.Windows.Usb;

internal sealed record NativeIoOutcome(UsbWriteResult Status, byte[] Data, bool WasSubmitted = false);

// 一个请求持有原生内存、事件及接口引用；只有内核完成与取消回调均结束后才释放。
internal sealed class WinUsbOverlappedRequest
{
    private readonly object syncRoot = new();
    private readonly SafeUsbFileHandle file;
    private readonly SafeWinUsbHandle primary, target;
    private readonly IErrorReporter reporter;
    private readonly EventWaitHandle completionEvent = new(false, EventResetMode.ManualReset);
    private readonly byte[] bytes;
    private readonly byte pipe;
    private readonly WinUsbNative.SetupPacket? setup;
    private nint buffer, overlapped;
    private bool primaryReference, targetReference;
    private bool submitted, cancelRequested, knownComplete, released;
    private bool timeoutRequested;
    private NativeIoOutcome? outcome;
    private Timer? deadline;
    private CancellationTokenRegistration cancellation;

    internal WinUsbOverlappedRequest(SafeUsbFileHandle file, SafeWinUsbHandle primary, SafeWinUsbHandle target,
        byte pipe, byte[] bytes, WinUsbNative.SetupPacket? setup, IErrorReporter reporter)
    {
        this.file = file; this.primary = primary; this.target = target;
        this.pipe = pipe; this.bytes = bytes; this.setup = setup; this.reporter = reporter;
        try
        {
            primary.DangerousAddRef(ref primaryReference);
            target.DangerousAddRef(ref targetReference);
            if (bytes.Length > 0)
            {
                buffer = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
            }
            overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<WinUsbNative.OverlappedData>());
            Marshal.StructureToPtr(new WinUsbNative.OverlappedData
                { Event = completionEvent.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
        }
        catch { ReleaseResources(); throw; }
    }

    internal async Task<NativeIoOutcome> RunAsync(int timeoutMilliseconds, CancellationToken token)
    {
        try
        {
            cancellation = token.Register(static state => ((WinUsbOverlappedRequest)state!).RequestCancellation(), this);
            deadline = new Timer(static state => ((WinUsbOverlappedRequest)state!).RequestCancellation(true),
                this, timeoutMilliseconds, Timeout.Infinite);
            outcome = await Task.Run(Execute).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reporter.Report(exception, "WinUsbOverlappedRun");
            lock (syncRoot)
            {
                knownComplete = !submitted;
                outcome = Result(0, 31, false, knownComplete);
            }
        }
        finally
        {
            if (deadline is not null) { await deadline.DisposeAsync().ConfigureAwait(false); }
            // 不持有请求锁等待取消回调，否则可能与回调锁竞争形成死锁。
            await cancellation.DisposeAsync().ConfigureAwait(false);
            if (knownComplete) { ReleaseResources(); }
        }
        return outcome!;
    }

    private NativeIoOutcome Execute()
    {
        try
        {
            bool pending;
            lock (syncRoot)
            {
                if (cancelRequested)
                {
                    knownComplete = true;
                    return Result(0, 995, true, true);
                }
                // 与取消串行发布请求，避免 CancelIoEx 在请求尚未提交时漏掉它。
                submitted = true;
                bool accepted = setup is { } packet
                    ? WinUsbNative.WinUsb_ControlTransfer(target, packet, buffer, (uint)bytes.Length, 0, overlapped)
                    : WinUsbNative.WinUsb_WritePipe(target, pipe, buffer, (uint)bytes.Length, 0, overlapped);
                int error = accepted ? 0 : Marshal.GetLastPInvokeError();
                pending = !accepted && error == 997;
                if (!accepted && !pending)
                {
                    knownComplete = true;
                    return Result(0, error, error == 995, true);
                }
                if (accepted) { knownComplete = true; }
            }
            return Complete(pending);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reporter.Report(exception, "WinUsbOverlappedExecute");
            lock (syncRoot)
            {
                knownComplete = knownComplete || !submitted;
                return Result(0, 31, false, knownComplete);
            }
        }
    }

    private NativeIoOutcome Complete(bool pending)
    {
        bool succeeded = WinUsbNative.WinUsb_GetOverlappedResult(primary, overlapped, out uint transferred, true);
        int error = succeeded ? 0 : Marshal.GetLastPInvokeError();
        bool ended = succeeded || !pending || (error is not (996 or 997) && completionEvent.WaitOne(0));
        lock (syncRoot)
        {
            knownComplete = ended;
            if (transferred > bytes.Length)
            {
                return Result(0, 13, false, ended);
            }
            int completed = succeeded ? (int)transferred : 0;
            if (succeeded && setup is { RequestType: >= 0x80 } && completed > 0)
            {
                Marshal.Copy(buffer, bytes, 0, completed);
            }
            return Result(completed, succeeded ? null : error, error == 995, ended);
        }
    }

    internal void RequestCancellation(bool timedOut = false)
    {
        lock (syncRoot)
        {
            cancelRequested = true;
            timeoutRequested |= timedOut;
            if (!submitted || knownComplete || released) { return; }
            try
            {
                if (!WinUsbNative.CancelIoEx(file, overlapped))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error != 1168) { reporter.Report(new Win32Exception(error), "WinUsbCancelIo"); }
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                reporter.Report(exception, "WinUsbCancelIo");
            }
        }
    }

    internal async Task<NativeIoOutcome> ReapAsync()
    {
        if (knownComplete) { return outcome!; }
        RequestCancellation();
        try { outcome = await Task.Run(() => Complete(true)).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reporter.Report(exception, "WinUsbOverlappedReap");
            outcome = Result(0, 31, false, false);
        }
        if (knownComplete) { ReleaseResources(); }
        return outcome;
    }

    private NativeIoOutcome Result(int completed, int? error, bool cancelled, bool known) =>
        new(new(bytes.Length, completed, error, cancelled, known, error is not null && timeoutRequested),
            setup is { RequestType: >= 0x80 } && known && error is null ? bytes.AsSpan(0, completed).ToArray() : [], submitted);

    private void ReleaseResources()
    {
        lock (syncRoot)
        {
            if (released) { return; }
            released = true;
            if (overlapped != 0) { Marshal.FreeHGlobal(overlapped); overlapped = 0; }
            if (buffer != 0) { Marshal.FreeHGlobal(buffer); buffer = 0; }
            completionEvent.Dispose();
            if (targetReference) { target.DangerousRelease(); targetReference = false; }
            if (primaryReference) { primary.DangerousRelease(); primaryReference = false; }
        }
    }
}
