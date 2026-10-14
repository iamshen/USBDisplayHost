using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Windows.Storage;

internal sealed record SettingsData(DisplayRotation Rotation, int FramesPerSecond, int JpegQuality);
internal sealed record ThemeData(
    string Kind, string? AssetId, ImageFit Fit, RgbColor Background, RgbColor Foreground,
    bool Use24HourClock, bool ShowDate, bool ShowCpuUsage, bool ShowMemoryUsage,
    bool ShowHostTemperature, bool ShowDeviceTemperature);
internal sealed record ProfileData(
    string DeviceKey, ThemeData Theme, SettingsData Settings, int? ConfirmedBrightness, PowerState PowerIntent);
internal sealed record DraftData(string? DeviceKey, long Revision, ThemeData Theme);
internal sealed record DeviceSettingsDraftData(
    [property: System.Text.Json.Serialization.JsonRequired] string Kind,
    [property: System.Text.Json.Serialization.JsonRequired] string? DeviceKey,
    [property: System.Text.Json.Serialization.JsonRequired] long Revision,
    [property: System.Text.Json.Serialization.JsonRequired] SettingsData Settings);
