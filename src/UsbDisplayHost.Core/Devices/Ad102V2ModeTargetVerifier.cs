using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Core.Themes;
using UsbDisplayHost.Core.Transport;

namespace UsbDisplayHost.Core.Devices;

public sealed record Ad102V2ModeTargetEvidence(OperationContext Context, DeviceSelectionAdmission Admission,
    Ad102V2Feature Feature, DeviceObservation Observation, ThemeInitializationReceipt? Initialization,
    CompletedHostFrame? FirstFrame);

// 只核验已收集的目标证据；不枚举、发命令、接管资源或修改协调器状态。
internal static class Ad102V2ModeTargetVerifier
{
    public static OperationResult<DeviceObservation> VerifyOriginal(Ad102V2ModeRequest request,
        Ad102V2ModeTargetEvidence evidence, Func<OperationContext, bool> isCurrent, IErrorReporter reporter)
    {
        try
        {
            var context = evidence.Context;
            var actual = evidence.Observation;
            var feature = evidence.Feature;
            var admission = evidence.Admission;
            if (isCurrent is null || admission is not { Selection: not null, Validity: not null }
                || context.DeviceKey != request.Context.DeviceKey
                || context.SessionGeneration < request.Context.SessionGeneration
                || context.OperationId != request.Context.OperationId
                || context.ContentRevision != request.Context.ContentRevision
                || admission.Selection.Key != request.Context.DeviceKey
                || !admission.Validity.IsCurrent(admission.Selection) || !isCurrent(context)
                || actual is null || actual.Identity is null || feature is null
                || actual.DeviceKey != request.Context.DeviceKey
                || !admission.Selection.Interfaces.Contains(actual.Identity)
                || DeviceKey.FromSerialNumber(actual.Identity.VendorId, actual.Identity.SerialNumber)
                    is not OperationResult<DeviceKey>.Success key || key.Value != request.Context.DeviceKey
                || actual.Mode != request.OriginalMode || actual.Power != request.PowerIntent
                || actual.Streaming != StreamingState.Stopped || actual.ObservedBrightness is < 0 or > 100
                || actual.Capabilities is not { IsProtocolMatched: true } || !actual.DriverVerified
                || feature.MaintenanceBusy || feature.CurrentMode != feature.TargetMode
                || feature.CurrentMode != (request.OriginalMode == DeviceMode.Theme ? Ad102V2Mode.Theme : Ad102V2Mode.Screen)
                || evidence.FirstFrame is not null)
            { return Unknown("V2OriginalNotReady", "同一设备的原模式尚未核实可用，不能报告未完成或回滚"); }
            if (request.OriginalMode == DeviceMode.SecondaryDisplay)
            {
                if (!actual.SecondaryDisplayPathVerified || evidence.Initialization is not null)
                { return Unknown("V2OriginalDisplayPathUnverified", "原副屏显示路径尚未核实可用"); }
            }
            else
            {
                if (request.OriginalMode != DeviceMode.Theme || actual.Capabilities.Width != 1920
                    || actual.Capabilities.Height != 1080)
                { return Unknown("V2OriginalThemeUnverified", "原主题模式的实际尺寸和能力尚未核实"); }
                var probe = evidence.Initialization;
                if (probe is not null && (probe.Context != context || probe.PowerIntent != request.PowerIntent
                    || probe.OnSequenceCompleted || probe.CompletedCommands != 0)
                    || request.PowerIntent == PowerState.On && probe is not { ProbeVerified: true })
                { return Unknown("V2OriginalProbeUnverified", "原主题缺少只读就绪核验或重查期间执行了亮屏控制"); }
            }
            if (!admission.Validity.IsCurrent(admission.Selection) || !isCurrent(context))
            { return Unknown("V2OriginalDeliveryExpired", "原模式观察在交付前已失效"); }
            return new OperationResult<DeviceObservation>.Success(actual);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (reporter is null) { return Unknown("V2OriginalReporterUnavailable", "原模式核验缺少诊断服务"); }
            var report = reporter.Report(exception, "V2ModeOriginalVerification");
            return new OperationResult<DeviceObservation>.Unknown(new(report.ErrorId, "V2OriginalVerificationFailed",
                "原模式核验未完成，设备状态待确认", "保留原请求，不自动重发或报告已回滚"));
        }
    }

