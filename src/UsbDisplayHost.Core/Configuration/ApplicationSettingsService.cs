using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Sessions;

namespace UsbDisplayHost.Core.Configuration;

public sealed record ApplicationSettingsEdit(bool StartWithWindows, bool SwitchToThemeOnStartup,
    bool SwitchToSecondaryOnExit, bool AutomaticallyCheckUpdates, bool CloseToTray,
    NavigationLayout NavigationLayout = NavigationLayout.Top);

public sealed record PendingApplicationSettingsSave(Guid ConfirmationId, ApplicationSettings Candidate, AppError Error);

public sealed class ApplicationSettingsService(IApplicationSettingsStore store, IStartupRegistration startup,
    IErrorReporter errorReporter, Func<DeviceSnapshot>? getDevice = null)
{
    public bool CanEnableAutomaticMode => getDevice?.Invoke() is
        { Connection: ConnectionState.Ready, PendingOperationId: null, Power: PowerState.On,
            Capabilities: { IsProtocolMatched: true } capabilities }
        && capabilities.Commands.Contains(DeviceCommand.SwitchMode);
    private readonly SemaphoreSlim gate = new(1, 1);
    private ApplicationSettings current = ApplicationSettings.Default;
    private PendingApplicationSettingsSave? pendingSave;
    public ApplicationSettings Current => Volatile.Read(ref current);
    public PendingApplicationSettingsSave? PendingSave => Volatile.Read(ref pendingSave);

    public Task<OperationResult<StartupRegistrationState>> QueryStartupAsync(CancellationToken cancellationToken) =>
        startup.QueryAsync(cancellationToken);

    public Task<OperationResult<ApplicationSettingsReadResult>> LoadAsync(CancellationToken cancellationToken) =>
        RunAsync(async () => await LoadCoreAsync(cancellationToken).ConfigureAwait(false), cancellationToken);

    private async Task<OperationResult<ApplicationSettingsReadResult>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var result = await store.LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (PendingSave is null && result is OperationResult<ApplicationSettingsReadResult>.Success loaded)
        {
            Volatile.Write(ref current, loaded.Value.Settings);
        }
        return result;
    }

    public Task<OperationResult<ApplicationSettings>> SaveAsync(ApplicationSettingsEdit edit, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            if (PendingSave is { } pending) { return new OperationResult<ApplicationSettings>.Unknown(pending.Error); }
            var load = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (load is not OperationResult<ApplicationSettingsReadResult>.Success loaded) { return ConvertLoadFailure<ApplicationSettings>(load); }
            if (loaded.Value.Recovery == ProfileRecovery.UnsupportedVersion) { return Unsupported<ApplicationSettings>(); }
            var before = loaded.Value.Settings;
            if (((edit.SwitchToThemeOnStartup && !before.SwitchToThemeOnStartup)
                || (edit.SwitchToSecondaryOnExit && !before.SwitchToSecondaryOnExit)) && !CanEnableAutomaticMode)
            {
                return new OperationResult<ApplicationSettings>.Failure(AppError.Create("ModeCapabilityUnavailable",
                    "当前设备不允许启用自动模式切换", "请连接具有匹配切换能力且已亮屏的设备后设置"));
            }
            var candidate = new ApplicationSettings(edit.StartWithWindows, edit.SwitchToThemeOnStartup,
                edit.SwitchToSecondaryOnExit, edit.AutomaticallyCheckUpdates, before.HasSeenTrayNotice, edit.CloseToTray,
                before.SuppressModeSwitchNotice, Enum.IsDefined(edit.NavigationLayout) ? edit.NavigationLayout : NavigationLayout.Top);
            OperationResult<IStartupRegistrationCheckpoint> change;
            try { change = await startup.ChangeAsync(candidate.StartWithWindows, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = errorReporter.Report(exception, "StartupChangeUnconfirmed");
                return RetainUnknown(candidate, new(report.ErrorId, "StartupChangeUnconfirmed",
                    "自启变更结果待确认", "请核实设置主文件与实际自启状态"));
            }
            if (change is not OperationResult<IStartupRegistrationCheckpoint>.Success checkpoint)
            {
                return change switch
                {
                    OperationResult<IStartupRegistrationCheckpoint>.Failure failure => new OperationResult<ApplicationSettings>.Failure(failure.Error),
                    OperationResult<IStartupRegistrationCheckpoint>.Unknown unknown => RetainUnknown(candidate, unknown.Error),
                    _ => new OperationResult<ApplicationSettings>.Cancelled()
                };
            }
            OperationResult<Unit> saved;
            try { saved = await store.SaveSettingsAsync(candidate, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = errorReporter.Report(exception, "ApplicationSettingsCommit");
                saved = new OperationResult<Unit>.Unknown(new AppError(report.ErrorId, "SettingsCommitUnconfirmed",
                    "设置保存结果待确认", "请重新读取设置和自启状态"));
            }
            if (saved is OperationResult<Unit>.Success)
            {
                Volatile.Write(ref current, candidate);
                return new OperationResult<ApplicationSettings>.Success(candidate);
            }
            if (saved is OperationResult<Unit>.Unknown)
            {
                OperationResult<ApplicationSettingsReadResult> confirmed;
                try { confirmed = await store.LoadSettingsAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    errorReporter.Report(exception, "ApplicationSettingsConfirm");
                    return RetainUnknown(candidate, ((OperationResult<Unit>.Unknown)saved).Error);
                }
                if (confirmed is OperationResult<ApplicationSettingsReadResult>.Success { Value.Recovery: ProfileRecovery.Primary } read)
                {
                    if (read.Value.Settings == candidate)
                    {
                        OperationResult<StartupRegistrationState> registration;
                        try { registration = await startup.QueryAsync(CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception exception) when (exception is not OutOfMemoryException)
                        {
                            errorReporter.Report(exception, "ApplicationSettingsStartupConfirmation");
                            return RetainUnknown(candidate, ((OperationResult<Unit>.Unknown)saved).Error);
                        }
                        if (registration is OperationResult<StartupRegistrationState>.Success { Value.IsManaged: true } actual
                            && actual.Value.IsEnabled == candidate.StartWithWindows)
                        {
                            Volatile.Write(ref current, candidate);
                            return new OperationResult<ApplicationSettings>.Success(candidate);
                        }
                    }
                }
                return RetainUnknown(candidate, ((OperationResult<Unit>.Unknown)saved).Error);
            }
            // 已知没有提交文件时恢复原注册状态；恢复失败不能宣称整体回滚。
            OperationResult<Unit> restored;
            try { restored = await startup.RestoreAsync(checkpoint.Value, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var report = errorReporter.Report(exception, "ApplicationSettingsRestore");
                return RetainUnknown(candidate, new AppError(report.ErrorId, "StartupRestoreUnconfirmed",
                    "自启恢复结果待确认", "请重新读取设置和自启状态"));
            }
            if (restored is not OperationResult<Unit>.Success)
            {
                AppError error = restored switch
                {
                    OperationResult<Unit>.Failure failure => failure.Error,
                    OperationResult<Unit>.Unknown unknown => unknown.Error,
                    _ => AppError.Create("StartupRestoreUnconfirmed", "自启恢复结果待确认", "请重新读取设置和自启状态")
                };
                return RetainUnknown(candidate, error);
            }
            return saved switch
            {
                OperationResult<Unit>.Failure failure => new OperationResult<ApplicationSettings>.Failure(failure.Error),
                OperationResult<Unit>.Unknown unknown => new OperationResult<ApplicationSettings>.Failure(unknown.Error),
                _ => new OperationResult<ApplicationSettings>.Cancelled()
            };
        }, cancellationToken);

    public Task<OperationResult<Unit>> MarkTrayNoticeAsync(CancellationToken cancellationToken) => MarkNoticeAsync(false, cancellationToken);

    public Task<OperationResult<Unit>> SuppressModeSwitchNoticeAsync(CancellationToken cancellationToken) => MarkNoticeAsync(true, cancellationToken);

    private Task<OperationResult<Unit>> MarkNoticeAsync(bool modeSwitch, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        if (PendingSave is { } pending) { return new OperationResult<Unit>.Unknown(pending.Error); }
        var load = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        if (load is not OperationResult<ApplicationSettingsReadResult>.Success loaded) { return ConvertLoadFailure<Unit>(load); }
        if (loaded.Value.Recovery == ProfileRecovery.UnsupportedVersion) { return Unsupported<Unit>(); }
        if (modeSwitch ? loaded.Value.Settings.SuppressModeSwitchNotice : loaded.Value.Settings.HasSeenTrayNotice)
        { return new OperationResult<Unit>.Success(new Unit()); }
        if (loaded.Value.Error is not null) { return new OperationResult<Unit>.Failure(loaded.Value.Error); }
        var candidate = modeSwitch ? loaded.Value.Settings with { SuppressModeSwitchNotice = true }
            : loaded.Value.Settings with { HasSeenTrayNotice = true };
        OperationResult<Unit> result;
        try { result = await store.SaveSettingsAsync(candidate, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "NoticePreferenceCommitUnconfirmed");
            result = new OperationResult<Unit>.Unknown(new(report.ErrorId, "NoticePreferenceCommitUnconfirmed",
                "提示偏好保存结果待确认", "请核实应用设置主文件与自启状态"));
        }
        if (result is OperationResult<Unit>.Success) { Volatile.Write(ref current, candidate); }
        if (result is OperationResult<Unit>.Unknown unknown) { RetainUnknown(candidate, unknown.Error); }
        return result;
    }, cancellationToken);

    private OperationResult<ApplicationSettings> RetainUnknown(ApplicationSettings candidate, AppError error)
    {
        Volatile.Write(ref pendingSave, new(Guid.NewGuid(), candidate, error));
        return new OperationResult<ApplicationSettings>.Unknown(error);
    }

    public Task<OperationResult<ApplicationSettings>> VerifyPendingAsync(Guid confirmationId, CancellationToken token) =>
        RunAsync<ApplicationSettings>(async () =>
        {
            var pending = PendingSave;
            if (pending is null || pending.ConfirmationId != confirmationId)
            {
                return new OperationResult<ApplicationSettings>.Failure(AppError.Create("SettingsConfirmationExpired",
                    "待确认设置记录已变化", "请重新读取当前待确认状态"));
            }
            try
            {
                var read = await store.LoadSettingsAsync(token).ConfigureAwait(false);
                var registration = await startup.QueryAsync(token).ConfigureAwait(false);
                if (read is OperationResult<ApplicationSettingsReadResult>.Success { Value.Recovery: ProfileRecovery.Primary } loaded
                    && loaded.Value.Settings == pending.Candidate
                    && registration is OperationResult<StartupRegistrationState>.Success { Value.IsManaged: true } actual
                    && actual.Value.IsEnabled == pending.Candidate.StartWithWindows)
                {
                    Volatile.Write(ref current, pending.Candidate);
                    Volatile.Write(ref pendingSave, null);
                    return new OperationResult<ApplicationSettings>.Success(pending.Candidate);
                }
                return new OperationResult<ApplicationSettings>.Unknown(pending.Error);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                errorReporter.Report(exception, "ApplicationSettingsPendingVerification");
                return new OperationResult<ApplicationSettings>.Unknown(pending.Error);
            }
        }, token);

    public Task<OperationResult<Unit>> DiscardPendingAsync(Guid confirmationId, CancellationToken token) => RunAsync(() =>
    {
        if (PendingSave is not { } pending || pending.ConfirmationId != confirmationId)
        {
            return Task.FromResult<OperationResult<Unit>>(new OperationResult<Unit>.Failure(AppError.Create(
                "SettingsConfirmationExpired", "待确认设置记录已变化", "请重新确认当前记录")));
        }
        // 仅明确放弃进程内核实记录，不重写配置或恢复注册表。
        Volatile.Write(ref pendingSave, null);
        return Task.FromResult<OperationResult<Unit>>(new OperationResult<Unit>.Success(new()));
    }, token);

    private async Task<OperationResult<T>> RunAsync<T>(Func<Task<OperationResult<T>>> work, CancellationToken cancellationToken)
    {
        bool entered = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            return await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new OperationResult<T>.Cancelled(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "ApplicationSettings");
            return new OperationResult<T>.Failure(new AppError(report.ErrorId, "ApplicationSettingsFailed",
                "应用设置操作未完成", "请检查本地权限并重新读取设置"));
        }
        finally { if (entered) { gate.Release(); } }
    }

    private static OperationResult<T> ConvertLoadFailure<T>(OperationResult<ApplicationSettingsReadResult> result) => result switch
    {
        OperationResult<ApplicationSettingsReadResult>.Failure failure => new OperationResult<T>.Failure(failure.Error),
        OperationResult<ApplicationSettingsReadResult>.Unknown unknown => new OperationResult<T>.Unknown(unknown.Error),
        _ => new OperationResult<T>.Cancelled()
    };
    private static OperationResult<T> Unsupported<T>() => new OperationResult<T>.Failure(AppError.Create("SettingsFormatUnsupported",
        "当前设置来自较新版本，不能覆盖", "请使用兼容版本恢复设置"));
}
