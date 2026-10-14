using System.ComponentModel;
using System.Runtime.InteropServices;
using UsbDisplayHost.Core.Diagnostics;

namespace UsbDisplayHost.Windows.Telemetry;

// 由 WindowsHostMetricProvider 的采样门串行访问，查询只创建一次。
internal sealed class PdhCpuMetric(IErrorReporter reporter) : IDisposable
{
    private static readonly object OwnersLock = new();
    private static readonly HashSet<PdhCpuMetric> Owners = [];
    private nint query, counter;
    private bool initialized, failed, hasBaseline, disposed, closeAttempted;
    private Exception? closeError;
    public void ResetBaseline() => hasBaseline = false;

    public double? Read()
    {
        if (disposed) { return null; }
        try
        {
            if (!initialized)
            {
                initialized = true;
                lock (OwnersLock)
                {
                    if (Owners.Count >= 8) { return null; }
                    Owners.Add(this);
                }
                uint opened = Native.PdhOpenQueryW(null, 0, out query);
                if (opened != 0 || query == 0)
                {
                    ReportStatus(opened);
                    CloseQuery();
                    return null;
                }
                uint added = Native.PdhAddEnglishCounterW(query, @"\Processor Information(_Total)\% Processor Time", 0, out counter);
                if (added != 0 || counter == 0)
                {
                    ReportStatus(added);
                    CloseQuery();
                    return null;
                }
            }
            if (query == 0 || counter == 0 || closeAttempted) { return null; }
            uint collected = Native.PdhCollectQueryData(query);
            if (collected != 0) { hasBaseline = false; ReportStatus(collected); return null; }
            if (!hasBaseline) { hasBaseline = true; return null; }
            uint formatted = Native.PdhGetFormattedCounterValue(counter, 0x200, out _, out var value);
            if (formatted != 0 || value.Status is not (0 or 1)
                || !double.IsFinite(value.Value) || value.Value is < 0 or > 100)
            {
                hasBaseline = false;
                ReportStatus(formatted != 0 ? formatted : value.Status);
                return null;
            }
            failed = false;
            return value.Value;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            hasBaseline = false;
            if (!failed) { reporter.Report(exception, "PdhCpuSample"); }
            failed = true;
            // 初始化异常可能留下查询，禁止再次创建，退出时由同一所有者关闭。
            return null;
        }
    }
    private void ReportStatus(uint status)
    {
        if (!failed)
        {
            Exception error = status == 0 ? new InvalidOperationException("CPU 计数器未提供有效句柄或数值")
                : new Win32Exception(unchecked((int)status));
            reporter.Report(error, "PdhCpuSample");
        }
        failed = true;
    }
    private void CloseQuery()
    {
        if (closeAttempted)
        {
            if (closeError is not null) { throw new InvalidOperationException("CPU 计数器查询关闭未确认", closeError); }
            return;
        }
        closeAttempted = true;
        try
        {
            if (query != 0)
            {
                uint status = Native.PdhCloseQuery(query);
                if (status != 0) { throw new Win32Exception(unchecked((int)status)); }
            }
            query = counter = 0;
            lock (OwnersLock) { Owners.Remove(this); }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            closeError = exception;
            throw;
        }
    }
    public void Dispose()
    {
        disposed = true;
        CloseQuery();
    }
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }
    private static class Native
    {
        [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        internal static extern uint PdhOpenQueryW(string? source, nuint data, out nint query);
        [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        internal static extern uint PdhAddEnglishCounterW(nint query, string path, nuint data, out nint counter);
        [DllImport("pdh.dll", ExactSpelling = true)]
        internal static extern uint PdhCollectQueryData(nint query);
        [DllImport("pdh.dll", ExactSpelling = true)]
        internal static extern uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out CounterValue value);
        [DllImport("pdh.dll", ExactSpelling = true)]
        internal static extern uint PdhCloseQuery(nint query);
    }
}
