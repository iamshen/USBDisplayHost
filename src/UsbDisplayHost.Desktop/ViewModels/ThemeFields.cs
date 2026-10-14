using System.Globalization;
using UsbDisplayHost.Core.Themes;
using Wpf.Ui.Controls;

namespace UsbDisplayHost.Desktop.ViewModels;

public enum ThemeKind { Image, Clock, Dashboard }
public sealed record ThemeOption(string Name, ThemeKind Value, SymbolRegular Icon);
public sealed record FitOption(string Name, ImageFit Value);

internal static class ThemeFields
{
    public static bool TryColor(string input, out RgbColor color)
    {
        color = default;
        if (input.Length != 7 || input[0] != '#'
            || !byte.TryParse(input.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte red)
            || !byte.TryParse(input.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte green)
            || !byte.TryParse(input.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte blue)) { return false; }
        color = new RgbColor(red, green, blue);
        return true;
    }

    public static string ColorText(RgbColor color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
}
