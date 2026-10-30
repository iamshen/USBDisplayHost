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
    private static readonly Lazy<BitmapSource> BuiltInBackground = new(LoadBuiltInBackground);
    private readonly MetricFreshness freshness = new(timeProvider);

    public void Paint(DrawingContext drawing, RenderRequest request, double width, double height, BitmapSource? image)
    {
        switch (request.Content)
        {
            case ThemeContent.Image content:
                drawing.DrawRectangle(Brush(content.Background), null, new Rect(0, 0, width, height));
                DrawImage(drawing, image ?? BuiltInBackground.Value, content.Fit, width, height);
                break;
            case ThemeContent.Clock content:
                DrawClock(drawing, request, content, width, height);
                break;
            case ThemeContent.Dashboard content:
                DrawDashboard(drawing, request, content, width, height);
                break;
            default:
                throw new InvalidDataException("Unsupported theme type.");
        }
    }

    private static SolidColorBrush Brush(RgbColor color) => new(Color.FromRgb(color.Red, color.Green, color.Blue));

    private static SolidColorBrush MixBrush(RgbColor background, RgbColor foreground, double amount) => new(Color.FromRgb(
        (byte)Math.Round(background.Red + (foreground.Red - background.Red) * amount),
        (byte)Math.Round(background.Green + (foreground.Green - background.Green) * amount),
        (byte)Math.Round(background.Blue + (foreground.Blue - background.Blue) * amount)));

    private static void DrawClock(DrawingContext drawing, RenderRequest request, ThemeContent.Clock content,
        double width, double height)
    {
        drawing.DrawRectangle(Brush(content.Background), null, new Rect(0, 0, width, height));
        var foreground = Brush(content.Foreground);
        var secondary = MixBrush(content.Background, content.Foreground, .7);
        double fontSize = Math.Min(width * .21, height * .32);
        var hours = CreateText(request.LocalTime.ToString(content.Use24HourClock ? "HH:mm" : "hh:mm", CultureInfo.CurrentCulture),
            foreground, fontSize, FontWeights.Bold);
        var seconds = CreateText(request.LocalTime.ToString(":ss", CultureInfo.CurrentCulture), secondary, fontSize * .26, FontWeights.SemiBold);
        FormattedText? period = content.Use24HourClock ? null
            : CreateText(request.LocalTime.ToString("tt", CultureInfo.CurrentCulture), secondary, fontSize * .18);
        double gap = fontSize * .08;
        double trailingWidth = Math.Max(seconds.WidthIncludingTrailingWhitespace, period?.WidthIncludingTrailingWhitespace ?? 0);
        double timeWidth = hours.WidthIncludingTrailingWhitespace + gap + trailingWidth;
        double scale = Math.Min(1, width * .84 / timeWidth);
        if (scale < 1)
        {
            hours.SetFontSize(fontSize * scale);
            seconds.SetFontSize(fontSize * .26 * scale);
            period?.SetFontSize(fontSize * .18 * scale);
            gap *= scale;
            trailingWidth = Math.Max(seconds.WidthIncludingTrailingWhitespace, period?.WidthIncludingTrailingWhitespace ?? 0);
            timeWidth = hours.WidthIncludingTrailingWhitespace + gap + trailingWidth;
        }

        string dateFormat = CultureInfo.CurrentCulture.DateTimeFormat.LongDatePattern;
        if (!dateFormat.Contains("ddd", StringComparison.Ordinal)) { dateFormat += " dddd"; }
        FormattedText? date = content.ShowDate
            ? FittedText(request.LocalTime.ToString(dateFormat, CultureInfo.CurrentCulture), secondary,
                Math.Min(width * .044, height * .072), width * .84, FontWeights.SemiBold)
            : null;
        double dateGap = fontSize * scale * .1;
        double groupHeight = hours.Height + (date is null ? 0 : dateGap + date.Height);
        double top = (height - groupHeight) / 2;
        double left = (width - timeWidth) / 2;
        drawing.DrawText(hours, new Point(left, top));
        double trailingLeft = left + hours.WidthIncludingTrailingWhitespace + gap;
        double secondsTop = top + hours.Baseline - seconds.Baseline;
        drawing.DrawText(seconds, new Point(trailingLeft, secondsTop));
        if (period is not null)
        {
            drawing.DrawText(period, new Point(trailingLeft,
                secondsTop - period.Height - fontSize * scale * .035));
        }
        if (date is not null)
        {
            drawing.DrawText(date, new Point((width - date.WidthIncludingTrailingWhitespace) / 2,
                top + hours.Height + dateGap));
        }
    }

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

    private static BitmapSource LoadBuiltInBackground()
    {
        using var stream = typeof(ScenePainter).Assembly.GetManifestResourceStream("UsbDisplayHost.Rendering.DefaultLandscape.png")
            ?? throw new InvalidDataException("Built-in theme image is missing.");
        var image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        image.Freeze();
        return image;
    }

    private void DrawDashboard(DrawingContext drawing, RenderRequest request, ThemeContent.Dashboard content,
        double width, double height)
    {
        drawing.DrawRectangle(Brush(content.Background), null, new Rect(0, 0, width, height));
        var metrics = new List<DashboardMetric>(4);
        if (content.ShowCpuUsage) { metrics.Add(ReadMetric(request, MetricSource.Host, MetricKind.CpuUsage, "CPU 使用率")); }
        if (content.ShowMemoryUsage) { metrics.Add(ReadMetric(request, MetricSource.Host, MetricKind.MemoryUsage, "内存占用")); }
        if (content.ShowHostTemperature) { metrics.Add(ReadMetric(request, MetricSource.Host, MetricKind.Temperature, "主机温度")); }
        if (content.ShowDeviceTemperature) { metrics.Add(ReadMetric(request, MetricSource.Device, MetricKind.Temperature, "设备温度")); }
        if (metrics.Count == 0) { return; }

        double unit = Math.Min(width, height);
        double margin = unit * .09;
        bool portrait = height > width;
        int columns = metrics.Count == 4 ? 2 : portrait ? 1 : metrics.Count;
        int rows = (metrics.Count + columns - 1) / columns;
        double cellWidth = (width - margin * 2) / columns;
        double cellHeight = (height - margin * 2) / rows;
        var separator = new Pen(MixBrush(content.Background, content.Foreground, .18), unit * .0018);
        for (int column = 1; column < columns; column++)
        {
            double x = margin + column * cellWidth;
            drawing.DrawLine(separator, new Point(x, margin), new Point(x, height - margin));
        }
        for (int row = 1; row < rows; row++)
        {
            double y = margin + row * cellHeight;
            drawing.DrawLine(separator, new Point(margin, y), new Point(width - margin, y));
        }
        for (int index = 0; index < metrics.Count; index++)
        {
            var cell = new Rect(margin + index % columns * cellWidth,
                margin + index / columns * cellHeight, cellWidth, cellHeight);
            DrawMetric(drawing, metrics[index], content, cell, unit);
        }
    }

    private sealed record DashboardMetric(string Label, string Source, string Value, string Unit, bool Unavailable, bool Stale);

    private DashboardMetric ReadMetric(RenderRequest request, MetricSource source, MetricKind kind, string label)
    {
        var metric = request.Metrics.FirstOrDefault(item => item.Source == source && item.Kind == kind);
        string sourceText = source == MetricSource.Host ? "主机" : "设备";
        if (metric is not null) { metric = freshness.Evaluate(metric); }
        if (metric?.Value is not double value || !double.IsFinite(value))
        {
            return new DashboardMetric(label, sourceText, "不可用", string.Empty, true, false);
        }
        return new DashboardMetric(label, sourceText, value.ToString("0.0", CultureInfo.CurrentCulture),
            metric.Unit, false, metric.Availability == MetricAvailability.Stale);
    }

    private static void DrawMetric(DrawingContext drawing, DashboardMetric metric, ThemeContent.Dashboard content,
        Rect cell, double unit)
    {
        var foreground = Brush(content.Foreground);
        var secondary = MixBrush(content.Background, content.Foreground, .7);
        double innerWidth = cell.Width * .84;
        double labelSize = Math.Min(unit * .055, cell.Height * .15);
        var label = FittedText(metric.Label, secondary, labelSize, innerWidth, FontWeights.SemiBold);
        double valueSize = Math.Min(cell.Width * .34, cell.Height * .46);
        if (metric.Unavailable) { valueSize *= .58; }
        var value = CreateText(metric.Value, metric.Unavailable ? secondary : foreground, valueSize, FontWeights.Bold);
        var suffix = CreateText(metric.Unit, foreground, valueSize * .42, FontWeights.SemiBold);
        double valueGap = string.IsNullOrEmpty(metric.Unit) ? 0 : valueSize * .04;
        double combinedWidth = value.WidthIncludingTrailingWhitespace + valueGap + suffix.WidthIncludingTrailingWhitespace;
        double valueScale = Math.Min(1, innerWidth / combinedWidth);
        if (valueScale < 1)
        {
            value.SetFontSize(valueSize * valueScale);
            suffix.SetFontSize(valueSize * .42 * valueScale);
            valueGap *= valueScale;
            combinedWidth = value.WidthIncludingTrailingWhitespace + valueGap + suffix.WidthIncludingTrailingWhitespace;
        }
        string status = metric.Unavailable ? $"{metric.Source} · 暂无有效读数"
            : metric.Stale ? $"{metric.Source} · 数据已过期 · 保留最近读数" : $"来源：{metric.Source}";
        var statusText = FittedText(status, secondary, labelSize * .6, innerWidth);
        double gap = labelSize * .35;
        double groupHeight = label.Height + gap + value.Height + gap + statusText.Height;
        double top = cell.Top + (cell.Height - groupHeight) / 2;
        drawing.DrawText(label, new Point(cell.Left + (cell.Width - label.WidthIncludingTrailingWhitespace) / 2, top));
        double valueLeft = cell.Left + (cell.Width - combinedWidth) / 2;
        double valueTop = top + label.Height + gap;
        drawing.DrawText(value, new Point(valueLeft, valueTop));
        drawing.DrawText(suffix, new Point(valueLeft + value.WidthIncludingTrailingWhitespace + valueGap,
            valueTop + value.Baseline - suffix.Baseline));

        drawing.DrawText(statusText, new Point(cell.Left + (cell.Width - statusText.WidthIncludingTrailingWhitespace) / 2,
            valueTop + value.Height + gap));
    }

    private static FormattedText CreateText(string value, Brush brush, double fontSize, FontWeight? weight = null) =>
        new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal,
                weight ?? FontWeights.Normal, FontStretches.Normal), fontSize, brush, 1);

    private static FormattedText FittedText(string value, Brush brush, double fontSize, double availableWidth,
        FontWeight? weight = null)
    {
        var text = CreateText(value, brush, fontSize, weight);
        if (text.WidthIncludingTrailingWhitespace > availableWidth)
        {
            text.SetFontSize(fontSize * availableWidth / text.WidthIncludingTrailingWhitespace);
        }
        return text;
    }

}
