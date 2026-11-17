using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using UsbDisplayHost.Core.Configuration;
using Windows.UI;

namespace UsbDisplayHost.WinUI;

internal static class WorkspacePalette
{
    private static readonly IReadOnlyDictionary<string, string[]> Colors = LoadColors();
    private static readonly (string Resource, string Token)[] Brushes =
    [
        ("WorkspaceNavigationBrush", "Surface/Nav"),
        ("WorkspaceInsetBrush", "Surface/Inset"),
        ("WorkspaceHoverBrush", "Surface/Hover"),
        ("WorkspaceSelectedBrush", "Surface/Selected"),
        ("WorkspaceModalBrush", "Surface/Modal"),
        ("WorkspaceTextPrimaryBrush", "Text/Primary"),
        ("WorkspaceTextSecondaryBrush", "Text/Secondary"),
        ("WorkspaceTextDisabledBrush", "Text/Disabled"),
        ("WorkspaceDividerBrush", "Border/Quiet"),
        ("WorkspaceBorderStrongBrush", "Border/Strong"),
        ("WorkspaceActionBrush", "Action/Primary"),
        ("WorkspaceOnActionBrush", "Action/OnPrimary"),
        ("WorkspaceLinkBrush", "Action/Link"),
        ("WorkspaceSuccessBrush", "Status/Success"),
        ("WorkspaceWarningBrush", "Status/Warning"),
        ("WorkspaceErrorBrush", "Status/Error"),
        ("WorkspaceFocusBrush", "Focus/Ring"),
        ("WorkspacePreviewBrush", "Preview/Background"),
        ("WorkspacePreviewTextBrush", "Preview/Text"),
        ("TextFillColorPrimaryBrush", "Text/Primary"),
        ("TextFillColorSecondaryBrush", "Text/Secondary"),
        ("TextFillColorDisabledBrush", "Text/Disabled"),
        ("ControlFillColorDefaultBrush", "Surface/Inset"),
        ("ControlFillColorSecondaryBrush", "Surface/Hover"),
        ("ControlFillColorDisabledBrush", "Surface/Inset"),
        ("ControlStrokeColorDefaultBrush", "Border/Quiet"),
        ("ControlStrokeColorSecondaryBrush", "Border/Strong"),
        ("AccentFillColorDefaultBrush", "Action/Primary"),
        ("AccentFillColorSecondaryBrush", "Action/Primary"),
        ("AccentFillColorTertiaryBrush", "Action/Primary"),
        ("AccentFillColorDisabledBrush", "Surface/Hover"),
        ("AccentButtonBackgroundPointerOver", "Action/Primary"),
        ("AccentButtonBackgroundPressed", "Text/Secondary"),
        ("AccentButtonBackgroundDisabled", "Surface/Hover"),
        ("AccentButtonForegroundPointerOver", "Action/OnPrimary"),
        ("AccentButtonForegroundPressed", "Action/OnPrimary"),
        ("AccentButtonForegroundDisabled", "Text/Disabled"),
        ("TextOnAccentFillColorPrimaryBrush", "Action/OnPrimary"),
        ("TextOnAccentFillColorSecondaryBrush", "Action/OnPrimary"),
        ("TextOnAccentFillColorDisabledBrush", "Text/Disabled"),
        ("ContentDialogBackground", "Surface/Modal"),
        ("ContentDialogBorderBrush", "Border/Strong"),
        ("SolidBackgroundFillColorBaseBrush", "Surface/Inset"),
        ("SystemFillColorCriticalBrush", "Status/Error"),
        ("SystemFillColorCautionBrush", "Status/Warning"),
        ("SystemFillColorSuccessBrush", "Status/Success"),
        ("FocusStrokeColorOuterBrush", "Focus/Ring"),
        ("ToggleButtonForegroundChecked", "Text/Primary"),
        ("ToggleButtonForegroundCheckedPointerOver", "Text/Primary"),
        ("ToggleButtonForegroundCheckedPressed", "Text/Primary"),
        ("ToggleButtonBackgroundChecked", "Surface/Selected"),
        ("ToggleButtonBackgroundCheckedPointerOver", "Surface/Selected"),
        ("ToggleButtonBackgroundCheckedPressed", "Surface/Hover"),
        ("ListViewItemBackgroundSelected", "Surface/Selected"),
        ("ListViewItemBackgroundSelectedPointerOver", "Surface/Selected"),
        ("ListViewItemBackgroundPointerOver", "Surface/Hover"),
    ];

    public static void Apply(ApplicationAppearance appearance)
    {
        var colors = GetDictionary("Default");
        var background = (LinearGradientBrush)colors["WorkspaceCanvasBrush"];
        background.GradientStops[0].Color = GetColor("Window/Top", appearance);
        background.GradientStops[1].Color = GetColor("Window/Bottom", appearance);
        foreach (var (resource, token) in Brushes)
        {
            ((SolidColorBrush)colors[resource]).Color = GetColor(token, appearance);
        }
    }

    public static Brush GetBrush(string key)
    {
        var accessibility = new global::Windows.UI.ViewManagement.AccessibilitySettings();
        return (Brush)GetDictionary(accessibility.HighContrast ? "HighContrast" : "Default")[key];
    }

    private static ResourceDictionary GetDictionary(string theme) =>
        Application.Current.Resources.MergedDictionaries.Last().ThemeDictionaries[theme] as ResourceDictionary
        ?? throw new InvalidOperationException("工作区配色资源缺失");

    public static Color GetColor(string token, ApplicationAppearance appearance)
    {
        string value = Colors[token][appearance == ApplicationAppearance.WarmBrown ? 1 : 0];
        uint rgb = Convert.ToUInt32(value[1..], 16);
        return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private static IReadOnlyDictionary<string, string[]> LoadColors()
    {
        using var stream = typeof(WorkspacePalette).Assembly.GetManifestResourceStream("UsbDisplayHost.WinUI.WorkspaceTokens.json")
            ?? throw new InvalidOperationException("工作区配色源缺失");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("colors").Deserialize<Dictionary<string, string[]>>()!;
    }
}
