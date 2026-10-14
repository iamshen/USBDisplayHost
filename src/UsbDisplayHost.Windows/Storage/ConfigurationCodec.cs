using System.Text.Json;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Sessions;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Windows.Storage;

internal static class ConfigurationCodec
{
    public static ProfileData Encode(DeviceProfile profile) => new(profile.DeviceKey.Value,
        Encode(profile.Content), new SettingsData(profile.Settings.Rotation,
            profile.Settings.FramesPerSecond, profile.Settings.JpegQuality),
        profile.ConfirmedBrightness, profile.PowerIntent);

    public static DraftData Encode(ThemeDraft draft) => new(draft.DeviceKey?.Value, draft.Revision, Encode(draft.Content));
    public static DeviceSettingsDraftData Encode(DeviceSettingsDraft draft) => new("device-settings",
        draft.DeviceKey?.Value, draft.Revision, new(draft.Settings.Rotation, draft.Settings.FramesPerSecond, draft.Settings.JpegQuality));
    public static OperationResult<DeviceSettingsDraft> DecodeDeviceSettingsDraft(JsonElement payload, DeviceKey? expectedKey)
    {
        var data = payload.Deserialize<DeviceSettingsDraftData>(AtomicJsonStorage.Options);
        if (data is null || data.Kind != "device-settings" || data.DeviceKey != expectedKey?.Value
            || data.Revision < 0 || data.Settings is null) { return Invalid<DeviceSettingsDraft>(); }
        return DeviceSettings.Create(data.Settings.Rotation, data.Settings.FramesPerSecond, data.Settings.JpegQuality)
            is OperationResult<DeviceSettings>.Success settings
            ? new OperationResult<DeviceSettingsDraft>.Success(new(expectedKey, data.Revision, settings.Value))
            : Invalid<DeviceSettingsDraft>();
    }

    public static OperationResult<DeviceProfile> DecodeProfile(JsonElement payload, DeviceKey expectedKey)
    {
        var data = payload.Deserialize<ProfileData>(AtomicJsonStorage.Options);
        if (data is null || data.DeviceKey != expectedKey.Value || data.Theme is null || data.Settings is null
            || data.ConfirmedBrightness is < 0 or > 100 || !Enum.IsDefined(data.PowerIntent))
        {
            return Invalid<DeviceProfile>();
        }

        var theme = DecodeTheme(data.Theme);
        var settings = DeviceSettings.Create(data.Settings.Rotation, data.Settings.FramesPerSecond, data.Settings.JpegQuality);
        if (theme is not OperationResult<ThemeContent>.Success content
            || settings is not OperationResult<DeviceSettings>.Success validSettings)
        {
            return Invalid<DeviceProfile>();
        }

        return new OperationResult<DeviceProfile>.Success(new DeviceProfile(expectedKey,
            content.Value, validSettings.Value, data.ConfirmedBrightness, data.PowerIntent));
    }

    public static OperationResult<ThemeDraft> DecodeDraft(JsonElement payload, DeviceKey? expectedKey)
    {
        var data = payload.Deserialize<DraftData>(AtomicJsonStorage.Options);
        if (data is null || data.DeviceKey != expectedKey?.Value || data.Revision < 0 || data.Theme is null)
        {
            return Invalid<ThemeDraft>();
        }

        return DecodeTheme(data.Theme) is OperationResult<ThemeContent>.Success content
            ? new OperationResult<ThemeDraft>.Success(new ThemeDraft(expectedKey, data.Revision, content.Value))
            : Invalid<ThemeDraft>();
    }

    public static OperationResult<ApplicationSettings> DecodeApplicationSettings(JsonElement payload)
    {
        // 导航偏好独立降级，不能因未知布局丢弃其他有效设置；高版本保护由存储层处理。
        var fields = payload.Deserialize<Dictionary<string, JsonElement>>(AtomicJsonStorage.Options);
        if (fields is null) { return Invalid<ApplicationSettings>(); }
        NavigationLayout layout = NavigationLayout.Top;
        if (fields.Remove("navigationLayout", out var value))
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                layout = value.GetString() == "Side" ? NavigationLayout.Side : NavigationLayout.Top;
            }
        }
        var data = JsonSerializer.SerializeToElement(fields, AtomicJsonStorage.Options)
            .Deserialize<ApplicationSettings>(AtomicJsonStorage.Options);
        if (data is not null) { data = data with { NavigationLayout = layout }; }
        return data is null ? Invalid<ApplicationSettings>() : new OperationResult<ApplicationSettings>.Success(data);
    }

    public static OperationResult<Unit> Validate(DeviceProfile profile)
    {
        if (profile.ConfirmedBrightness is < 0 or > 100 || !Enum.IsDefined(profile.PowerIntent))
        {
            return Invalid<Unit>();
        }
        return ThemeValidation.Validate(profile.Content);
    }

    private static ThemeData Encode(ThemeContent content) => content switch
    {
        ThemeContent.Image image => new("image", image.AssetId, image.Fit, image.Background,
            default, true, true, true, true, false, false),
        ThemeContent.Clock clock => new("clock", null, ImageFit.Contain, clock.Background,
            clock.Foreground, clock.Use24HourClock, clock.ShowDate, true, true, false, false),
        ThemeContent.Dashboard dashboard => new("dashboard", null, ImageFit.Contain, dashboard.Background,
            dashboard.Foreground, true, true, dashboard.ShowCpuUsage, dashboard.ShowMemoryUsage,
            dashboard.ShowHostTemperature, dashboard.ShowDeviceTemperature),
        _ => throw new InvalidDataException("Unknown theme type.")
    };

    private static OperationResult<ThemeContent> DecodeTheme(ThemeData data)
    {
        ThemeContent? content = data.Kind switch
        {
            "image" => new ThemeContent.Image(data.AssetId, data.Fit, data.Background),
            "clock" => new ThemeContent.Clock(data.Use24HourClock, data.ShowDate, data.Background, data.Foreground),
            "dashboard" => new ThemeContent.Dashboard(data.ShowCpuUsage, data.ShowMemoryUsage,
                data.ShowHostTemperature, data.ShowDeviceTemperature, data.Background, data.Foreground),
            _ => null
        };
        return content is not null && ThemeValidation.Validate(content) is OperationResult<Unit>.Success
            ? new OperationResult<ThemeContent>.Success(content)
            : Invalid<ThemeContent>();
    }

    private static OperationResult<T> Invalid<T>() => new OperationResult<T>.Failure(AppError.Create(
        "InvalidConfiguration", "配置内容或设备归属无效，未应用该文件", "请使用可用备份或检查配置文件"));
}
