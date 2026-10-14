using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UsbDisplayHost.Core.Telemetry;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Rendering.Scenes;

internal sealed class ScenePainter(TimeProvider timeProvider)
{
    private readonly MetricFreshness freshness = new(timeProvider);

    public void Paint(DrawingContext drawing, RenderRequest request, double width, double height, BitmapSource? image)
    {
        switch (request.Content)
        {
            case ThemeContent.Image content:
                drawing.DrawRectangle(Brush(content.Background), null, new Rect(0, 0, width, height));
                if (image is null) { DrawBuiltInBackground(drawing, width, height, content.Background); }
                else { DrawImage(drawing, image, content.Fit, width, height); }
                break;
            case ThemeContent.Clock content:
                drawing.DrawRectangle(Brush(content.Background), null, new Rect(0, 0, width, height));
                string clock = request.LocalTime.ToString(content.Use24HourClock ? "HH:mm:ss" : "hh:mm:ss tt", CultureInfo.CurrentCulture);
                double clockHeight = DrawText(drawing, clock, Brush(content.Foreground), width * .12, width, height * .36,
                    fitToWidth: true);
                if (content.ShowDate)
                {
                    DrawText(drawing, request.LocalTime.ToString("D", CultureInfo.CurrentCulture),
                        Brush(content.Foreground), width * .045, width, height * .36 + clockHeight + height * .04,
                        fitToWidth: true);
                }
                break;
            case ThemeContent.Dashboard content:
                DrawDashboard(drawing, request, content, width, height);
                break;
            default:
                throw new InvalidDataException("Unsupported theme type.");
        }
    }

    private static SolidColorBrush Brush(RgbColor color) => new(Color.FromRgb(color.Red, color.Green, color.Blue));

    private static void DrawImage(DrawingContext drawing, BitmapSource image, ImageFit fit, double width, double height)
    {
        double scale = fit == ImageFit.Contain
            ? Math.Min(width / image.PixelWidth, height / image.PixelHeight)
            : Math.Max(width / image.PixelWidth, height / image.PixelHeight);
        double renderedWidth = image.PixelWidth * scale;
        double renderedHeight = image.PixelHeight * scale;
        drawing.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
        drawing.DrawImage(image, new Rect((width - renderedWidth) / 2, (height - renderedHeight) / 2,
            renderedWidth, renderedHeight));
        drawing.Pop();
    }

    private static void DrawBuiltInBackground(DrawingContext drawing, double width, double height, RgbColor background)
    {
        bool light = background.Red * .2126 + background.Green * .7152 + background.Blue * .0722 > 160;
        Brush foreground = light ? new SolidColorBrush(Color.FromRgb(30, 40, 54)) : new SolidColorBrush(Color.FromRgb(229, 237, 247));
        var outline = new Pen(foreground, width * .003);
        var screen = new Rect(width * .42, height * .26, width * .16, height * .18);
        drawing.DrawRoundedRectangle(null, outline, screen, width * .009, width * .009);
        drawing.DrawLine(outline, new Point(width * .5, height * .44), new Point(width * .5, height * .48));
        drawing.DrawLine(outline, new Point(width * .46, height * .48), new Point(width * .54, height * .48));
        DrawText(drawing, "USB Display", foreground, width * .052, width, height * .56);
    }

    private void DrawDashboard(DrawingContext drawing, RenderRequest request, ThemeContent.Dashboard content,
        double width, double height)
    {
        drawing.DrawRectangle(Brush(content.Background), null, new Rect(0, 0, width, height));
        var labels = new List<string>(4);
        if (content.ShowCpuUsage) { labels.Add(MetricText(request, MetricSource.Host, MetricKind.CpuUsage, "PC CPU")); }
        if (content.ShowMemoryUsage) { labels.Add(MetricText(request, MetricSource.Host, MetricKind.MemoryUsage, "PC 内存")); }
        if (content.ShowHostTemperature) { labels.Add(MetricText(request, MetricSource.Host, MetricKind.Temperature, "PC 温度")); }
        if (content.ShowDeviceTemperature) { labels.Add(MetricText(request, MetricSource.Device, MetricKind.Temperature, "设备温度")); }
        for (int index = 0; index < labels.Count; index++)
        {
            DrawText(drawing, labels[index], Brush(content.Foreground), width * .06, width,
                height * (.2 + index * .15));
        }
    }

    private string MetricText(RenderRequest request, MetricSource source, MetricKind kind, string label)
    {
        var metric = request.Metrics.FirstOrDefault(item => item.Source == source && item.Kind == kind);
        if (metric is null) { return $"{label}：不可用"; }
        metric = freshness.Evaluate(metric);
        if (metric.Value is not double value || !double.IsFinite(value)) { return $"{label}：不可用"; }
        string text = $"{label}：{value:0.0}{metric.Unit}";
        return metric.Availability == MetricAvailability.Stale ? text + "（数据已过期）" : text;
    }

    private static double DrawText(DrawingContext drawing, string value, Brush brush, double fontSize, double width, double top,
        bool fitToWidth = false)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), fontSize, brush, 1);
        double availableWidth = width * .92;
        if (fitToWidth)
        {
            double naturalWidth = text.WidthIncludingTrailingWhitespace;
            if (naturalWidth > availableWidth)
            {
                text.SetFontSize(fontSize * availableWidth / naturalWidth);
            }
            text.MaxLineCount = 1;
        }
        text.MaxTextWidth = availableWidth;
        text.TextAlignment = TextAlignment.Center;
        text.Trimming = TextTrimming.CharacterEllipsis;
        drawing.DrawText(text, new Point(width * .04, top));
        return text.Height;
    }
}
