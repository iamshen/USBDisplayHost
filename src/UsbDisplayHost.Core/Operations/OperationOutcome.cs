namespace UsbDisplayHost.Core.Operations;

public enum OperationDisposition { Succeeded, Failed, Cancelled, Unknown }
public enum DeliveryState { NotAttempted, Completed, Failed, Unknown }
public enum PresentationState { Unknown, Confirmed, Failed }

public sealed record OperationOutcome(Guid OperationId, OperationDisposition Disposition, AppError? Error);
public sealed record ProfileApplicationOutcome(
    OperationOutcome Operation, DeliveryState Transmission, DeliveryState Persistence, PresentationState Presentation);
