using System.Collections.Immutable;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Diagnostics;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Core.Telemetry;
using UsbDisplayHost.Core.Themes;
using UsbDisplayHost.Core.Transport;

namespace UsbDisplayHost.Core.Configuration;

// 每个所选设备一个服务；主题／设置应用与本地重试互斥，不修改调用方草稿对象。
public sealed class ThemeApplicationService(DeviceKey deviceKey, DeviceCoordinator coordinator,
    IFrameRenderer renderer, FrameBoundaryScheduler scheduler, IProfileStore store, IAssetStore assets,
    TimeProvider timeProvider, IErrorReporter errorReporter)
{
    private int busy;
    private PendingThemeSave? pendingSave;
    private AssetReferenceLease? activeAsset;
    private AssetReferenceLease? pendingAsset;
    private bool sessionReferencesClosed;
    public PendingThemeSave? PendingSave => Volatile.Read(ref pendingSave);
    internal DeviceKey DeviceKey => deviceKey;
    internal DeviceCoordinator Coordinator => coordinator;
    internal FrameBoundaryScheduler Scheduler => scheduler;

    public Task<OperationResult<ThemeApplicationResult>> ApplyAsync(OperationContext context, ThemeDraft frozenDraft,
        ImmutableArray<MetricSnapshot> metrics, CancellationToken cancellationToken) =>
        ApplyCoreAsync(context, frozenDraft, null, metrics, cancellationToken);

    public Task<OperationResult<ThemeApplicationResult>> ApplySettingsAsync(OperationContext context, DeviceSettingsDraft frozenDraft,
        ImmutableArray<MetricSnapshot> metrics, CancellationToken cancellationToken) =>
        frozenDraft.DeviceKey == deviceKey && frozenDraft.Revision == context.ContentRevision
        ? ApplyCoreAsync(context, null, frozenDraft.Settings, metrics, cancellationToken)
        : Task.FromResult(Failed<ThemeApplicationResult>("SettingsCandidateMismatch", "设置草稿与受理操作不匹配"));

    // 会话工作保留组合结果，只有未进入发送阶段的明确拒绝才可交给协调器保留原会话。
    public async Task<ThemeApplicationAttempt> ApplyForSessionAsync(OperationContext context, ThemeDraft? themeDraft,
        DeviceSettingsDraft? settingsDraft, ImmutableArray<MetricSnapshot> metrics, CancellationToken cancellationToken)
    {
        bool submitted = false;
        OperationResult<ThemeApplicationResult> result;
        if ((themeDraft is null) == (settingsDraft is null)
            || settingsDraft is not null && (settingsDraft.DeviceKey != deviceKey || settingsDraft.Revision != context.ContentRevision))
        {
            result = Failed<ThemeApplicationResult>("ApplicationDraftMismatch", "请选择与当前操作匹配的单一冻结草稿");
        }
        else
        {
            result = await ApplyCoreAsync(context, themeDraft, settingsDraft?.Settings, metrics, cancellationToken,
                () => submitted = true).ConfigureAwait(false);
        }
        var rejection = !submitted && result is OperationResult<ThemeApplicationResult>.Failure or OperationResult<ThemeApplicationResult>.Cancelled
            ? new DeviceRejectionProof(context) : null;
        return new(result, rejection);
    }

    private async Task<OperationResult<ThemeApplicationResult>> ApplyCoreAsync(OperationContext context, ThemeDraft? frozenDraft,
        DeviceSettings? settingsCandidate, ImmutableArray<MetricSnapshot> metrics, CancellationToken cancellationToken,
        Action? enteringSend = null)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) { return Failed<ThemeApplicationResult>("ThemeApplicationBusy", "主题应用或保存正在执行"); }
        OperationResult<FrameTransferReceipt>? transmission = null;
        bool activeCommitted = false;
        bool sendAttempted = false;
        AssetReferenceLease?[] reserved = new AssetReferenceLease?[3];
        try
        {
            if (sessionReferencesClosed) { return Failed<ThemeApplicationResult>("ThemeSessionClosed", "该会话已经关闭，不能再次发送"); }
            cancellationToken.ThrowIfCancellationRequested();
            if (context.DeviceKey != deviceKey || !coordinator.IsCurrent(context)
                || (frozenDraft is null ? settingsCandidate is null : frozenDraft.DeviceKey != deviceKey
                    || frozenDraft.Revision != context.ContentRevision))
            {
                return Failed<ThemeApplicationResult>("ThemeCandidateMismatch", "冻结草稿与受理操作不匹配");
            }
            if (!metrics.IsDefault && metrics.Length > 64) { return Failed<ThemeApplicationResult>("TooManyThemeMetrics", "主题指标快照超过受限数量"); }
            if (frozenDraft is not null && ThemeValidation.Validate(frozenDraft.Content) is OperationResult<Unit>.Failure invalid)
            {
                return new OperationResult<ThemeApplicationResult>.Failure(invalid.Error);
            }
            var loaded = await store.LoadAsync(deviceKey, cancellationToken).ConfigureAwait(false);
            using var restoredReference = (loaded as OperationResult<ProfileReadResult>.Success)?.Value;
            if (loaded is OperationResult<ProfileReadResult>.Failure loadFailed) { return new OperationResult<ThemeApplicationResult>.Failure(loadFailed.Error); }
            if (loaded is OperationResult<ProfileReadResult>.Unknown loadUnknown) { return new OperationResult<ThemeApplicationResult>.Unknown(loadUnknown.Error); }
            if (loaded is not OperationResult<ProfileReadResult>.Success restored) { return new OperationResult<ThemeApplicationResult>.Cancelled(); }
            if (restored.Value.Recovery == ProfileRecovery.UnsupportedVersion)
            {
                return Failed<ThemeApplicationResult>("UnsupportedRestoreVersion", "恢复配置来自更高版本，未开始应用");
            }
            var captured = coordinator.CaptureThemeApplicationBase(context, restored.Value.Profile, frozenDraft?.Content, settingsCandidate);
            if (captured is OperationResult<ThemeApplicationBase>.Failure rejected) { return new OperationResult<ThemeApplicationResult>.Failure(rejected.Error); }
            var basis = ((OperationResult<ThemeApplicationBase>.Success)captured).Value;
            var candidate = new ActiveProfile(context, basis.Content, basis.Settings, basis.PipelineRevision);
            if (candidate.Content is ThemeContent.Image { AssetId: not null } image)
            {
                AssetReferenceOwner[] owners = [AssetReferenceOwner.Candidate, AssetReferenceOwner.ActiveProfile, AssetReferenceOwner.PendingRestore];
                for (int index = 0; index < owners.Length; index++)
                {
                    var acquired = assets.AcquireReference(image.AssetId, owners[index]);
                    if (acquired is OperationResult<AssetReferenceLease>.Failure referenceFailure)
                    {
                        return new OperationResult<ThemeApplicationResult>.Failure(referenceFailure.Error);
                    }
                    if (acquired is not OperationResult<AssetReferenceLease>.Success lease)
                    {
                        return Failed<ThemeApplicationResult>("CandidateAssetReferenceUnconfirmed", "候选素材引用未确认，未开始发送");
                    }
                    reserved[index] = lease.Value;
                }
            }
            var frameContext = new FrameContext(deviceKey, context.SessionGeneration, context.OperationId, basis.PipelineRevision);
            var numbered = scheduler.AllocateFrameNumber();
            if (numbered is OperationResult<long>.Failure numberingFailure) { return new OperationResult<ThemeApplicationResult>.Failure(numberingFailure.Error); }
            var request = new RenderRequest(candidate.Content, candidate.Settings, basis.Width, basis.Height,
                basis.MaximumFrameBytes, frameContext, ((OperationResult<long>.Success)numbered).Value, FramePurpose.Active,
                timeProvider.GetLocalNow(), metrics.IsDefault ? ImmutableArray<MetricSnapshot>.Empty : metrics);
            var rendered = await renderer.RenderAsync(request, cancellationToken).ConfigureAwait(false);
            if (rendered is OperationResult<EncodedFrame>.Failure renderFailed) { return new OperationResult<ThemeApplicationResult>.Failure(renderFailed.Error); }
            if (rendered is OperationResult<EncodedFrame>.Unknown renderUnknown) { return new OperationResult<ThemeApplicationResult>.Unknown(renderUnknown.Error); }
            if (rendered is not OperationResult<EncodedFrame>.Success frame) { return new OperationResult<ThemeApplicationResult>.Cancelled(); }
            if (frame.Value.Context != frameContext || frame.Value.FrameNumber != request.FrameNumber
                || frame.Value.Purpose != FramePurpose.Active || frame.Value.Width != basis.Width || frame.Value.Height != basis.Height)
            {
                return Failed<ThemeApplicationResult>("CandidateRenderMismatch", "首帧渲染收据与冻结候选不匹配");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!coordinator.IsCurrent(context)) { return Failed<ThemeApplicationResult>("ThemeCandidateExpired", "首帧准备期间设备会话已失效"); }
            var prepared = await scheduler.PrepareCandidateAsync(frame.Value, cancellationToken).ConfigureAwait(false);
            if (prepared is OperationResult<Unit>.Failure preparationFailure) { return new OperationResult<ThemeApplicationResult>.Failure(preparationFailure.Error); }
            if (prepared is OperationResult<Unit>.Unknown preparationUnknown) { return new OperationResult<ThemeApplicationResult>.Unknown(preparationUnknown.Error); }
            if (prepared is not OperationResult<Unit>.Success) { return new OperationResult<ThemeApplicationResult>.Cancelled(); }
            cancellationToken.ThrowIfCancellationRequested();
            if (!coordinator.IsCurrent(context)) { return Failed<ThemeApplicationResult>("ThemeCandidateExpired", "首帧校验期间设备会话已失效"); }
            sendAttempted = true;
            enteringSend?.Invoke();
            transmission = await scheduler.SubmitFrameAsync(frame.Value, true, cancellationToken).ConfigureAwait(false);
            if (transmission is not OperationResult<FrameTransferReceipt>.Success sent)
            {
                return Completed(context.ContentRevision, transmission, false, ThemeSaveState.NotAttempted, null, TransmissionError(transmission));
            }
            var committed = await coordinator.CommitActiveProfileAsync(candidate, sent.Value, scheduler.Pacing).ConfigureAwait(false);
            if (committed is not OperationResult<ActiveProfile>.Success)
            {
                return Completed(context.ContentRevision, transmission, false, ThemeSaveState.NotAttempted, null,
                    committed is OperationResult<ActiveProfile>.Failure commitFailed ? commitFailed.Error
                        : AppError.Create("ActiveCommitUnconfirmed", "已发送，但活动配置提交未确认", "停止刷新并核实设备状态"));
            }
            activeCommitted = true;
            var previousActiveAsset = activeAsset;
            activeAsset = reserved[1];
            reserved[1] = null;
            previousActiveAsset?.Dispose();
            var restoreCandidate = new DeviceProfile(deviceKey, candidate.Content, candidate.Settings,
                basis.ConfirmedBrightness, basis.PowerIntent);
            var save = new PendingThemeSave(Guid.NewGuid(), context, restoreCandidate, restored.Value.Profile, ThemeSaveState.NotAttempted, null);
            Volatile.Write(ref pendingSave, save);
            var previousPendingAsset = pendingAsset;
            pendingAsset = reserved[2];
            reserved[2] = null;
            previousPendingAsset?.Dispose();
            // 完整发送和活动提交之后不因用户取消而假装设备没有变化；保存结果单独报告。
            save = await SaveLocalAsync(save, CancellationToken.None).ConfigureAwait(false);
            return Completed(context.ContentRevision, transmission, true, save.State,
                save.State == ThemeSaveState.Saved ? null : save.RetryId, save.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && transmission is null && !sendAttempted)
        {
            return new OperationResult<ThemeApplicationResult>.Cancelled();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "ThemeApplication");
            var error = new AppError(report.ErrorId, "ThemeApplicationFailed", "主题应用阶段未完整完成", "请核实发送和保存状态后处理");
            if (transmission is null && !sendAttempted) { return new OperationResult<ThemeApplicationResult>.Failure(error); }
            transmission ??= new OperationResult<FrameTransferReceipt>.Unknown(error);
            var pending = PendingSave;
            return Completed(context.ContentRevision, transmission, activeCommitted,
                pending?.Context == context ? pending.State : ThemeSaveState.NotAttempted,
                pending?.Context == context ? pending.RetryId : null, error);
        }
        finally
        {
            foreach (var lease in reserved) { lease?.Dispose(); }
            Volatile.Write(ref busy, 0);
        }
    }

    public async Task<OperationResult<PendingThemeSave>> RetrySaveAsync(Guid retryId, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) { return Failed<PendingThemeSave>("ThemeApplicationBusy", "主题应用或保存正在执行"); }
        try
        {
            var save = PendingSave;
            if (save is null || save.RetryId != retryId || save.State == ThemeSaveState.Saved)
            {
                return Failed<PendingThemeSave>("ThemeSaveRetryExpired", "该保存重试已完成或被新的应用替代");
            }
            if (save.State == ThemeSaveState.Unknown && !save.CanVerifyProfile)
            {
                return new OperationResult<PendingThemeSave>.Unknown(save.Error ?? AppError.Create("ThemeSaveReceiptUnverified",
                    "保存收据与应用候选不一致，无法核实本次写入", "请保留诊断并明确处理待保存结果，不直接重写"));
            }
            var active = coordinator.CurrentActiveProfile;
            if (active is not null && active.Context.DeviceKey == deviceKey && active.Context != save.Context)
            {
                return Failed<PendingThemeSave>("NewerActiveProfile", "活动配置已更新，不能覆盖为旧候选");
            }
            var loaded = await store.LoadAsync(deviceKey, cancellationToken).ConfigureAwait(false);
            using var readReference = (loaded as OperationResult<ProfileReadResult>.Success)?.Value;
            if (loaded is not OperationResult<ProfileReadResult>.Success read)
            {
                return loaded switch
                {
                    OperationResult<ProfileReadResult>.Failure failure => new OperationResult<PendingThemeSave>.Failure(failure.Error),
                    OperationResult<ProfileReadResult>.Unknown unknown => new OperationResult<PendingThemeSave>.Unknown(unknown.Error),
                    _ => new OperationResult<PendingThemeSave>.Cancelled()
                };
            }
            if (read.Value.Recovery == ProfileRecovery.UnsupportedVersion) { return Failed<PendingThemeSave>("UnsupportedRestoreVersion", "更高版本恢复配置不可覆盖"); }
            if (save.State == ThemeSaveState.Unknown)
            {
                if (read.Value.Recovery == ProfileRecovery.Primary && read.Value.Profile == save.Profile)
                {
                    var confirmed = save with { State = ThemeSaveState.Saved, Error = null };
                    Volatile.Write(ref pendingSave, null);
                    ReleasePendingAsset();
                    return new OperationResult<PendingThemeSave>.Success(confirmed);
                }
                return new OperationResult<PendingThemeSave>.Unknown(save.Error ?? AppError.Create("ThemeSaveUnknown",
                    "上一次保存结果仍未知，未重复写入", "请保留诊断并核实恢复配置"));
            }
            if (read.Value.Profile is { } changed
                && !SameThemeAndSettings(changed, save.PreviousRestore) && !SameThemeAndSettings(changed, save.Profile))
            {
                return Failed<PendingThemeSave>("RecoveryProfileChanged", "恢复主题或设置已更新，不能覆盖为旧候选");
            }
            // 重试只写主题／设置候选，保留期间由其他已确认控制保存的亮度和电源意图。
            var merged = read.Value.Profile is { } latest ? save with
            {
                Profile = save.Profile with
                {
                    ConfirmedBrightness = latest.ConfirmedBrightness != save.PreviousRestore?.ConfirmedBrightness
                        ? latest.ConfirmedBrightness : save.Profile.ConfirmedBrightness,
                    PowerIntent = latest.PowerIntent != save.PreviousRestore?.PowerIntent
                        ? latest.PowerIntent : save.Profile.PowerIntent
                }
            } : save;
            var saved = await SaveLocalAsync(merged, cancellationToken).ConfigureAwait(false);
            return saved.State switch
            {
                ThemeSaveState.Saved => new OperationResult<PendingThemeSave>.Success(saved),
                ThemeSaveState.Unknown => new OperationResult<PendingThemeSave>.Unknown(saved.Error!),
                _ => new OperationResult<PendingThemeSave>.Failure(saved.Error!)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new OperationResult<PendingThemeSave>.Cancelled(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "ThemeSaveRetry");
            return new OperationResult<PendingThemeSave>.Failure(new(report.ErrorId, "ThemeSaveRetryFailed", "恢复配置重试未完成", "请保留当前应用状态并稍后处理"));
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    private async Task<PendingThemeSave> SaveLocalAsync(PendingThemeSave save, CancellationToken cancellationToken)
    {
        PendingThemeSave outcome;
        try
        {
            var attempted = await store.SaveApplicationAsync(save.Profile, save.PreviousRestore, cancellationToken).ConfigureAwait(false);
            OperationResult<Unit> saved;
            if (attempted is OperationResult<ProfileApplicationSaveAttempt>.Success receipt)
            {
                if (receipt.Value is null || receipt.Value.Candidate is not { } candidate
                    || !SameThemeAndSettings(candidate, save.Profile) || receipt.Value.Write is null
                    || candidate.ConfirmedBrightness is < 0 or > 100 || !Enum.IsDefined(candidate.PowerIntent))
                {
                    save = save with { CanVerifyProfile = false };
                    saved = new OperationResult<Unit>.Unknown(AppError.Create("ThemeSaveReceiptMismatch",
                        "保存收据不属于已发送的主题或设置候选", "请保留诊断并处理原待保存结果，不重复写入或发送"));
                }
                else
                {
                    save = save with { Profile = candidate };
                    saved = receipt.Value.Write;
                }
            }
            else
            {
                saved = attempted switch
                {
                    OperationResult<ProfileApplicationSaveAttempt>.Failure failure => new OperationResult<Unit>.Failure(failure.Error),
                    OperationResult<ProfileApplicationSaveAttempt>.Unknown unknown => new OperationResult<Unit>.Unknown(unknown.Error),
                    _ => new OperationResult<Unit>.Cancelled()
                };
            }
            outcome = saved switch
            {
                OperationResult<Unit>.Success => save with { State = ThemeSaveState.Saved, Error = null },
                OperationResult<Unit>.Failure failure => save with { State = ThemeSaveState.Failed, Error = failure.Error },
                OperationResult<Unit>.Unknown unknown => save with { State = ThemeSaveState.Unknown, Error = unknown.Error },
                _ => save with { State = ThemeSaveState.Failed, Error = AppError.Create("ThemeSaveCancelled", "已应用，但本地保存取消", "可仅重试保存，无需重发画面") }
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var report = errorReporter.Report(exception, "ThemeRestoreSave");
            outcome = save with { State = ThemeSaveState.Unknown, Error = new(report.ErrorId, "ThemeSaveUnconfirmed",
                "已应用，但恢复配置保存结果未知", "请核实保存结果，不要直接重复写入") };
        }
        Volatile.Write(ref pendingSave, outcome.State == ThemeSaveState.Saved ? null : outcome);
        if (outcome.State == ThemeSaveState.Saved) { ReleasePendingAsset(); }
        return outcome;
    }

    private void ReleasePendingAsset()
    {
        var released = pendingAsset;
        pendingAsset = null;
        released?.Dispose();
    }

    // 仅由管线在原生关闭确认后调用；未完成保存必须保留或明确放弃指定候选。
    internal OperationResult<Unit> ReleaseReferencesAfterClose(Guid? discardSaveId)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) { return Failed<Unit>("ThemeApplicationBusy", "本地配置操作尚未结束"); }
        try
        {
            sessionReferencesClosed = true;
            var previous = activeAsset;
            activeAsset = null;
            previous?.Dispose();
            if (PendingSave is { } pending && discardSaveId != pending.RetryId)
            {
                return Failed<Unit>("PendingRestoreSaveUnresolved", "设备已关闭，但恢复配置的待保存结果尚未处理");
            }
            Volatile.Write(ref pendingSave, null);
            ReleasePendingAsset();
            return new OperationResult<Unit>.Success(new Unit());
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    private static OperationResult<ThemeApplicationResult> Completed(long revision,
        OperationResult<FrameTransferReceipt> transmission, bool active, ThemeSaveState state, Guid? retryId, AppError? error) =>
        new OperationResult<ThemeApplicationResult>.Success(new(revision, transmission, active, state,
            ThemePresentationState.NotProvided, retryId, error));

    private static AppError? TransmissionError(OperationResult<FrameTransferReceipt> transmission) => transmission switch
    {
        OperationResult<FrameTransferReceipt>.Failure failure => failure.Error,
        OperationResult<FrameTransferReceipt>.Unknown unknown => unknown.Error,
        _ => null
    };

    private static bool SameThemeAndSettings(DeviceProfile profile, DeviceProfile? other) =>
        other is not null && profile.DeviceKey == other.DeviceKey && profile.Content == other.Content && profile.Settings == other.Settings;

    private static OperationResult<T> Failed<T>(string code, string reason) => new OperationResult<T>.Failure(
        AppError.Create(code, reason, "请等待当前操作结束并核实设备与恢复配置"));
}
