using Wisp.Core;

namespace Wisp.Android;

public partial class App : Application
{
    private readonly SettingsStore _settingsStore;

    private string _theme = "dark";

    public App()
    {
        InitializeComponent();
        _settingsStore = new SettingsStore(
            Path.Combine(FileSystem.AppDataDirectory, "settings.json"));
        _theme = _settingsStore.Load().Theme;
        UserAppTheme = _theme switch
        {
            "light" => AppTheme.Light,
            "system" => AppTheme.Unspecified,
            _ => AppTheme.Dark
        };
    }

    public void ApplyTheme(string theme)
    {
        _theme = theme;
        UserAppTheme = theme switch
        {
            "light" => AppTheme.Light,
            "system" => AppTheme.Unspecified,
            _ => AppTheme.Dark
        };
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // Reapply after the platform window exists so AppThemeBinding picks it up.
        UserAppTheme = _theme switch
        {
            "light" => AppTheme.Light,
            "system" => AppTheme.Unspecified,
            _ => AppTheme.Dark
        };
        var page = new MainPage();
        var window = new Window(page);
        window.Destroying += (_, _) => page.Shutdown();
        window.Deactivated += (_, _) => page.OnAppBackgrounded();
        window.Activated += (_, _) => page.OnAppResumed();
        return window;
    }
}
