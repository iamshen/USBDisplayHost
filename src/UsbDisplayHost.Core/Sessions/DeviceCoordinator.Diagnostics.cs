using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Sessions;

public sealed partial class DeviceCoordinator
{
    private void RecordOperationStart(Request request, OperationContext context)
    {
        if (request.Action == DeviceAction.QueryState || errorReporter is not IDiagnosticService diagnostics) { return; }
        diagnostics.Write(DiagnosticSeverity.Information, "DeviceOperation", $"{request.Action}Started",
            Association(context, request.Started) with { State = StateText(request.Before ?? Current) });
    }

    private void RecordOperationCompletion(Completion completion)
    {
        // 只读取已完成的业务回执，区分底层执行成功与最终会话核验成功。
        if (errorReporter is not IDiagnosticService diagnostics || !completion.Request.Reply.Task.IsCompletedSuccessfully) { return; }
        var result = completion.Request.Reply.Task.Result;
        if (completion.Request.Action == DeviceAction.QueryState && result is OperationResult<DeviceSnapshot>.Success) { return; }
        AppError? error = result switch
        {
            OperationResult<DeviceSnapshot>.Failure failure => failure.Error,
            OperationResult<DeviceSnapshot>.Unknown unknown => unknown.Error,
            _ => null
        };
        string outcome = result switch
        {
            OperationResult<DeviceSnapshot>.Success => "Completed",
            OperationResult<DeviceSnapshot>.Failure => "Failed",
            OperationResult<DeviceSnapshot>.Unknown => "Unknown",
            _ => "Cancelled"
        };
        diagnostics.Write(error is null ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning,
            "DeviceOperation", $"{completion.Request.Action}{outcome}",
            Association(completion.Context, completion.Request.Started) with { State = StateText(Current) }, error?.ErrorId);
    }

    private void RecordStateChange(DeviceSnapshot previous, DeviceSnapshot value)
    {
        if (errorReporter is not IDiagnosticService diagnostics || value.DeviceKey is null) { return; }
        if (previous.DeviceKey == value.DeviceKey && previous.SessionGeneration == value.SessionGeneration
            && previous.Connection == value.Connection && previous.Mode == value.Mode && previous.Power == value.Power
            && previous.Streaming == value.Streaming && previous.Error?.ErrorId == value.Error?.ErrorId) { return; }
        diagnostics.Write(value.Error is null ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning,
            "DeviceState", value.Error?.Code ?? "StateChanged",
            new DiagnosticContext(DiagnosticContext.Anonymize(value.DeviceKey), value.SessionGeneration,
                value.PendingOperationId ?? previous.PendingOperationId, StateText(value),
                FirmwareVersion: value.Capabilities?.FirmwareVersion), value.Error?.ErrorId);
    }

    private static string StateText(DeviceSnapshot value) => $"{value.Connection}.{value.Mode}.{value.Power}.{value.Streaming}";
}