    public static OperationResult<DeviceObservation> Verify(Ad102V2ModeRequest request,
        Ad102V2ModeTargetEvidence evidence, Func<OperationContext, bool> isCurrent, IErrorReporter reporter)
    {
        try
        {
            if (request is null || evidence is null || isCurrent is null || reporter is null
                || request.Context.DeviceKey is null || request.Context.SessionGeneration <= 0
                || request.Context.OperationId == Guid.Empty || request.Context.ContentRevision < 0
                || request.Target is not (DeviceMode.Theme or DeviceMode.SecondaryDisplay)
                || request.PowerIntent is not (PowerState.On or PowerState.Off)
                || evidence.Context.DeviceKey != request.Context.DeviceKey
                || evidence.Context.SessionGeneration <= request.Context.SessionGeneration
                || evidence.Context.OperationId != request.Context.OperationId
                || evidence.Context.ContentRevision != request.Context.ContentRevision
                || evidence.Admission is not { Selection: not null, Validity: not null }
                || evidence.Admission.Selection.Key != request.Context.DeviceKey
                || !evidence.Admission.Validity.IsCurrent(evidence.Admission.Selection)
                || !isCurrent(evidence.Context))
            { return Unknown("V2TargetEvidenceExpired", "目标核验不属于当前设备、操作或新的会话代次"); }

            var actual = evidence.Observation;
            var feature = evidence.Feature;
            var target = request.Target == DeviceMode.Theme ? Ad102V2Mode.Theme : Ad102V2Mode.Screen;
            if (actual is null || feature is null || actual.DeviceKey != request.Context.DeviceKey
                || actual.Identity is null || !evidence.Admission.Selection.Interfaces.Contains(actual.Identity)
                || DeviceKey.FromSerialNumber(actual.Identity.VendorId, actual.Identity.SerialNumber)
                    is not OperationResult<DeviceKey>.Success key || key.Value != request.Context.DeviceKey
                || actual.Mode != request.Target || actual.Power != request.PowerIntent
                || actual.Capabilities is not { IsProtocolMatched: true } || !actual.DriverVerified
                || !Enum.IsDefined(actual.Streaming) || actual.ObservedBrightness is < 0 or > 100
                || feature.CurrentMode != target || feature.TargetMode != target || feature.MaintenanceBusy)
            { return Unknown("V2TargetNotReady", "目标身份、真实模式、电源或驱动状态尚未通过核验"); }

            if (request.Target == DeviceMode.SecondaryDisplay)
            {
                if (!actual.SecondaryDisplayPathVerified || actual.Streaming != StreamingState.Stopped
                    || evidence.Initialization is not null || evidence.FirstFrame is not null)
                { return Unknown("V2SecondaryPathUnverified", "副屏目标显示路径未核实或仍有主题数据面证据"); }
            }
            else
            {
                var initialized = evidence.Initialization;
                if (actual.Capabilities.Width != 1920 || actual.Capabilities.Height != 1080)
                { return Unknown("V2ThemeInitializationUnverified", "主题目标尚未完成同一会话的尺寸和初始化核验"); }
                if (request.PowerIntent == PowerState.Off)
                {
                    if (initialized is not null && (initialized.Context != evidence.Context
                            || initialized.PowerIntent != PowerState.Off
                            || initialized.OnSequenceCompleted || initialized.CompletedCommands != 0)
                        || evidence.FirstFrame is not null || actual.Streaming != StreamingState.Stopped)
                    { return Unknown("V2OffIntentNotPreserved", "熄屏切换出现亮屏初始化、首帧或推流，不能确认保持电源意图"); }
                }
                else if (initialized is null || initialized.Context != evidence.Context
                    || initialized.PowerIntent != PowerState.On || !initialized.ProbeVerified
                    || !initialized.OnSequenceCompleted || initialized.CompletedCommands <= 0
                    || actual.Streaming is not (StreamingState.Stopped or StreamingState.Running)
                    || !MatchesFirstFrame(evidence, actual.Capabilities))
                { return Unknown("V2FirstFrameUnverified", "亮屏主题目标缺少同一操作的完整合法首帧收据"); }
            }
            // 核验过程中发现通知／代次可能变化；交付前再读取当前凭据。
            if (!evidence.Admission.Validity.IsCurrent(evidence.Admission.Selection) || !isCurrent(evidence.Context))
            { return Unknown("V2TargetDeliveryExpired", "目标核验交付前会话或发现凭据已变化"); }
            return new OperationResult<DeviceObservation>.Success(actual);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (reporter is null) { return Unknown("V2TargetReporterUnavailable", "目标核验缺少诊断服务"); }
            var report = reporter.Report(exception, "V2ModeTargetVerification");
            return new OperationResult<DeviceObservation>.Unknown(new(report.ErrorId, "V2TargetVerificationFailed",
                "目标核验未能完成，模式状态仍待确认", "保留原请求及诊断，不自动重发或切回"));
        }
    }

    private static bool MatchesFirstFrame(Ad102V2ModeTargetEvidence evidence, DeviceCapabilities capabilities)
    {
        if (evidence.FirstFrame is not { Frame: not null, Receipt: not null } completed) { return false; }
        var frame = completed.Frame;
        var receipt = completed.Receipt;
        var context = evidence.Context;
        return frame.Context == new FrameContext(context.DeviceKey, context.SessionGeneration, context.OperationId, context.ContentRevision)
            && frame.Context == receipt.Context && frame.Purpose == FramePurpose.Active
            && frame.Width == 1920 && frame.Height == 1080 && !frame.Jpeg.IsDefaultOrEmpty
            && frame.Jpeg.Length <= capabilities.MaximumFrameBytes && frame.FrameNumber > 0
            && receipt.FrameNumber == frame.FrameNumber && receipt.JpegBytes == frame.Jpeg.Length
            && receipt.PacketCount > 0 && receipt.PacketCount <= frame.Jpeg.Length
            && receipt.PacketBytes == receipt.JpegBytes + 8L * (receipt.PacketCount - 1) + 11
            && receipt.CompletedTimestamp >= frame.GeneratedTimestamp;
    }

    private static OperationResult<DeviceObservation> Unknown(string code, string reason) =>
        new OperationResult<DeviceObservation>.Unknown(AppError.Create(code, reason,
            "核实同一设备的实际目标就绪，不重发切换或宣称已回滚"));
}
