using System.Windows;
using System.Windows.Controls;

namespace UsbDisplayHost.Desktop.Controls;

public partial class InlineMessage : UserControl
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(InlineMessage), new PropertyMetadata(string.Empty));

    public InlineMessage()
    {
        InitializeComponent();
        SetBinding(TagProperty, new System.Windows.Data.Binding(nameof(Message)) { Source = this });
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }
}
