using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Themes;

public static class ThemeValidation
{
    public static OperationResult<Unit> Validate(ThemeContent content) => content switch
    {
        ThemeContent.Image image when !Enum.IsDefined(image.Fit) => Invalid("图片适配方式无效"),
        ThemeContent.Image { AssetId: not null } image when image.AssetId.Length != 32
            || image.AssetId.Any(character => !char.IsAsciiHexDigit(character)) => Invalid("图片素材标识无效"),
        ThemeContent.Image => new OperationResult<Unit>.Success(default),
        ThemeContent.Clock => new OperationResult<Unit>.Success(default),
        ThemeContent.Dashboard dashboard when !dashboard.ShowCpuUsage && !dashboard.ShowMemoryUsage
            && !dashboard.ShowHostTemperature && !dashboard.ShowDeviceTemperature => Invalid("请至少选择一项仪表盘指标"),
        ThemeContent.Dashboard => new OperationResult<Unit>.Success(default),
        _ => Invalid("主题内容无效")
    };

    private static OperationResult<Unit> Invalid(string reason) =>
        new OperationResult<Unit>.Failure(AppError.Create("InvalidTheme", reason, "请检查主题字段后重试"));
}
