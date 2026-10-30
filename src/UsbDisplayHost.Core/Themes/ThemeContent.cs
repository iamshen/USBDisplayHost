namespace UsbDisplayHost.Core.Themes;

public readonly record struct RgbColor(byte Red, byte Green, byte Blue);
public enum ImageFit { Contain, Cover }

public abstract record ThemeContent
{
    private ThemeContent() { }

    public sealed record Image(string? AssetId, ImageFit Fit, RgbColor Background) : ThemeContent;
    public sealed record Clock(bool Use24HourClock, bool ShowDate, RgbColor Background, RgbColor Foreground) : ThemeContent;
    public sealed record Dashboard(bool ShowCpuUsage, bool ShowMemoryUsage, bool ShowHostTemperature,
        bool ShowDeviceTemperature, RgbColor Background, RgbColor Foreground) : ThemeContent;

    public static ThemeContent Default { get; } = new Image(null, ImageFit.Contain, new RgbColor(20, 23, 27));
    public static ThemeContent DefaultClock { get; } = new Clock(true, true, new RgbColor(20, 23, 27), new RgbColor(243, 245, 247));
    public static ThemeContent DefaultDashboard { get; } = new Dashboard(true, true, false, false,
        new RgbColor(20, 23, 27), new RgbColor(243, 245, 247));
}
