using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Themes;

public static class ThemeValidation
{
    public static OperationResult<Unit> Validate(ThemeContent content) => content switch
    {
        ThemeContent.Image image when !Enum.IsDefined(image.Fit) => Invalid("图片适配方式无效"),
        ThemeContent.Image image when !ValidAsset(image.AssetId) => Invalid("图片素材标识无效"),
        ThemeContent.Image => new OperationResult<Unit>.Success(default),
        ThemeContent.Clock => new OperationResult<Unit>.Success(default),
        ThemeContent.Dashboard dashboard when !dashboard.ShowCpuUsage && !dashboard.ShowMemoryUsage
            && !dashboard.ShowHostTemperature && !dashboard.ShowDeviceTemperature => Invalid("请至少选择一项仪表盘指标"),
        ThemeContent.Dashboard => new OperationResult<Unit>.Success(default),
        ThemeContent.Scene scene => ValidateScene(scene),
        _ => Invalid("主题内容无效")
    };

    private static OperationResult<Unit> ValidateScene(ThemeContent.Scene scene)
    {
        if (!Enum.IsDefined(scene.Backdrop) || !ValidAsset(scene.AssetId) || scene.Elements.IsDefault
            || scene.Elements.Length > ThemeElement.MaximumCount
            || scene.Elements.Any(element => element is null || !Enum.IsDefined(element.Kind)
                || !Enum.IsDefined(element.Fit) || element.FontSize is < ThemeElement.MinimumFontSize or > ThemeElement.MaximumFontSize
                || element.Text is null || element.Text.Length > ThemeElement.MaximumTextLength
                || element != element.MoveTo(element.X, element.Y)))
        {
            return Invalid("元素位置、字号或内容无效");
        }
        if (scene.Elements.Count(element => element.Kind == ThemeElementKind.Image) != (scene.AssetId is null ? 0 : 1))
        {
            return Invalid("图片元素与素材不匹配");
        }
        return new OperationResult<Unit>.Success(default);
    }

    private static bool ValidAsset(string? id) => id is null || id.Length == 32 && id.All(char.IsAsciiHexDigit);

    private static OperationResult<Unit> Invalid(string reason) =>
        new OperationResult<Unit>.Failure(AppError.Create("InvalidTheme", reason, "请检查主题字段后重试"));
}
