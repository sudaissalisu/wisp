using Android.OS;
using Android.Views;
using Android.Graphics;
using Microsoft.Maui.ApplicationModel;

namespace Wisp.Android;

/// <summary>
/// Keeps the system status/navigation bar icon contrast in sync with the app's
/// light or dark theme, including Android 15+ edge-to-edge windows.
/// </summary>
public static class AndroidTheme
{
    public static void Apply(bool light)
    {
        try
        {
            var activity = Platform.CurrentActivity;
            var window = activity?.Window;
            if (window is null) return;

            if ((int)Build.VERSION.SdkInt >= 30)
            {
                var controller = window.InsetsController;
                if (controller is not null)
                {
                    var flags = (int)(WindowInsetsControllerAppearance.LightStatusBars |
                                      WindowInsetsControllerAppearance.LightNavigationBars);
                    controller.SetSystemBarsAppearance(light ? flags : 0, flags);
                }
            }
            else
            {
                var flags = SystemUiFlags.LayoutStable | SystemUiFlags.LayoutFullscreen;
                if (light) flags |= SystemUiFlags.LightStatusBar | SystemUiFlags.LightNavigationBar;
                window.DecorView.SystemUiVisibility = (StatusBarVisibility)(int)flags;
            }

            if ((int)Build.VERSION.SdkInt < 35)
            {
                var color = global::Android.Graphics.Color.ParseColor(light ? "#F4F7F6" : "#0B0E12");
                window.SetStatusBarColor(color);
                window.SetNavigationBarColor(color);
            }
        }
        catch
        {
        }
    }
}
