using System.ComponentModel;
using System.Windows;
using UsbDisplayHost.Core.Diagnostics;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace UsbDisplayHost.Desktop.Services;

public sealed class DesktopThemeService : IDisposable
{
    private readonly Application application;
    private readonly IErrorReporter errorReporter;
    private readonly Action<string> showError;
    private ThemesDictionary themes;
    private ResourceDictionary overrides;
    private ApplicationTheme currentTheme;
    private volatile bool disposed;
    private int refreshPending;

    public DesktopThemeService(Application application, IErrorReporter errorReporter, Action<string> showError)
    {
        this.application = application;
        this.errorReporter = errorReporter;
        this.showError = showError;
        currentTheme = SelectTheme();
        themes = new ThemesDictionary { Source = SelectLibrarySource(currentTheme) };
        application.Resources.MergedDictionaries.Add(themes);
        application.Resources.MergedDictionaries.Add(new ControlsDictionary());
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("Resources/SemanticResources.xaml", UriKind.Relative)
        });
        overrides = CreateOverrides(currentTheme);
        application.Resources.MergedDictionaries.Add(overrides);
        SystemParameters.StaticPropertyChanged += OnSystemSettingChanged;
    }

    private static ApplicationTheme SelectTheme() => SystemParameters.HighContrast
        ? ApplicationTheme.HighContrast
        : ApplicationTheme.Dark;

    private static ResourceDictionary CreateOverrides(ApplicationTheme theme) => new()
    {
        Source = new Uri(theme == ApplicationTheme.HighContrast
            ? "Resources/ThemeOverrides.HighContrast.xaml"
            : "Resources/ThemeOverrides.Dark.xaml", UriKind.Relative)
    };

    private static Uri SelectLibrarySource(ApplicationTheme theme)
    {
        string name = "Dark";
        if (theme == ApplicationTheme.HighContrast)
        {
            SystemThemeManager.UpdateSystemThemeCache();
            name = ApplicationThemeManager.GetSystemTheme() switch
            {
                SystemTheme.HC1 => "HC1",
                SystemTheme.HC2 => "HC2",
                SystemTheme.HCBlack => "HCBlack",
                SystemTheme.HCWhite => "HCWhite",
                _ => "HCWhite"
            };
        }
        return new Uri($"pack://application:,,,/Wpf.Ui;component/Resources/Theme/{name}.xaml", UriKind.Absolute);
    }

    private void OnSystemSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (disposed || application.Dispatcher.HasShutdownStarted)
        {
            return;
        }
        if (Interlocked.Exchange(ref refreshPending, 1) != 0) { return; }

        try
        {
            application.Dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref refreshPending, 0);
                if (!disposed)
                {
                    try
                    {
                        var selected = SelectTheme();
                        var librarySource = SelectLibrarySource(selected);
                        if (selected == currentTheme && librarySource == themes.Source) { return; }
                        // 先完整加载库资源和项目覆盖，避免缺失资源破坏现用字典。
                        var libraryReplacement = new ThemesDictionary { Source = librarySource };
                        var replacement = CreateOverrides(selected);
                        int index = application.Resources.MergedDictionaries.IndexOf(overrides);
                        int libraryIndex = application.Resources.MergedDictionaries.IndexOf(themes);
                        if (index < 0 || libraryIndex < 0)
                        { throw new InvalidOperationException("Theme resources are detached."); }
                        application.Resources.MergedDictionaries[libraryIndex] = libraryReplacement;
                        try { application.Resources.MergedDictionaries[index] = replacement; }
                        catch
                        {
                            application.Resources.MergedDictionaries[libraryIndex] = themes;
                            throw;
                        }
                        themes = libraryReplacement;
                        overrides = replacement;
                        currentTheme = selected;
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        var report = errorReporter.Report(exception, "ThemeResourcesChange");
                        showError($"界面主题未完成更新。错误编号：{report.ErrorId}");
                    }
                }
            });
        }
        catch (InvalidOperationException exception)
        {
            Interlocked.Exchange(ref refreshPending, 0);
            errorReporter.Report(exception, "ThemeNotificationDuringShutdown");
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            SystemParameters.StaticPropertyChanged -= OnSystemSettingChanged;
        }
    }
}
