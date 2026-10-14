using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Telemetry;

namespace UsbDisplayHost.Windows.Telemetry;

public sealed class WindowsHostMetricProvider(TimeProvider timeProvider, IErrorReporter errorReporter) : IMetricProvider
{
    private readonly object syncRoot = new();
    private CpuTimes? previous;
    private bool cpuFailed;
    private bool memoryFailed;
    private bool disposed;
    private readonly PdhCpuMetric groupedCpu = new(errorReporter);
    private ushort lastGroupCount;

    public Task<OperationResult<ImmutableArray<MetricSnapshot>>> SampleAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult<OperationResult<ImmutableArray<MetricSnapshot>>>(new OperationResult<ImmutableArray<MetricSnapshot>>.Cancelled());
        }
        lock (syncRoot)
        {
            if (disposed) { return Task.FromResult<OperationResult<ImmutableArray<MetricSnapshot>>>(new OperationResult<ImmutableArray<MetricSnapshot>>.Cancelled()); }
            try
            {
                double? cpu = ReadCpu();
                double? memory = ReadMemory();
                long timestamp = timeProvider.GetTimestamp();
                DateTimeOffset now = timeProvider.GetUtcNow();
                ImmutableArray<MetricSnapshot> metrics =
                [Create(MetricKind.CpuUsage, cpu), Create(MetricKind.MemoryUsage, memory)];
                return Task.FromResult<OperationResult<ImmutableArray<MetricSnapshot>>>(
                    new OperationResult<ImmutableArray<MetricSnapshot>>.Success(metrics));

                MetricSnapshot Create(MetricKind kind, double? value) => new(MetricSource.Host, kind, value, "%",
                    timestamp, now, value.HasValue ? MetricAvailability.Available : MetricAvailability.Unavailable);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                previous = null;
                var report = errorReporter.Report(exception, "HostMetrics");
                return Task.FromResult<OperationResult<ImmutableArray<MetricSnapshot>>>(
                    new OperationResult<ImmutableArray<MetricSnapshot>>.Failure(new AppError(report.ErrorId,
                        "HostMetricsUnavailable", "主机指标暂不可用", "主题仍可使用，请稍后查看指标")));
            }
        }
    }

    private double? ReadCpu()
    {
        ushort groups = NativeMethods.GetActiveProcessorGroupCount();
        if (groups != lastGroupCount)
        {
            previous = null;
            groupedCpu.ResetBaseline();
            lastGroupCount = groups;
        }
        if (groups != 1)
        {
            previous = null;
            return groups > 1 ? groupedCpu.Read() : null;
        }
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            int error = Marshal.GetLastPInvokeError();
            previous = null;
            if (!cpuFailed) { errorReporter.Report(new Win32Exception(error), "HostCpuSample"); }
            cpuFailed = true;
            return null;
        }
        cpuFailed = false;
        var current = new CpuTimes(idle.ToUInt64(), kernel.ToUInt64(), user.ToUInt64());
        CpuTimes? before = previous;
        previous = current;
        if (before is not { } baseline || current.Idle < baseline.Idle
            || current.Kernel < baseline.Kernel || current.User < baseline.User) { return null; }
        // Kernel 包括 Idle，分母同时包含内核与用户时间。
        double total = (double)(current.Kernel - baseline.Kernel) + (current.User - baseline.User);
        double idleDelta = current.Idle - baseline.Idle;
        return total <= 0 || idleDelta > total ? null : 100 * (total - idleDelta) / total;
    }

    private double? ReadMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            int error = Marshal.GetLastPInvokeError();
            if (!memoryFailed) { errorReporter.Report(new Win32Exception(error), "HostMemorySample"); }
            memoryFailed = true;
            return null;
        }
        memoryFailed = false;
        return status.TotalPhysical != 0
            && status.AvailablePhysical <= status.TotalPhysical
            ? 100 * (1 - (double)status.AvailablePhysical / status.TotalPhysical) : null;
    }

    private readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

    public ValueTask DisposeAsync()
    {
        lock (syncRoot) { disposed = true; groupedCpu.Dispose(); }
        return ValueTask.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern ushort GetActiveProcessorGroupCount();

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    }
}
