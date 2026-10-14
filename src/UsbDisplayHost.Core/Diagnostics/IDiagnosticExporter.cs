using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Transport;

namespace UsbDisplayHost.Core.Diagnostics;

public sealed record DiagnosticExportProgress(long ReadBytes, long TotalBytes, int FilesCompleted, int TotalFiles);
public sealed record DiagnosticExportSummary(string OutputPath, long ExportedEvents, long OmittedRecords);
public sealed record PendingDiagnosticExport(Guid VerificationId, string OutputPath, long Length,
    string Sha256, DiagnosticExportSummary Summary);
public sealed record DiagnosticConfigurationSummary(string Workspace, string Template,
    bool HasUnsavedChanges, bool IsInputValid, bool HasImportedImage);
public enum DiagnosticSessionResourceState { Active, Closing, ClosedRetained, Unconfirmed }
// 导出仅保留主机计数与资源状态；不接受身份、接口路径或任意设备文本。
public sealed record DiagnosticTransferSummary(DateTimeOffset CapturedAt, long UsbCompletedFrames,
    long ConfirmedPacketBytes, FrameTransferState ChannelState, DiagnosticSessionResourceState ResourceState);
public sealed record DiagnosticExportRequest(string OutputPath, DiagnosticConfigurationSummary Configuration)
{
    public DiagnosticTransferSummary? Transfer { get; init; }
}

public interface IDiagnosticExporter
{
    PendingDiagnosticExport? Pending { get; }
    Task<OperationResult<DiagnosticExportSummary>> VerifyAsync(Guid verificationId, CancellationToken cancellationToken);
    OperationResult<Unit> DiscardVerification(Guid verificationId);
    Task<OperationResult<DiagnosticExportSummary>> ExportAsync(DiagnosticExportRequest request,
        IProgress<DiagnosticExportProgress>? progress, CancellationToken cancellationToken);
}
