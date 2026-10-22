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
        double fontSize = Math.Min(width * .21, height * .36);
        var hours = CreateText(request.LocalTime.ToString(content.Use24HourClock ? "HH:mm" : "hh:mm", CultureInfo.CurrentCulture),
            foreground, fontSize, FontWeights.Light);
        var seconds = CreateText(request.LocalTime.ToString(":ss", CultureInfo.CurrentCulture), secondary, fontSize * .34, FontWeights.Light);
        FormattedText? period = content.Use24HourClock ? null
            : CreateText(request.LocalTime.ToString("tt", CultureInfo.CurrentCulture), secondary, fontSize * .18);
        double gap = fontSize * .08;
        double trailingWidth = Math.Max(seconds.WidthIncludingTrailingWhitespace, period?.WidthIncludingTrailingWhitespace ?? 0);
        double timeWidth = hours.WidthIncludingTrailingWhitespace + gap + trailingWidth;
        double scale = Math.Min(1, width * .84 / timeWidth);
        if (scale < 1)
        {
            hours.SetFontSize(fontSize * scale);
            seconds.SetFontSize(fontSize * .34 * scale);
            period?.SetFontSize(fontSize * .18 * scale);
            gap *= scale;
            trailingWidth = Math.Max(seconds.WidthIncludingTrailingWhitespace, period?.WidthIncludingTrailingWhitespace ?? 0);
            timeWidth = hours.WidthIncludingTrailingWhitespace + gap + trailingWidth;
        }

        FormattedText? date = content.ShowDate
            ? FittedText(request.LocalTime.ToString("D", CultureInfo.CurrentCulture), secondary,
                Math.Min(width * .034, height * .055), width * .84)
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
        var metrics = new List<DashboardMetric>(4);
        if (content.ShowCpuUsage) { metrics.Add(ReadMetric(request, MetricSource.Host, MetricKind.CpuUsage, "CPU 使用率")); }
        if (content.ShowMemoryUsage) { metrics.Add(ReadMetric(request, MetricSource.Host, MetricKind.MemoryUsage, "内存占用")); }
        if (content.ShowHostTemperature) { metrics.Add(ReadMetric(request, MetricSource.Host, MetricKind.Temperature, "主机温度")); }
        if (content.ShowDeviceTemperature) { metrics.Add(ReadMetric(request, MetricSource.Device, MetricKind.Temperature, "设备温度")); }
        if (metrics.Count == 0) { return; }

        double unit = Math.Min(width, height);
        double margin = unit * .065;
        double gap = unit * .024;
        double headingHeight = unit * .09;
        var foreground = Brush(content.Foreground);
        var secondary = MixBrush(content.Background, content.Foreground, .66);
        var heading = CreateText("系统监测", foreground, unit * .038, FontWeights.SemiBold);
        drawing.DrawText(heading, new Point(margin, margin));
        var count = CreateText($"{metrics.Count} 项指标", secondary, unit * .026);
        drawing.DrawText(count, new Point(width - margin - count.WidthIncludingTrailingWhitespace,
            margin + heading.Baseline - count.Baseline));

        bool portrait = height > width;
        int columns = metrics.Count == 4 ? 2 : portrait ? 1 : metrics.Count;
        int rows = (metrics.Count + columns - 1) / columns;
        double cardWidth = (width - margin * 2 - gap * (columns - 1)) / columns;
        double cardHeight = (height - margin * 2 - headingHeight - gap * (rows - 1)) / rows;
        for (int index = 0; index < metrics.Count; index++)
        {
            var card = new Rect(margin + index % columns * (cardWidth + gap),
                margin + headingHeight + index / columns * (cardHeight + gap), cardWidth, cardHeight);
            DrawMetricCard(drawing, metrics[index], content, card, unit);
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

    private static void DrawMetricCard(DrawingContext drawing, DashboardMetric metric, ThemeContent.Dashboard content,
        Rect card, double unit)
    {
        var foreground = Brush(content.Foreground);
        var secondary = MixBrush(content.Background, content.Foreground, .66);
        var panel = MixBrush(content.Background, content.Foreground, .065);
        var outline = new Pen(MixBrush(content.Background, content.Foreground, .16), unit * .0012);
        double radius = unit * .022;
        drawing.DrawRoundedRectangle(panel, outline, card, radius, radius);

        double padding = Math.Min(card.Width * .075, unit * .044);
        double innerWidth = card.Width - padding * 2;
        double labelSize = Math.Min(unit * .034, card.Height * .11);
        var source = CreateText(metric.Source, secondary, labelSize * .75);
        double badgePadding = labelSize * .35;
        var sourceBadge = new Rect(card.Right - padding - source.WidthIncludingTrailingWhitespace - badgePadding * 2,
            card.Top + padding, source.WidthIncludingTrailingWhitespace + badgePadding * 2, source.Height + badgePadding);
        drawing.DrawRoundedRectangle(MixBrush(content.Background, content.Foreground, .1), null,
            sourceBadge, radius * .35, radius * .35);
        drawing.DrawText(source, new Point(sourceBadge.Left + badgePadding, sourceBadge.Top + badgePadding / 2));
        var label = FittedText(metric.Label, foreground, labelSize,
            innerWidth - sourceBadge.Width - labelSize * .5, FontWeights.SemiBold);
        drawing.DrawText(label, new Point(card.Left + padding,
            sourceBadge.Top + (sourceBadge.Height - label.Height) / 2));

        double valueSize = Math.Min(card.Width * .28, card.Height * .34);
        if (metric.Unavailable) { valueSize *= .58; }
        var value = CreateText(metric.Value, metric.Unavailable ? secondary : foreground, valueSize, FontWeights.Light);
        var suffix = CreateText(metric.Unit, secondary, valueSize * .32);
        double valueGap = string.IsNullOrEmpty(metric.Unit) ? 0 : valueSize * .08;
        double combinedWidth = value.WidthIncludingTrailingWhitespace + valueGap + suffix.WidthIncludingTrailingWhitespace;
        double valueScale = Math.Min(1, innerWidth / combinedWidth);
        if (valueScale < 1)
        {
            value.SetFontSize(valueSize * valueScale);
            suffix.SetFontSize(valueSize * .32 * valueScale);
            valueGap *= valueScale;
            combinedWidth = value.WidthIncludingTrailingWhitespace + valueGap + suffix.WidthIncludingTrailingWhitespace;
        }
        double valueLeft = card.Left + (card.Width - combinedWidth) / 2;
        double valueTop = card.Top + (card.Height - value.Height) / 2;
        drawing.DrawText(value, new Point(valueLeft, valueTop));
        drawing.DrawText(suffix, new Point(valueLeft + value.WidthIncludingTrailingWhitespace + valueGap,
            valueTop + value.Baseline - suffix.Baseline));

        string status = metric.Unavailable ? "暂无有效读数" : metric.Stale ? "数据已过期 · 保留最近读数" : "当前读数";
        var statusText = FittedText(status, secondary, labelSize * .75, innerWidth);
        double statusTop = card.Bottom - padding - statusText.Height;
        if (metric.Stale)
        {
            drawing.DrawLine(new Pen(secondary, unit * .0015), new Point(card.Left + padding, statusTop - labelSize * .4),
                new Point(card.Left + padding + labelSize, statusTop - labelSize * .4));
        }
        drawing.DrawText(statusText, new Point(card.Left + padding, statusTop));
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

    private static double DrawText(DrawingContext drawing, string value, Brush brush, double fontSize, double width, double top)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), fontSize, brush, 1);
        double availableWidth = width * .92;
        text.MaxTextWidth = availableWidth;
        text.TextAlignment = TextAlignment.Center;
        text.Trimming = TextTrimming.CharacterEllipsis;
        drawing.DrawText(text, new Point(width * .04, top));
        return text.Height;
    }
}
