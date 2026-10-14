using System.Collections.Immutable;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Telemetry;
using UsbDisplayHost.Core.Themes;
using UsbDisplayHost.Core.Transport;

namespace UsbDisplayHost.Core.Sessions;

public sealed record ThemePipelineDependencies(IFrameRenderer Renderer, IProfileStore Profiles, IAssetStore Assets,
    DeviceSettings Settings, FirmwareJpegProfile JpegProfile, IJpegFrameValidator JpegValidator,
    FramePacingPolicy? PacingPolicy, Func<ImmutableArray<MetricSnapshot>> GetMetrics, TimeProvider Clock);

// 连接工作在启动构建前持有此对象，部分创建的后台组件不能从失败结果中丢失。
internal sealed class ThemeSessionPipelineBuild
{
    private readonly object sync = new();
    private readonly IErrorReporter reporter;
    private readonly Task<OperationResult<ThemeSessionPipeline>> building;
    private ActiveFrameProducer? producer;
    private FrameBoundaryScheduler? scheduler;
    private Task<OperationResult<Unit>>? stopping;
    internal ThemeSessionPipeline? Pipeline { get; private set; }

    internal ThemeSessionPipelineBuild(DeviceKey key, long generation, DeviceCoordinator coordinator,
        IDisplayTransport transport, IApplicationSessionState observation, IFrameParserRecovery parser,
        DeviceCapabilities capabilities, TransportLimits limits, ThemeInitializationReceipt initialization,
        FrameParserRecoveryReceipt? clearance, ThemePipelineDependencies dependencies, IErrorReporter reporter)
    {
        this.reporter = reporter;
        building = Task.Run(() =>
        {
            try
            {
                var pacing = new FrameRateController(dependencies.Settings, dependencies.PacingPolicy, dependencies.Clock, reporter);
                var executor = new FrameTransferExecutor(key, generation, capabilities, dependencies.JpegProfile, limits,
                    transport, dependencies.JpegValidator, parser, new CoordinatorFrameGuard(coordinator), reporter, dependencies.Clock);
                if (initialization.PowerIntent == PowerState.On)
                {
                    var admitted = executor.AdmitConnectionInitialization(initialization, clearance);
                    if (admitted is OperationResult<Unit>.Failure failure)
                    { return (OperationResult<ThemeSessionPipeline>)new OperationResult<ThemeSessionPipeline>.Failure(failure.Error); }
                }
                scheduler = new(executor, coordinator.IsCurrent, reporter, pacing);
                var application = new ThemeApplicationService(key, coordinator, dependencies.Renderer, scheduler,
                    dependencies.Profiles, dependencies.Assets, dependencies.Clock, reporter);
                producer = new(coordinator, dependencies.Renderer, scheduler, dependencies.GetMetrics, dependencies.Clock, reporter);
                Pipeline = new(key, generation, coordinator, application, producer, scheduler, executor, transport, observation, reporter);
                return (OperationResult<ThemeSessionPipeline>)new OperationResult<ThemeSessionPipeline>.Success(Pipeline);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = reporter.Report(exception, "ThemePipelineBuild");
                return new OperationResult<ThemeSessionPipeline>.Failure(new(report.ErrorId, "ThemePipelineBuildFailed",
                    "主题管线创建未完成，部分组件仍由连接工作持有", "请等待原连接关闭后重新检测，不替换原所有者"));
            }
        });
    }

    internal Task<OperationResult<ThemeSessionPipeline>> Completion => building;
    internal Task<OperationResult<Unit>> StopAsync()
    {
        lock (sync) { return stopping ??= StopCoreAsync(); }
    }
    private async Task<OperationResult<Unit>> StopCoreAsync()
    {
        try
        {
            await building.ConfigureAwait(false);
            if (producer is not null) { await producer.DisposeAsync().ConfigureAwait(false); }
            if (scheduler is not null) { await scheduler.DisposeAsync().ConfigureAwait(false); }
            return new OperationResult<Unit>.Success(new());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "ThemePipelineBuildStop");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "ThemePipelineBuildStopUnconfirmed",
                "部分主题管线停止未确认，组件与连接资源已保留", "请检查诊断，不能继续释放依赖资源"));
        }
    }
}
