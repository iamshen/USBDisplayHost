namespace UsbDisplayHost.Core.Operations;

public abstract record OperationResult<T>
{
    private OperationResult() { }

    public sealed record Success(T Value) : OperationResult<T>;
    public sealed record Failure(AppError Error) : OperationResult<T>;
    public sealed record Cancelled : OperationResult<T>;
    public sealed record Unknown(AppError Error) : OperationResult<T>;
}

public readonly record struct Unit;
