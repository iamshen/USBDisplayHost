using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UsbDisplayHost.Core.Telemetry;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Rendering.Scenes;

internal sealed partial class ScenePainter
{
    private const double SceneWidth = ThemeElement.CanvasWidth;
    private const double SceneHeight = ThemeElement.CanvasHeight;
    private static readonly Rect SceneBounds = new(0, 0, SceneWidth, SceneHeight);
    private static readonly IReadOnlyDictionary<ThemeBackdrop, Lazy<BitmapSource>> ScenicBackdrops =
        new[] { ThemeBackdrop.AlpineLake, ThemeBackdrop.Leaves, ThemeBackdrop.Jellyfish, ThemeBackdrop.StarTrails }
            .ToDictionary(backdrop => backdrop, backdrop => new Lazy<BitmapSource>(() => LoadBackdrop(backdrop)));

    private void DrawScene(DrawingContext drawing, RenderRequest request, ThemeContent.Scene scene,
        double width, double height, BitmapSource? image)
    {
        double scale = Math.Min(width / SceneWidth, height / SceneHeight);
        drawing.DrawRectangle(Brush(scene.Background), null, new Rect(0, 0, width, height));
        drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale,
            (width - SceneWidth * scale) / 2, (height - SceneHeight * scale) / 2));
        drawing.PushClip(new RectangleGeometry(SceneBounds));
        DrawBackdrop(drawing, scene);
        foreach (var element in scene.Elements)
        {
            drawing.PushTransform(new TranslateTransform(element.X, element.Y));
            DrawElement(drawing, request, scene.Backdrop, element, image);
            drawing.Pop();
        }
        drawing.Pop();
        drawing.Pop();
    }

    private static void DrawBackdrop(DrawingContext drawing, ThemeContent.Scene scene)
    {
        Brush backdrop = scene.Backdrop switch
        {
            ThemeBackdrop.Obsidian => Gradient("#323940", "#0E1013"),
            ThemeBackdrop.Amber => Gradient("#573622", "#1E110B"),
            ThemeBackdrop.Terminal => Gradient("#16352E", "#0D211D"),
            ThemeBackdrop.Ivory => Gradient("#F2EBDF", "#BAAB97"),
            _ => Brush(scene.Background)
        };
        drawing.DrawRectangle(backdrop, null, SceneBounds);
        if (ScenicBackdrops.TryGetValue(scene.Backdrop, out var scenic))
        {
            drawing.DrawImage(scenic.Value, SceneBounds);
        }
        else if (scene.Backdrop == ThemeBackdrop.ClassicLandscape)
        {
            DrawImage(drawing, BuiltInBackground.Value, ImageFit.Contain, SceneWidth, SceneHeight);
        }
        else if (scene.Backdrop == ThemeBackdrop.Obsidian)
        {
            var glow = new RadialGradientBrush(Color.FromArgb(150, 19, 22, 25), Color.FromArgb(0, 19, 22, 25))
                { RadiusX = 0.5, RadiusY = 0.5 };
            drawing.DrawEllipse(glow, null, new Point(960, 240), 580, 160);
        }
    }

    private static BitmapSource LoadBackdrop(ThemeBackdrop backdrop)
    {
        using var stream = typeof(ScenePainter).Assembly.GetManifestResourceStream(
            $"UsbDisplayHost.Rendering.Backdrops.{backdrop}.jpg")
            ?? throw new InvalidDataException("Theme backdrop is missing.");
        var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        image.Freeze();
        return image;
    }

    private void DrawElement(DrawingContext drawing, RenderRequest request, ThemeBackdrop backdrop,
        ThemeElement element, BitmapSource? image)
    {
        var (width, height) = element.GetSize();
        var color = Brush(element.Color);
        switch (element.Kind)
        {
            case ThemeElementKind.Image when image is not null:
                DrawImage(drawing, image, element.Fit, width, height);
                break;
            case ThemeElementKind.Clock:
                string format = element.Use24HourClock ? "HH:mm" : "hh:mm tt";
                var text = FittedText(request.LocalTime.ToString(format, CultureInfo.CurrentCulture), color, element.FontSize, width);
                drawing.DrawText(text, new Point((width - text.Width) / 2, 0));
                if (element.ShowDate)
                {
                    var date = FittedText(request.LocalTime.ToString("D", CultureInfo.CurrentCulture),
                        color, element.FontSize * .28, width);
                    drawing.DrawText(date, new Point((width - date.Width) / 2, element.FontSize * 1.19));
                }
                break;
            case ThemeElementKind.FlipClock:
                DrawFlipClock(drawing, request.LocalTime, element, color, width, height);
                break;
            case ThemeElementKind.AnalogClock:
                DrawAnalogClock(drawing, request.LocalTime, color, width);
                break;
            case ThemeElementKind.Text:
                drawing.DrawText(FittedText(element.Text, color, element.FontSize, width), new Point());
                break;
            case ThemeElementKind.CpuUsage:
            case ThemeElementKind.MemoryUsage:
            case ThemeElementKind.HostTemperature:
            case ThemeElementKind.DeviceTemperature:
                DrawSceneMetric(drawing, request, backdrop, element, color, width);
                break;
        }
    }

    private void DrawSceneMetric(DrawingContext drawing, RenderRequest request, ThemeBackdrop backdrop,
        ThemeElement element, Brush color, double width)
    {
        var (source, kind, label) = element.Kind switch
        {
            ThemeElementKind.CpuUsage => (MetricSource.Host, MetricKind.CpuUsage, "CPU"),
            ThemeElementKind.MemoryUsage => (MetricSource.Host, MetricKind.MemoryUsage, "RAM"),
            ThemeElementKind.HostTemperature => (MetricSource.Host, MetricKind.Temperature, "主机温度"),
            _ => (MetricSource.Device, MetricKind.Temperature, "设备温度")
        };
        var metric = ReadMetric(request, source, kind, label);
        string value = metric.Unavailable ? "不可用" : metric.Value + metric.Unit;
        if (metric.Stale) { value += "（过期）"; }
        var text = FittedText($"{label}  {value}", color, element.FontSize, width);
        drawing.DrawText(text, new Point(backdrop == ThemeBackdrop.Obsidian ? width - text.Width : 0, 0));
        if (backdrop != ThemeBackdrop.Terminal || kind == MetricKind.Temperature) { return; }
        const int segments = 14;
        double segment = width / segments;
        double progress = !metric.Unavailable && double.TryParse(metric.Value, CultureInfo.CurrentCulture, out double number)
            ? Math.Clamp(number / 100, 0, 1) : 0;
        var inactive = new SolidColorBrush(Color.FromArgb(35, element.Color.Red, element.Color.Green, element.Color.Blue));
        for (int index = 0; index < segments; index++)
        {
            drawing.DrawRectangle(index < Math.Round(progress * segments) ? color : inactive, null,
                new Rect(index * segment, element.FontSize * 1.38, segment - 5, element.FontSize * .3));
        }
    }

    private static void DrawFlipClock(DrawingContext drawing, DateTimeOffset time, ThemeElement element, Brush color,
        double width, double height)
    {
        var frame = new Rect(0, 0, width, height);
        drawing.DrawRoundedRectangle(Gradient("#201813", "#3A2518"), new Pen(new SolidColorBrush(Color.FromRgb(128, 85, 50)), 6),
            frame, height * .15, height * .15);
        double panelWidth = width * .425;
        double panelHeight = height * .76;
        double panelTop = height * .12;
        var edge = new Pen(new SolidColorBrush(Color.FromRgb(118, 84, 58)), 3);
        string[] digits =
        [
            time.ToString(element.Use24HourClock ? "HH" : "hh", CultureInfo.CurrentCulture),
            time.ToString("mm", CultureInfo.CurrentCulture)
        ];
        for (int index = 0; index < digits.Length; index++)
        {
            double left = width * .058 + index * width * .458;
            drawing.DrawRoundedRectangle(Gradient("#36291F", "#201811"), edge,
                new Rect(left, panelTop, panelWidth, panelHeight), height * .08, height * .08);
            var text = FittedText(digits[index], color, element.FontSize, panelWidth * .85, FontWeights.Bold);
            drawing.DrawText(text, new Point(left + (panelWidth - text.Width) / 2, (height - text.Height) / 2));
            drawing.DrawLine(new Pen(Brushes.Black, 4), new Point(left, height / 2), new Point(left + panelWidth, height / 2));
        }
    }

    private static void DrawAnalogClock(DrawingContext drawing, DateTimeOffset time, Brush color, double width)
    {
        double radius = width / 2;
        var center = new Point(radius, radius);
        drawing.DrawEllipse(Gradient("#FCF9F3", "#DED6C9"), new Pen(new SolidColorBrush(Color.FromRgb(179, 159, 134)), width * .015),
            center, radius * .975, radius * .975);
        for (int hour = 0; hour < 12; hour++)
        {
            drawing.DrawLine(new Pen(color, width * .005), OnDial(center, hour / 12.0, radius * .78),
                OnDial(center, hour / 12.0, radius * .92));
        }
        double minute = (time.Minute + time.Second / 60.0) / 60.0;
        double hourHand = (time.Hour + minute) / 12.0;
        drawing.DrawLine(new Pen(color, width * .017), center, OnDial(center, hourHand, radius * .51));
        drawing.DrawLine(new Pen(color, width * .009), center, OnDial(center, minute, radius * .77));
        drawing.DrawEllipse(color, null, center, width * .024, width * .024);
    }

    private static Point OnDial(Point center, double fraction, double length)
    {
        double angle = fraction * (2 * Math.PI);
        return new Point(center.X + Math.Sin(angle) * length, center.Y - Math.Cos(angle) * length);
    }

    private static LinearGradientBrush Gradient(string top, string bottom) =>
        new((Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom),
            new Point(0.5, 0), new Point(0.5, 1));
}
