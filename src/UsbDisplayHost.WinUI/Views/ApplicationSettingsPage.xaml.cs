using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Desktop.ViewModels;

namespace UsbDisplayHost.WinUI.Views;

public sealed partial class ApplicationSettingsPage : Page
{
    private const double TwoColumnMinimumWidth = 760;
    private ApplicationSettingsPageViewModel? ViewModel { get; set; }
    private bool uiOnly;

    public ApplicationSettingsPage()
    {
        InitializeComponent();
    }

    internal void InitializeView(ApplicationSettingsPageViewModel viewModel, bool uiOnly)
    {
        this.uiOnly = uiOnly;
        ViewModel = viewModel;
        DataContext = viewModel;
        DeepBlueButton.Content = AppearanceSample("深蓝", ApplicationAppearance.DeepBlue);
        WarmBrownButton.Content = AppearanceSample("暖棕", ApplicationAppearance.WarmBrown);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ApplicationSettingsPageViewModel.Appearance)) { RefreshAppearance(); }
        };
        RefreshAppearance();
        Bindings.Update();
    }

    private void OnAppearanceClick(object sender, RoutedEventArgs args)
    {
        if (ViewModel is not { CanEdit: true } model) { RefreshAppearance(); return; }
        model.Appearance = ReferenceEquals(sender, WarmBrownButton) ? ApplicationAppearance.WarmBrown : ApplicationAppearance.DeepBlue;
        RefreshAppearance();
    }

    private void RefreshAppearance()
    {
        bool brown = ViewModel?.Appearance == ApplicationAppearance.WarmBrown;
        DeepBlueButton.IsChecked = !brown;
        WarmBrownButton.IsChecked = brown;
        SelectSample(DeepBlueButton, !brown);
        SelectSample(WarmBrownButton, brown);
    }

    private static void SelectSample(ToggleButton button, bool selected)
    {
        if (button.Content is not StackPanel panel || panel.Children[0] is not Border sample
            || sample.Child is not Grid shape || shape.Children[2] is not Border check) { return; }
        sample.BorderThickness = new Thickness(selected ? 2 : 1);
        sample.BorderBrush = WorkspacePalette.GetBrush(selected ? "WorkspaceActionBrush" : "WorkspaceBorderStrongBrush");
        check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    }

    private static StackPanel AppearanceSample(string label, ApplicationAppearance appearance)
    {
        var gradient = new LinearGradientBrush { StartPoint = new(0.5, 0), EndPoint = new(0.5, 1) };
        gradient.GradientStops.Add(new GradientStop { Offset = 0, Color = WorkspacePalette.GetColor("Window/Top", appearance) });
        gradient.GradientStops.Add(new GradientStop { Offset = 1, Color = WorkspacePalette.GetColor("Window/Bottom", appearance) });
        var nav = new Border
        {
            Width = 74, Height = 13, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 11, 0, 0),
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(WorkspacePalette.GetColor("Surface/Selected", appearance)),
            Child = new Border { Width = 23, Height = 9, CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(4, 0, 0, 0), Background = new SolidColorBrush(WorkspacePalette.GetColor("Action/Primary", appearance)) }
        };
        var monitor = new Border
        {
            Width = 36, Height = 21, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 20, 0, 0),
            Background = new SolidColorBrush(WorkspacePalette.GetColor("Preview/Background", appearance)),
            BorderBrush = new SolidColorBrush(WorkspacePalette.GetColor("Border/Strong", appearance))
        };
        var check = new Border
        {
            Width = 17, Height = 17, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 7, 6),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Background = WorkspacePalette.GetBrush("WorkspaceActionBrush"),
            Child = new FontIcon { Glyph = "\uE73E", FontSize = 10, Foreground = WorkspacePalette.GetBrush("WorkspaceOnActionBrush") }
        };
        return new StackPanel
        {
            Spacing = 5,
            Children =
            {
                new Border { Width = 128, Height = 72, CornerRadius = new CornerRadius(8), Background = gradient,
                    Child = new Grid { Children = { nav, monitor, check } } },
                new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 }
            }
        };
    }

    private bool CanEditStartup(bool canEdit) => canEdit && !uiOnly;
    private bool UpdateOptInEnabled(bool canEdit, bool hasUpdateAction) => canEdit && hasUpdateAction;

    private Visibility ModeRequirementVisibility(bool canEdit, bool canEditStartupMode, bool canEditExitMode) =>
        canEdit && (!canEditStartupMode || !canEditExitMode) ? Visibility.Visible : Visibility.Collapsed;

    private Visibility ReloadVisibility(string error, bool hasUnconfirmedSave) =>
        !string.IsNullOrEmpty(error) && !hasUnconfirmedSave ? Visibility.Visible : Visibility.Collapsed;

    private void OnSettingsLayoutSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool stacked = args.NewSize.Width < TwoColumnMinimumWidth;
        SettingsLayout.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        SettingsLayout.ColumnSpacing = stacked ? 0 : 26;
        SettingsLayout.RowSpacing = stacked ? 24 : 0;
        Grid.SetColumn(UpdateSettings, stacked ? 0 : 1);
        Grid.SetRow(UpdateSettings, stacked ? 1 : 0);
        UpdateSettings.BorderThickness = stacked ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        UpdateSettings.Padding = stacked ? new Thickness(0, 18, 0, 0) : new Thickness(39, 0, 0, 0);
        UpdateSettings.MinHeight = stacked ? 0 : 464;
    }
}
