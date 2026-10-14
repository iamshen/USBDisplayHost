using System.Collections.Immutable;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Core.Telemetry;
using UsbDisplayHost.Core.Transport;

namespace UsbDisplayHost.Core.Configuration;

// 实际会话适配器提供只读核验；不能把请求值或首帧 USB 收据当作设备状态读回。
public interface IApplicationSessionState
{
    Task<OperationResult<Unit>> PrepareConnectionAsync(OperationContext context, CancellationToken cancellationToken) =>
        Task.FromResult<OperationResult<Unit>>(new OperationResult<Unit>.Success(new()));
    Task<OperationResult<DeviceObservation>> ObserveAsync(OperationContext context, CancellationToken cancellationToken);
    Task<OperationResult<Unit>> DrainAsync();
    // 物理会话终止时确认控制句柄、读循环及回调全部释放；未知关闭保留所有者，不发送电源／模式命令。
    Task<OperationResult<Unit>> CloseAsync();
}

// 每次冻结应用创建一个工作，由 DeviceCoordinator 受理；页面分别读取 Attempt 中的发送／保存结果。
public sealed class ThemeApplicationSessionWork : IDeviceSessionWork
{
    private readonly ThemeApplicationService application;
    private readonly ThemeDraft? theme;
    private readonly DeviceSettingsDraft? settings;
    private readonly ImmutableArray<MetricSnapshot> metrics;
    private readonly IApplicationSessionState session;
    private readonly Func<OperationContext, bool> isCurrent;
    private readonly IErrorReporter reporter;
    private readonly object drainLock = new();
    private readonly TaskCompletionSource<Unit> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<OperationResult<Unit>>? drainTask;
    private ThemeApplicationAttempt? attempt;
    private int started;

    private ThemeApplicationSessionWork(ThemeApplicationService application, ThemeDraft? theme, DeviceSettingsDraft? settings,
        ImmutableArray<MetricSnapshot> metrics, IApplicationSessionState session,
        Func<OperationContext, bool> isCurrent, IErrorReporter reporter)
    {
        this.application = application;
        this.theme = theme;
        this.settings = settings;
        this.metrics = metrics;
        this.session = session;
        this.isCurrent = isCurrent;
        this.reporter = reporter;
    }

    public static ThemeApplicationSessionWork ForTheme(ThemeApplicationService application, ThemeDraft frozenDraft,
        ImmutableArray<MetricSnapshot> metrics, IApplicationSessionState session,
        Func<OperationContext, bool> isCurrent, IErrorReporter reporter) =>
        new(application, frozenDraft, null, metrics, session, isCurrent, reporter);

    public static ThemeApplicationSessionWork ForSettings(ThemeApplicationService application, DeviceSettingsDraft frozenDraft,
        ImmutableArray<MetricSnapshot> metrics, IApplicationSessionState session,
        Func<OperationContext, bool> isCurrent, IErrorReporter reporter) =>
        new(application, null, frozenDraft, metrics, session, isCurrent, reporter);

    public DeviceAction Action => theme is not null ? DeviceAction.ApplyContent : DeviceAction.ApplySettings;
    public ThemeApplicationAttempt? Attempt => Volatile.Read(ref attempt);
    public DeviceRejectionProof? RejectionBeforeIo => Attempt?.RejectionBeforeIo;

    public async Task<OperationResult<DeviceObservation>> ExecuteAsync(OperationContext context, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref started, 1, 0) != 0)
        {
            return Failure("ApplicationWorkReused", "该应用工作已受理，不能再次执行", "请为当前草稿创建新的应用操作");
        }
        try
        {
            var applied = await application.ApplyForSessionAsync(context, theme, settings, metrics, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref attempt, applied);
            if (applied.Result is OperationResult<ThemeApplicationResult>.Failure rejected)
            {
                return new OperationResult<DeviceObservation>.Failure(rejected.Error);
            }
            if (applied.Result is OperationResult<ThemeApplicationResult>.Unknown unknown)
            {
                return new OperationResult<DeviceObservation>.Unknown(unknown.Error);
            }
            if (applied.Result is OperationResult<ThemeApplicationResult>.Cancelled)
            {
                return new OperationResult<DeviceObservation>.Cancelled();
            }
            var outcome = ((OperationResult<ThemeApplicationResult>.Success)applied.Result).Value;
            if (outcome.Transmission is not OperationResult<FrameTransferReceipt>.Success || !outcome.ActiveCommitted)
            {
                var error = outcome.Error ?? AppError.Create("ApplicationNotCommitted", "首帧或活动配置未完整提交", "请核实设备状态后恢复会话");
                return outcome.Transmission is OperationResult<FrameTransferReceipt>.Failure
                    ? new OperationResult<DeviceObservation>.Failure(error)
                    : new OperationResult<DeviceObservation>.Unknown(error);
            }
            if (!isCurrent(context))
            {
                return new OperationResult<DeviceObservation>.Unknown(AppError.Create("ApplicationSessionExpired",
                    "已应用，但设备会话已变化", "请读取当前设备状态；不要直接重发画面"));
            }
            // 设备已经变化；后续用户取消不撤销已提交结果，实际核验与收敛仍须完成。
            var observed = await session.ObserveAsync(context, CancellationToken.None).ConfigureAwait(false);
            return observed switch
            {
                OperationResult<DeviceObservation>.Success or OperationResult<DeviceObservation>.Failure
                    or OperationResult<DeviceObservation>.Unknown => observed,
                _ => new OperationResult<DeviceObservation>.Unknown(AppError.Create("ApplicationObservationUnconfirmed",
                    "已应用，但实际设备状态核验未完成", "请保留应用结果并重新核实设备状态"))
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "ApplicationSessionWork");
            return new OperationResult<DeviceObservation>.Unknown(new(report.ErrorId, "ApplicationSessionUnconfirmed",
                "应用后的设备状态未确认", "请保留应用结果并检查设备诊断；不要直接重发"));
        }
        finally { finished.TrySetResult(new Unit()); }
    }

    public Task<OperationResult<Unit>> DrainAsync()
    {
        lock (drainLock)
        {
            if (Volatile.Read(ref started) == 0)
            {
                return Task.FromResult<OperationResult<Unit>>(new OperationResult<Unit>.Failure(AppError.Create(
                    "ApplicationWorkNotStarted", "该应用工作尚未执行，不能确认会话资源收敛", "请通过设备协调器受理操作")));
            }
            return drainTask ??= Task.Run(DrainCoreAsync);
        }
    }

    private async Task<OperationResult<Unit>> DrainCoreAsync()
    {
        await finished.Task.ConfigureAwait(false);
        try { return await session.DrainAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = reporter.Report(exception, "ApplicationSessionDrain");
            return new OperationResult<Unit>.Unknown(new(report.ErrorId, "ApplicationSessionDrainUnconfirmed",
                "应用会话资源尚未确认收敛", "请停止使用该会话并检查诊断"));
        }
    }

    private static OperationResult<DeviceObservation> Failure(string code, string reason, string action) =>
        new OperationResult<DeviceObservation>.Failure(AppError.Create(code, reason, action));
}
