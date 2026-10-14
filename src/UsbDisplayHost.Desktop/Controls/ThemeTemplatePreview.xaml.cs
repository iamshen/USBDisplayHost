using System.Windows;
using System.Windows.Controls;
using UsbDisplayHost.Desktop.ViewModels;

namespace UsbDisplayHost.Desktop.Controls;

public partial class ThemeTemplatePreview : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(ThemeKind), typeof(ThemeTemplatePreview), new PropertyMetadata(ThemeKind.Image));
    public ThemeTemplatePreview() => InitializeComponent();
    public ThemeKind Value
    {
        get => (ThemeKind)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
}
