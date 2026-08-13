using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClaudeSessions;

/// <summary>Applies a <see cref="ThemePalette"/> to the live application resources.</summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; } = true;

    /// <summary>Resolves System against the OS setting, then repaints every themed brush.</summary>
    public static void Apply(ThemePreference preference)
    {
        IsDark = preference switch
        {
            ThemePreference.Light => false,
            ThemePreference.Dark => true,
            _ => SystemPrefersDark(),
        };

        // Replace the entries rather than recolouring the brushes: WPF freezes some of them when
        // it seals styles and templates, and a frozen brush throws on assignment. Every themed
        // colour is referenced with DynamicResource, so replacing the entry repaints the window.
        var palette = IsDark ? ThemePalette.Dark : ThemePalette.Light;
        var theme = Application.Current.Resources.MergedDictionaries
                        .FirstOrDefault(d => d.Contains("WindowBrush"))
                    ?? Application.Current.Resources;
        foreach (var (key, color) in palette)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            theme[key] = brush;
        }

        foreach (Window window in Application.Current.Windows)
            SetTitleBar(window, IsDark);
    }

    /// <summary>False when the key is missing or unreadable — dark stays the app's default.</summary>
    private static bool SystemPrefersDark()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", null);
            return value is int light ? light == 0 : true;
        }
        catch
        {
            return true;
        }
    }

    public static void ApplyTitleBar(Window window) => SetTitleBar(window, IsDark);

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// WPF leaves the title bar to the OS theme, so a light window under a dark system keeps dark
    /// chrome. Best-effort: pre-20H1 builds just ignore the attribute.
    /// </summary>
    private static void SetTitleBar(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var value = dark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)); }
        catch (DllNotFoundException) { }
    }
}
