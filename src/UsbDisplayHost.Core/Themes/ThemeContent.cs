using System.Collections.Immutable;

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

    public sealed record Scene(ThemeBackdrop Backdrop, RgbColor Background, string? AssetId,
        ImmutableArray<ThemeElement> Elements) : ThemeContent
    {
        public bool Equals(Scene? other) => other is not null && Backdrop == other.Backdrop
            && Background == other.Background && AssetId == other.AssetId
            && (Elements == other.Elements || !Elements.IsDefault && !other.Elements.IsDefault
                && Elements.SequenceEqual(other.Elements));

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Backdrop);
            hash.Add(Background);
            hash.Add(AssetId);
            if (!Elements.IsDefault)
            {
                foreach (var element in Elements) { hash.Add(element); }
            }
            return hash.ToHashCode();
        }
    }

    public string? GetImageAssetId() => this switch { Image image => image.AssetId, Scene scene => scene.AssetId, _ => null };

    public bool IsDynamic => this is Clock or Dashboard || this is Scene { Elements.IsDefault: false } scene
        && scene.Elements.Any(element => element.Kind is not (ThemeElementKind.Image or ThemeElementKind.Text));

    public static ThemeContent Default { get; } = new Image(null, ImageFit.Contain, new RgbColor(20, 23, 27));
    public static ThemeContent DefaultClock { get; } = new Clock(true, true, new RgbColor(20, 23, 27), new RgbColor(243, 245, 247));
    public static ThemeContent DefaultDashboard { get; } = new Dashboard(true, true, false, false,
        new RgbColor(20, 23, 27), new RgbColor(243, 245, 247));
}
