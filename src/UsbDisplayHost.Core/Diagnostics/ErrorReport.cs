namespace UsbDisplayHost.Core.Diagnostics;

public sealed record ErrorReport(string ErrorId, bool IsRecorded, bool IsPending = false);
