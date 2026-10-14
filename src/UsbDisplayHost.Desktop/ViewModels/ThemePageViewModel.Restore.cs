using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Desktop.ViewModels;

public sealed partial class ThemePageViewModel
{
    private bool Restore(ThemeDraft value)
    {
        string? nextAsset = (value.Content as ThemeContent.Image)?.AssetId;
        if (!ReplaceEditorAsset(nextAsset)) { return false; }
        restoring = true;
        try
        {
            draft = value;
            revision = value.Revision;
            Fit = ImageFit.Contain;
            ImageDescription = nextAsset is null ? "内置背景" : "已保存图片";
            ForegroundColor = "#FFFFFF";
            Use24HourClock = true;
            ShowDate = true;
            ShowCpuUsage = true;
            ShowMemoryUsage = true;
            ShowHostTemperature = false;
            ShowDeviceTemperature = false;
            switch (value.Content)
            {
                case ThemeContent.Image image:
                    SelectedTemplate = ThemeKind.Image;
                    assetId = image.AssetId;
                    ImageDescription = assetId is null ? "内置背景" : "已保存图片";
                    Fit = image.Fit;
                    BackgroundColor = ThemeFields.ColorText(image.Background);
                    break;
                case ThemeContent.Clock clock:
                    SelectedTemplate = ThemeKind.Clock;
                    Use24HourClock = clock.Use24HourClock;
                    ShowDate = clock.ShowDate;
                    BackgroundColor = ThemeFields.ColorText(clock.Background);
                    ForegroundColor = ThemeFields.ColorText(clock.Foreground);
                    break;
                case ThemeContent.Dashboard dashboard:
                    SelectedTemplate = ThemeKind.Dashboard;
                    ShowCpuUsage = dashboard.ShowCpuUsage;
                    ShowMemoryUsage = dashboard.ShowMemoryUsage;
                    ShowHostTemperature = dashboard.ShowHostTemperature;
                    ShowDeviceTemperature = dashboard.ShowDeviceTemperature;
                    BackgroundColor = ThemeFields.ColorText(dashboard.Background);
                    ForegroundColor = ThemeFields.ColorText(dashboard.Foreground);
                    break;
            }
            OnPropertyChanged(nameof(IsDirty));
        }
        finally { restoring = false; }
        return true;
    }
}
