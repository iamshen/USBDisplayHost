using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using UsbDisplayHost.Desktop.ViewModels;

namespace UsbDisplayHost.Desktop.Views;

public sealed class ThemeColorBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // 输入中的非法 HEX 保留原文与现有校验，仅将示意色块留空。
        if (value is not string text || !ThemeFields.TryColor(text, out var color)) { return Brushes.Transparent; }
        var brush = new SolidColorBrush(Color.FromRgb(color.Red, color.Green, color.Blue));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
