using System.Windows;
using System.Windows.Controls;

namespace UsbDisplayHost.Desktop.Controls;

public partial class ColorPalette : UserControl
{
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor), typeof(string), typeof(ColorPalette),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnColorChanged));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ColorPalette), new PropertyMetadata("颜色色板"));
    private bool updating;

    public ColorPalette()
    {
        InitializeComponent();
        Palette.ItemsSource = new ColorOption[]
        {
            new("黑色", "#000000", "#FFFFFF"), new("白色", "#FFFFFF", "#000000"),
            new("深蓝", "#152035", "#FFFFFF"), new("蓝色", "#2563EB", "#FFFFFF"),
            new("绿色", "#16A34A", "#000000"), new("橙色", "#F59E0B", "#000000")
        };
    }

    public string SelectedColor
    {
        get => (string)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    private static void OnColorChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        var palette = (ColorPalette)owner;
        palette.updating = true;
        try { palette.Palette.SelectedValue = (e.NewValue as string)?.ToUpperInvariant(); }
        finally { palette.updating = false; }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 自定义值或校验中的输入取消色板选择，不覆盖原文本；只有明确选中色板才写回。
        if (!updating && e.AddedItems.Count > 0 && e.AddedItems[0] is ColorOption option)
        { SetCurrentValue(SelectedColorProperty, option.Value); }
    }

    private sealed record ColorOption(string Name, string Value, string CheckColor);
}
