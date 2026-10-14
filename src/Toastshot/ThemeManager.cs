using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Toastshot;

public enum AppTheme
{
    Dark,
    Light
}

/// <summary>Follows the Windows app mode: swaps Themes/Dark.xaml and Themes/Light.xaml and matches each window's title bar.</summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private static Application? _following;
    private static bool _titleBarHandler;

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>Settings › Personalisation › Colours › "Choose your default app mode".</summary>
    public static AppTheme SystemTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int light && light == 1 ? AppTheme.Light : AppTheme.Dark;
    }

    /// <summary>
    /// Puts the theme's colours into the app's resources, replacing the other theme's. An app without Toastshot's
    /// resources yet (tests) also gets the control styles.
    /// </summary>
    public static void Apply(Application app, AppTheme theme)
    {
        var merged = app.Resources.MergedDictionaries;
        var colours = Load(theme == AppTheme.Light ? "Light" : "Dark");
        var index = merged.ToList().FindIndex(d => IsNamed(d, "Dark.xaml") || IsNamed(d, "Light.xaml"));
        if (index >= 0)
        {
            merged[index] = colours;
        }
        else
        {
            merged.Insert(0, colours);
            if (!merged.Any(d => IsNamed(d, "Controls.xaml")))
            {
                merged.Add(Load("Controls"));
                merged.Add(Load("Extras"));
            }
        }

        Current = theme;
        if (!_titleBarHandler)
        {
            _titleBarHandler = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => MatchTitleBar((Window)sender)));
        }

        foreach (Window window in app.Windows)
        {
            MatchTitleBar(window);
        }
    }

    /// <summary>Switches theme whenever the Windows app mode changes.</summary>
    public static void FollowSystem(Application app)
    {
        _following = app;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void StopFollowingSystem()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _following = null;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && _following is { } app)
        {
            app.Dispatcher.BeginInvoke(() =>
            {
                var theme = SystemTheme();
                if (theme != Current)
                {
                    Apply(app, theme);
                }
            });
        }
    }

    /// <summary>
    /// Paints the title bar in the window's own background (and its text in ink), so each window is one surface
    /// instead of wearing the Windows accent colour. Colours need Windows 11; dark mode alone works on 10 20H1+.
    /// </summary>
    private static void MatchTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var dark = Current == AppTheme.Dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        SetColour(window, hwnd, DWMWA_CAPTION_COLOR, "SurfaceBrush");
        SetColour(window, hwnd, DWMWA_TEXT_COLOR, "InkMutedBrush");
        SetColour(window, hwnd, DWMWA_BORDER_COLOR, "LineBrush");
    }

    private static void SetColour(Window window, IntPtr hwnd, int attribute, string brush)
    {
        if (window.TryFindResource(brush) is SolidColorBrush { Color: var c })
        {
            var colorRef = c.R | (c.G << 8) | (c.B << 16); // COLORREF is 0x00BBGGRR
            DwmSetWindowAttribute(hwnd, attribute, ref colorRef, sizeof(int));
        }
    }

    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    private static bool IsNamed(ResourceDictionary dictionary, string file) =>
        dictionary.Source?.OriginalString.EndsWith(file, StringComparison.OrdinalIgnoreCase) == true;

    private static ResourceDictionary Load(string name) =>
        new() { Source = new Uri($"pack://application:,,,/Toastshot;component/Themes/{name}.xaml") };

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
