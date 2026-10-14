namespace UsbDisplayHost.Core.Operations;

public sealed record AppError(string ErrorId, string Code, string Reason, string Action)
{
    public static AppError Create(string code, string reason, string action) =>
        new(Guid.NewGuid().ToString("N"), code, reason, action);
}
