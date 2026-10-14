using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Windows.Usb;

public sealed record WinUsbIoPolicy
{
    private WinUsbIoPolicy(int writeTimeoutMilliseconds, int controlTimeoutMilliseconds,
        bool shortPacketTerminate, string evidenceReference)
    {
        WriteTimeoutMilliseconds = writeTimeoutMilliseconds;
        ControlTimeoutMilliseconds = controlTimeoutMilliseconds;
        ShortPacketTerminate = shortPacketTerminate;
        EvidenceReference = evidenceReference;
    }
    public int WriteTimeoutMilliseconds { get; }
    public int ControlTimeoutMilliseconds { get; }
    public bool ShortPacketTerminate { get; }
    public string EvidenceReference { get; }
    public static OperationResult<WinUsbIoPolicy> Create(int writeTimeoutMilliseconds,
        int controlTimeoutMilliseconds, bool shortPacketTerminate, string evidenceReference)
    {
        if (writeTimeoutMilliseconds is < 1 or > 60000 || controlTimeoutMilliseconds is < 1 or > 60000
            || string.IsNullOrWhiteSpace(evidenceReference) || evidenceReference.Length > 256)
        {
            return new OperationResult<WinUsbIoPolicy>.Failure(AppError.Create("InvalidUsbIoPolicy",
                "USB 超时或短包契约不完整", "请提供冻结的数据面策略"));
        }
        return new OperationResult<WinUsbIoPolicy>.Success(new(writeTimeoutMilliseconds,
            controlTimeoutMilliseconds, shortPacketTerminate, evidenceReference));
    }
}
