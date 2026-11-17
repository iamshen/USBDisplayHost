using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        Bindings.Update();
    }

    private bool CanEditStartup(bool canEdit) => canEdit && !uiOnly;

    private Visibility ModeRequirementVisibility(bool canEdit, bool canEditStartupMode, bool canEditExitMode) =>
        canEdit && (!canEditStartupMode || !canEditExitMode) ? Visibility.Visible : Visibility.Collapsed;

    private Visibility ReloadVisibility(string error, bool hasUnconfirmedSave) =>
        !string.IsNullOrEmpty(error) && !hasUnconfirmedSave ? Visibility.Visible : Visibility.Collapsed;

    private void OnSettingsLayoutSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool stacked = args.NewSize.Width < TwoColumnMinimumWidth;
        SettingsLayout.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        SettingsLayout.ColumnSpacing = stacked ? 0 : 40;
        SettingsLayout.RowSpacing = stacked ? 24 : 0;
        Grid.SetColumn(UpdateSettings, stacked ? 0 : 1);
        Grid.SetRow(UpdateSettings, stacked ? 1 : 0);
    }
}
