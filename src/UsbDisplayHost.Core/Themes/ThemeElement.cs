namespace UsbDisplayHost.Core.Themes;

public enum ThemeBackdrop { Obsidian, Amber, AlpineLake, Terminal, Leaves, Jellyfish, Ivory, StarTrails, Solid, ClassicLandscape }
public enum ThemeElementKind { Clock, FlipClock, AnalogClock, CpuUsage, MemoryUsage, HostTemperature, DeviceTemperature, Image, Text }

public sealed record ThemeElement(ThemeElementKind Kind, int X, int Y, int FontSize, RgbColor Color,
    string Text = "", bool Use24HourClock = true, bool ShowDate = false, ImageFit Fit = ImageFit.Contain)
{
    public const int CanvasWidth = 1920;
    public const int CanvasHeight = 1080;
    public const int MaximumCount = 24;
    public const int MaximumFontSize = 440;
    public const int MinimumFontSize = 12;
    public const int MaximumTextLength = 120;

    public (int Width, int Height) GetSize() => Kind switch
    {
        ThemeElementKind.Image => (CanvasWidth, CanvasHeight),
        ThemeElementKind.AnalogClock => (FontSize * 2, FontSize * 2),
        ThemeElementKind.FlipClock => (FontSize * 4, (int)(FontSize * 1.55)),
        ThemeElementKind.Clock => ((int)(FontSize * 3.38), (int)(FontSize * (ShowDate ? 1.65 : 1.23))),
        ThemeElementKind.Text => (Math.Min(CanvasWidth, FontSize * Math.Max(2, Text.Length)), (int)(FontSize * 1.45)),
        _ => (Math.Min(CanvasWidth, FontSize * 12), FontSize * 2)
    };

    public ThemeElement MoveTo(int x, int y)
    {
        var (width, height) = GetSize();
        // 图片以画布为基准，可以移动以改变裁切取景。
        if (Kind == ThemeElementKind.Image)
        {
            return this with { X = Math.Clamp(x, -CanvasWidth + 1, CanvasWidth - 1),
                Y = Math.Clamp(y, -CanvasHeight + 1, CanvasHeight - 1) };
        }
        return this with { X = Math.Clamp(x, 0, CanvasWidth - width), Y = Math.Clamp(y, 0, CanvasHeight - height) };
    }
}
