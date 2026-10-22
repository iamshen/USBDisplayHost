using System.Windows;
using System.Windows.Controls;
using UsbDisplayHost.Core.Sessions;

namespace UsbDisplayHost.Desktop.Controls;

public partial class DeviceStatus : UserControl
{
    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(nameof(Snapshot),
        typeof(DeviceSnapshot), typeof(DeviceStatus), new PropertyMetadata(DeviceSnapshot.Disconnected, OnSnapshotChanged));
    private static readonly DependencyPropertyKey ConnectionTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ConnectionText), typeof(string), typeof(DeviceStatus), new PropertyMetadata("未连接"));
    private static readonly DependencyPropertyKey ModeTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ModeText), typeof(string), typeof(DeviceStatus), new PropertyMetadata("未知"));
    private static readonly DependencyPropertyKey PowerTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(PowerText), typeof(string), typeof(DeviceStatus), new PropertyMetadata("未知"));
    private static readonly DependencyPropertyKey StreamingTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(StreamingText), typeof(string), typeof(DeviceStatus), new PropertyMetadata("已停止"));
    public static readonly DependencyProperty ConnectionTextProperty = ConnectionTextPropertyKey.DependencyProperty;
    public static readonly DependencyProperty ModeTextProperty = ModeTextPropertyKey.DependencyProperty;
    public static readonly DependencyProperty PowerTextProperty = PowerTextPropertyKey.DependencyProperty;
    public static readonly DependencyProperty StreamingTextProperty = StreamingTextPropertyKey.DependencyProperty;
    public DeviceStatus() => InitializeComponent();
    public DeviceSnapshot Snapshot { get => (DeviceSnapshot)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
    public string ConnectionText => (string)GetValue(ConnectionTextProperty);
    public string ModeText => (string)GetValue(ModeTextProperty);
    public string PowerText => (string)GetValue(PowerTextProperty);
    public string StreamingText => (string)GetValue(StreamingTextProperty);
    private static void OnSnapshotChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (DeviceStatus)sender;
        var value = args.NewValue as DeviceSnapshot ?? DeviceSnapshot.Disconnected;
        control.SetValue(ConnectionTextPropertyKey, value.Connection switch
        {
            ConnectionState.Disconnected => "未连接", ConnectionState.Discovered => "已发现", ConnectionState.Opening => "连接中",
            ConnectionState.Ready => "已就绪", ConnectionState.Recovering => "恢复中", ConnectionState.Faulted => "故障", _ => "未知"
        });
        control.SetValue(ModeTextPropertyKey, value.Mode switch { DeviceMode.Theme => "主题", DeviceMode.SecondaryDisplay => "副屏", _ => "未知" });
        control.SetValue(PowerTextPropertyKey, value.Power switch { PowerState.On => "已亮屏", PowerState.Off => "已熄屏", _ => "未知" });
        control.SetValue(StreamingTextPropertyKey, value.Streaming switch
        { StreamingState.Running => "发送中", StreamingState.Paused => "已暂停", StreamingState.Faulted => "故障", StreamingState.Stopped => "已停止", _ => "未知" });
    }
}
