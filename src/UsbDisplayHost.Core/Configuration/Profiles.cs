using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Core.Configuration;

public sealed record ThemeDraft(DeviceKey? DeviceKey, long Revision, ThemeContent Content);
public sealed record ActiveProfile(OperationContext Context, ThemeContent Content, DeviceSettings Settings, long PipelineRevision);
public sealed record DeviceProfile(
    DeviceKey DeviceKey, ThemeContent Content, DeviceSettings Settings, int? ConfirmedBrightness, PowerState PowerIntent);

public sealed record ApplicationSettings(
    bool StartWithWindows, bool SwitchToThemeOnStartup, bool SwitchToSecondaryOnExit,
    bool AutomaticallyCheckUpdates, bool HasSeenTrayNotice, bool CloseToTray = true,
    bool SuppressModeSwitchNotice = false,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<NavigationLayout>))]
    NavigationLayout NavigationLayout = NavigationLayout.Top)
{
    public static ApplicationSettings Default { get; } = new(false, false, false, true, false);
}

public enum NavigationLayout { Top, Side }
