using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Core.Transport;

public sealed partial class FrameTransferExecutor
{
    private DiagnosticContext FrameAssociation(FrameContext context, long started, long? bytes, string state) =>
        new(DiagnosticContext.Anonymize(deviceKey), sessionGeneration, context.OperationId, state,
            ActualBytes: bytes, ElapsedMilliseconds: timeProvider.GetElapsedTime(started).TotalMilliseconds,
            FirmwareVersion: capabilities.FirmwareVersion);

    private void RecordFrame(FrameContext context, long started, long? bytes, string state, string? errorId = null)
    {
        if (errorReporter is not IDiagnosticService diagnostics) { return; }
        diagnostics.Write(errorId is null ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning,
            "FrameTransfer", state, FrameAssociation(context, started, bytes, state), errorId);
    }
}
