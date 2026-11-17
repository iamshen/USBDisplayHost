using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.WinUI.Views;

internal sealed record ThemePreset(string Name, int Category, ThemeContent.Scene Content)
{
    public string ImagePath => $"ms-appx:///Assets/Workspace/theme{(int)Content.Backdrop}.jpg";
}

internal static class ThemePresets
{
    private static readonly RgbColor White = new(244, 246, 248);
    private static readonly RgbColor Mint = new(73, 221, 164);
    private static readonly RgbColor Amber = new(247, 181, 97);
    private static readonly RgbColor Brass = new(161, 111, 55);
    private static readonly RgbColor Background = new(18, 20, 25);

    public static IReadOnlyList<ThemePreset> All { get; } =
    [
        Preset("黑曜时钟", 1, ThemeBackdrop.Obsidian,
            Element(ThemeElementKind.Clock, 320, 370, 240), Element(ThemeElementKind.CpuUsage, 1320, 984, 45)),
        Preset("琥珀翻页", 1, ThemeBackdrop.Amber, Element(ThemeElementKind.FlipClock, 160, 230, 400, Amber)),
        Preset("薄暮山湖", 3, ThemeBackdrop.AlpineLake, Element(ThemeElementKind.Clock, 555, 65, 240)),
        Preset("终端仪表", 2, ThemeBackdrop.Terminal, Element(ThemeElementKind.CpuUsage, 245, 235, 117, Mint),
            Element(ThemeElementKind.MemoryUsage, 245, 596, 117, Mint)),
        Preset("雾绿植物", 3, ThemeBackdrop.Leaves, Element(ThemeElementKind.Clock, 75, 350, 200)),
        Preset("深海流光", 3, ThemeBackdrop.Jellyfish, Element(ThemeElementKind.Clock, 1240, 502, 176)),
        Preset("素白表盘", 1, ThemeBackdrop.Ivory, Element(ThemeElementKind.AnalogClock, 520, 100, 440, Brass)),
        Preset("星轨", 3, ThemeBackdrop.StarTrails, Element(ThemeElementKind.Clock, 590, 403, 220))
    ];

    public static ThemeContent.Scene ToScene(ThemeContent content) => content switch
    {
        ThemeContent.Scene scene => scene,
        ThemeContent.Image { AssetId: { } id } image => new(ThemeBackdrop.Solid, image.Background, id,
            [Element(ThemeElementKind.Image, 0, 0, 100) with { Fit = image.Fit }]),
        ThemeContent.Image image => new(ThemeBackdrop.ClassicLandscape, image.Background, null, []),
        ThemeContent.Clock clock => new(ThemeBackdrop.Solid, clock.Background, null,
            [Element(ThemeElementKind.Clock, 340, 293, 360, clock.Foreground) with
                { ShowDate = clock.ShowDate, Use24HourClock = clock.Use24HourClock }]),
        ThemeContent.Dashboard dashboard => ToDashboard(dashboard),
        _ => throw new InvalidOperationException("主题类型未定义")
    };

    public static ThemeElement Element(ThemeElementKind kind, int x, int y, int fontSize, RgbColor? color = null) =>
        new ThemeElement(kind, x, y, fontSize, color ?? White, kind == ThemeElementKind.Text ? "文字" : "").MoveTo(x, y);

    private static ThemePreset Preset(string name, int category, ThemeBackdrop backdrop, params ThemeElement[] elements) =>
        new(name, category, new(backdrop, Background, null, [.. elements]));

    private static ThemeContent.Scene ToDashboard(ThemeContent.Dashboard dashboard)
    {
        var elements = new List<ThemeElement>();
        foreach (var (enabled, kind) in new[]
        {
            (dashboard.ShowCpuUsage, ThemeElementKind.CpuUsage),
            (dashboard.ShowMemoryUsage, ThemeElementKind.MemoryUsage),
            (dashboard.ShowHostTemperature, ThemeElementKind.HostTemperature),
            (dashboard.ShowDeviceTemperature, ThemeElementKind.DeviceTemperature)
        })
        {
            if (enabled) { elements.Add(Element(kind, 280, 150 + elements.Count * 214, 84, dashboard.Foreground)); }
        }
        return new(ThemeBackdrop.Solid, dashboard.Background, null, [.. elements]);
    }
}
