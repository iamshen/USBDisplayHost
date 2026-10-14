using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Windows.Usb;

public sealed record DeviceInterfacePath(Guid InterfaceClass, string Path);
public sealed record DeviceInterfaceInventory(ImmutableArray<DeviceInterfacePath> Interfaces, long ChangeEpoch, bool ChangedDuringEnumeration);

// 仅发现接口路径，不从路径推断序列号、模式、驱动或可信能力，不打开设备数据面。
public sealed class WindowsDeviceInterfaceMonitor : IAsyncDisposable
{
    private const int MaximumMonitors = 8;
    private static readonly object RegistrationSync = new();
    private static readonly HashSet<Guid> MonitoredClasses = [];
    private static readonly DeviceChange Rescan = new(DeviceChangeKind.RescanRequired, null);
    private static int monitorCount;
    private static readonly DeviceInterfaceNative.NotificationCallback NativeCallback = OnNotification;
    private readonly ImmutableArray<Guid> interfaceClasses;
    private readonly IErrorReporter reporter;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object closeSync = new();
    private readonly List<nint> registrations = new(4);
    private readonly Channel<DeviceChange> changes = Channel.CreateBounded<DeviceChange>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false
    });
    private GCHandle callbackOwner;
    private Task<OperationResult<Unit>>? closing;
    private Exception? callbackError;
    private readonly string?[] removedPaths = new string?[16];
    private int removalUnconfirmed;
    private long changeEpoch;
    private int stopping, readerClaimed;
    private bool countOwned;

    private WindowsDeviceInterfaceMonitor(ImmutableArray<Guid> classes, IErrorReporter reporter)
    {
        interfaceClasses = classes; this.reporter = reporter;
    }
    public long ChangeEpoch => Interlocked.Read(ref changeEpoch);

    public static async Task<OperationResult<WindowsDeviceInterfaceMonitor>> CreateAsync(ImmutableArray<Guid> interfaceClasses,
        IErrorReporter reporter, CancellationToken token)
    {
        if (interfaceClasses.IsDefaultOrEmpty || interfaceClasses.Length > 4 || interfaceClasses.Contains(Guid.Empty)
            || interfaceClasses.Distinct().Count() != interfaceClasses.Length)
        {
            return new OperationResult<WindowsDeviceInterfaceMonitor>.Failure(AppError.Create("InvalidInterfaceClasses",
                "接口类记录为空、重复或超过限额", "请提供冻结兼容记录中的实际接口 GUID"));
        }
        if (token.IsCancellationRequested) { return new OperationResult<WindowsDeviceInterfaceMonitor>.Cancelled(); }
        lock (RegistrationSync)
        {
            if (monitorCount == MaximumMonitors || interfaceClasses.Any(MonitoredClasses.Contains))
            {
                return new OperationResult<WindowsDeviceInterfaceMonitor>.Failure(AppError.Create("InterfaceMonitorOccupied",
                    "接口类已有监视所有者或数量达到上限", "请先确认现有通知注销；未知注销不能用新实例替代"));
            }
            monitorCount++;
            foreach (Guid interfaceClass in interfaceClasses) { MonitoredClasses.Add(interfaceClass); }
        }
        var monitor = new WindowsDeviceInterfaceMonitor(interfaceClasses, reporter) { countOwned = true };
        return await Task.Run(() => monitor.Register(token)).ConfigureAwait(false);
    }

    private OperationResult<WindowsDeviceInterfaceMonitor> Register(CancellationToken token)
    {
        try
        {
            callbackOwner = GCHandle.Alloc(this);
            foreach (Guid interfaceClass in interfaceClasses)
            {
                token.ThrowIfCancellationRequested();
                var filter = new DeviceInterfaceNative.NotificationFilter { Size = 416, InterfaceClass = interfaceClass };
                uint status = DeviceInterfaceNative.CM_Register_Notification(ref filter, GCHandle.ToIntPtr(callbackOwner),
                    NativeCallback, out nint registration);
                if (registration != nint.Zero) { registrations.Add(registration); }
                if (status != 0 || registration == nint.Zero) { throw ConfigurationFailure("Register", status); }
            }
            token.ThrowIfCancellationRequested();
            changes.Writer.TryWrite(Rescan);
            return new OperationResult<WindowsDeviceInterfaceMonitor>.Success(this);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DeviceInterfaceRegistration");
            var closed = CloseCore();
            if (closed is OperationResult<Unit>.Unknown unknown) { return new OperationResult<WindowsDeviceInterfaceMonitor>.Unknown(unknown.Error); }
            return exception is OperationCanceledException && token.IsCancellationRequested
                ? new OperationResult<WindowsDeviceInterfaceMonitor>.Cancelled()
                : new OperationResult<WindowsDeviceInterfaceMonitor>.Failure(new(report.ErrorId, "InterfaceRegistrationFailed",
                    "设备接口通知注册失败，未开始枚举或连接", "请检查接口兼容记录与系统诊断"));
        }
    }

    private static uint OnNotification(nint notification, nint context, uint action, nint eventData, uint eventDataSize)
    {
        WindowsDeviceInterfaceMonitor? owner = null;
        try
        {
            owner = GCHandle.FromIntPtr(context).Target as WindowsDeviceInterfaceMonitor;
            if (owner is null || Volatile.Read(ref owner.stopping) != 0) { return 0; }
            if (action == 1) { owner.RecordRemoval(eventData, eventDataSize); }
            if (Interlocked.Increment(ref owner.changeEpoch) < 0)
            {
                Interlocked.CompareExchange(ref owner.callbackError, new InvalidOperationException("Interface change epoch exhausted."), null);
            }
            owner.changes.Writer.TryWrite(Rescan);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 不在系统回调线程执行日志 I/O 或连接；保留一条故障，消费方接续诊断。
            if (owner is not null) { Interlocked.CompareExchange(ref owner.callbackError, exception, null); }
            if (owner is not null)
            {
                Interlocked.Exchange(ref owner.removalUnconfirmed, 1);
                Interlocked.Increment(ref owner.changeEpoch);
                owner.changes.Writer.TryWrite(Rescan);
            }
        }
        return 0;
    }

    private void RecordRemoval(nint data, uint size)
    {
        // SDK CM_NOTIFY_EVENT_DATA: 类型／保留字段 8 字节，GUID 16 字节，之后为 UTF-16 路径。
        if (data == nint.Zero || size is < 26 or > 65560 || (size - 24) % 2 != 0
            || Marshal.ReadInt32(data) != 0 || Marshal.ReadInt32(data, 4) != 0
            || !interfaceClasses.Contains(Marshal.PtrToStructure<Guid>(data + 8)))
        { Interlocked.Exchange(ref removalUnconfirmed, 1); return; }
        int capacity = (int)(size - 24) / 2;
        int length = 0;
        while (length < capacity && Marshal.ReadInt16(data, 24 + length * 2) != 0) { length++; }
        if (length == 0 || length == capacity || length > 32767)
        { Interlocked.Exchange(ref removalUnconfirmed, 1); return; }
        string? path = Marshal.PtrToStringUni(data + 24, length);
        if (path is null || !path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.Any(char.IsControl))
        { Interlocked.Exchange(ref removalUnconfirmed, 1); return; }
        for (int index = 0; index < removedPaths.Length; index++)
        {
            var existing = Volatile.Read(ref removedPaths[index]);
            if (existing is null && Interlocked.CompareExchange(ref removedPaths[index], path, null) is null) { return; }
        }
        Interlocked.Exchange(ref removalUnconfirmed, 1);
    }

    public async IAsyncEnumerable<DeviceChange> WatchAsync([EnumeratorCancellation] CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref readerClaimed, 1, 0) != 0)
        {
            throw new InvalidOperationException("Interface monitor has one notification consumer.");
        }
        await foreach (var change in changes.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            long drainedEpoch = ChangeEpoch;
            for (int index = 0; index < removedPaths.Length; index++)
            {
                if (Interlocked.Exchange(ref removedPaths[index], null) is { } path)
                { yield return new(DeviceChangeKind.Removed, path); }
            }
            if (Interlocked.Exchange(ref removalUnconfirmed, 0) != 0)
            { yield return new(DeviceChangeKind.Removed, null); }
            yield return change with { NotificationEpoch = drainedEpoch };
        }
    }

    public async Task<OperationResult<DeviceInterfaceInventory>> EnumerateAsync(CancellationToken token)
    {
        bool entered = false;
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false); entered = true;
            if (Volatile.Read(ref stopping) != 0)
            {
                return new OperationResult<DeviceInterfaceInventory>.Failure(AppError.Create("InterfaceMonitorStopped",
                    "接口监视已停止，未开始枚举", "请先核实通知所有者关闭结果"));
            }
            return await Task.Run(() => EnumerateCore(token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new OperationResult<DeviceInterfaceInventory>.Cancelled(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "DeviceInterfaceEnumeration");
            return new OperationResult<DeviceInterfaceInventory>.Failure(new(report.ErrorId, "InterfaceEnumerationFailed",
                "设备接口枚举未完成，未返回部分设备列表", "请检查接口记录、设备变化与系统诊断后重扫"));
        }
        finally { if (entered) { gate.Release(); } }
    }

    private OperationResult<DeviceInterfaceInventory> EnumerateCore(CancellationToken token)
    {
        if (Volatile.Read(ref callbackError) is { } notificationError) { throw new InvalidOperationException("Interface notification callback failed.", notificationError); }
        long startedEpoch = ChangeEpoch;
        var interfaces = ImmutableArray.CreateBuilder<DeviceInterfacePath>();
        var seen = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (Guid value in interfaceClasses)
        {
            Guid interfaceClass = value;
            char[]? buffer = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                uint status = DeviceInterfaceNative.CM_Get_Device_Interface_List_SizeW(out uint length, ref interfaceClass, null, 0);
                if (status != 0) { throw ConfigurationFailure("ListSize", status); }
                if (length is < 1 or > 65536) { throw new InvalidDataException("Interface list size exceeds the bounded inventory."); }
                buffer = new char[length];
                status = DeviceInterfaceNative.CM_Get_Device_Interface_ListW(ref interfaceClass, null, buffer, length, 0);
                if (status == 0x1A) { buffer = null; continue; }
                if (status != 0) { throw ConfigurationFailure("List", status); }
                break;
            }
            if (buffer is null) { throw new IOException("Interface list changed beyond the bounded retries."); }
            int cursor = 0;
            while (cursor < buffer.Length && buffer[cursor] != '\0')
            {
                token.ThrowIfCancellationRequested();
                int end = Array.IndexOf(buffer, '\0', cursor);
                if (end < 0 || end - cursor > 32767) { throw new InvalidDataException("Interface path is not a bounded terminated string."); }
                string path = new(buffer, cursor, end - cursor);
                if (!path.StartsWith(@"\\?\", StringComparison.Ordinal)) { throw new InvalidDataException("Interface path is not a symbolic device interface."); }
                if (seen.TryGetValue(path, out Guid previous))
                {
                    if (previous != interfaceClass) { throw new InvalidDataException("Interface path belongs to conflicting class records."); }
                }
                else
                {
                    if (interfaces.Count == 256) { throw new InvalidDataException("Interface count exceeds the bounded inventory."); }
                    seen.Add(path, interfaceClass); interfaces.Add(new(interfaceClass, path));
                }
                cursor = end + 1;
            }
            if (cursor == buffer.Length) { throw new InvalidDataException("Interface multi-string has no final terminator."); }
            if (buffer.AsSpan(cursor).ContainsAnyExcept('\0')) { throw new InvalidDataException("Interface list contains data after the final terminator."); }
        }
        long completedEpoch = ChangeEpoch;
        if (completedEpoch != startedEpoch) { changes.Writer.TryWrite(Rescan); }
        return new OperationResult<DeviceInterfaceInventory>.Success(new(interfaces.ToImmutable(), completedEpoch, completedEpoch != startedEpoch));
    }

    public Task<OperationResult<Unit>> CloseAsync()
    {
        lock (closeSync)
        {
            Volatile.Write(ref stopping, 1);
            return closing ??= Task.Run(async () =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try { return CloseCore(); }
                finally { gate.Release(); }
            });
        }
    }
    private OperationResult<Unit> CloseCore()
    {
        Volatile.Write(ref stopping, 1);
        AppError? error = null;
        for (int index = registrations.Count - 1; index >= 0; index--)
        {
            try
            {
                uint status = DeviceInterfaceNative.CM_Unregister_Notification(registrations[index]);
                if (status != 0) { throw ConfigurationFailure("Unregister", status); }
                registrations.RemoveAt(index);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = reporter.Report(exception, "DeviceInterfaceUnregister");
                error ??= new(report.ErrorId, "InterfaceUnregisterUnconfirmed", "设备通知注销尚未确认，回调所有者已保留",
                    "请保留诊断并重新启动应用；不能继续释放上下文或重复建立监视");
            }
        }
        changes.Writer.TryComplete();
        if (error is not null) { return new OperationResult<Unit>.Unknown(error); }
        if (callbackOwner.IsAllocated) { callbackOwner.Free(); }
        if (countOwned)
        {
            lock (RegistrationSync)
            {
                countOwned = false; monitorCount--;
                foreach (Guid interfaceClass in interfaceClasses) { MonitoredClasses.Remove(interfaceClass); }
            }
        }
        return new OperationResult<Unit>.Success(new());
    }
    public async ValueTask DisposeAsync()
    {
        if (await CloseAsync().ConfigureAwait(false) is not OperationResult<Unit>.Success)
        {
            throw new InvalidOperationException("Device interface notification owner cannot be released before confirmed unregister.");
        }
    }
    private static Exception ConfigurationFailure(string stage, uint code) =>
        new InvalidOperationException($"Configuration manager {stage} failed with CONFIGRET 0x{code:X8}.");
}
