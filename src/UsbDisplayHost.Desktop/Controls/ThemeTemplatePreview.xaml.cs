using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UsbDisplayHost.Desktop.ViewModels;

namespace UsbDisplayHost.Desktop.Controls;

public partial class ThemeTemplatePreview : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(ThemeKind), typeof(ThemeTemplatePreview), new PropertyMetadata(ThemeKind.Image));
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(ThemeTemplatePreview));
    public ThemeTemplatePreview() => InitializeComponent();
    public ThemeKind Value
    {
        get => (ThemeKind)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }
}
