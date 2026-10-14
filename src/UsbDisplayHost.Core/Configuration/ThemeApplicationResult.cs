using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Core.Transport;

namespace UsbDisplayHost.Core.Configuration;

public sealed record ThemeApplicationBase(OperationContext Context, Themes.ThemeContent Content, DeviceSettings Settings,
    int? ConfirmedBrightness, PowerState PowerIntent, int Width, int Height, int MaximumFrameBytes, long PipelineRevision);
public enum ThemeSaveState { NotAttempted, Saved, Failed, Unknown }
public enum ThemePresentationState { NotProvided }
public sealed record ThemeApplicationResult(long FrozenDraftRevision,
    OperationResult<FrameTransferReceipt> Transmission, bool ActiveCommitted, ThemeSaveState SaveState,
    ThemePresentationState Presentation, Guid? SaveRetryId, AppError? Error);
public sealed record ThemeApplicationAttempt(OperationResult<ThemeApplicationResult> Result,
    DeviceRejectionProof? RejectionBeforeIo);
public sealed record PendingThemeSave(Guid RetryId, OperationContext Context, DeviceProfile Profile,
    DeviceProfile? PreviousRestore, ThemeSaveState State, AppError? Error)
{
    public bool CanVerifyProfile { get; init; } = true;
}
