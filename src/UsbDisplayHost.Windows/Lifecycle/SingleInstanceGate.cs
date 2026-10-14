using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using UsbDisplayHost.Core.Diagnostics;

namespace UsbDisplayHost.Windows.Lifecycle;

// 锁的取得和释放必须由同一个启动 STA 线程执行。
public sealed class SingleInstanceGate : IDisposable
{
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly IErrorReporter errorReporter;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object syncRoot = new();
    private Task? listening;
    private Action? activate;
    private bool pendingActivation;
    private bool disposed;

    public SingleInstanceGate(IErrorReporter errorReporter)
    {
        this.errorReporter = errorReporter;
        using var identity = WindowsIdentity.GetCurrent();
        string user = identity.User?.Value ?? throw new InvalidOperationException("无法确定当前用户身份");
        using var process = Process.GetCurrentProcess();
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{user}:{process.SessionId}")));
        pipeName = $"UsbDisplayHost.Activate.{key}";
        mutex = new Mutex(false, $"Local\\UsbDisplayHost.Instance.{key}");
        try { IsOwner = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsOwner = true; }
        catch { mutex.Dispose(); lifetime.Dispose(); throw; }
        if (IsOwner) { listening = Task.Run(ListenAsync); }
    }

    public bool IsOwner { get; }

    public void SetActivationHandler(Action handler)
    {
        bool notify;
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            activate = handler;
            notify = pendingActivation;
            pendingActivation = false;
        }
        if (notify) { handler(); }
    }

    public async Task<bool> ActivateExistingAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await client.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
            byte[] response = new byte[1];
            return await client.ReadAsync(response, timeout.Token).ConfigureAwait(false) == 1 && response[0] == 1;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            errorReporter.Report(exception, "ActivateExistingInstance");
            return false;
        }
    }

    private async Task ListenAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 1, 1);
                await server.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                request.CancelAfter(TimeSpan.FromSeconds(2));
                byte[] signal = new byte[1];
                if (await server.ReadAsync(signal, request.Token).ConfigureAwait(false) == 1 && signal[0] == 1)
                {
                    Action? handler;
                    lock (syncRoot)
                    {
                        handler = activate;
                        if (handler is null) { pendingActivation = true; }
                    }
                    handler?.Invoke();
                    await server.WriteAsync(new byte[] { 1 }, request.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                errorReporter.Report(exception, "SingleInstanceListener");
                try { await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            }
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed) { return; }
            disposed = true;
            activate = null;
            lifetime.Cancel();
        }
        try { listening?.GetAwaiter().GetResult(); }
        finally
        {
            if (IsOwner) { mutex.ReleaseMutex(); }
            mutex.Dispose();
            lifetime.Dispose();
        }
    }
}
