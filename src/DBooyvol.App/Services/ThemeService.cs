using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DBooyvol.App.Services;

/// <summary>
/// Owns the palette dictionary: Light.xaml or Dark.xaml is merged at index 0
/// of the application resources, and every control style resolves colours
/// through DynamicResource. Swapping the dictionary restyles live windows
/// without recreating templates.
/// </summary>
public static class ThemeService
{
    public const string Auto = "auto";
    public const string Light = "light";
    public const string Dark = "dark";

    private static readonly Uri LightUri = new("pack://application:,,,/Themes/Light.xaml");
    private static readonly Uri DarkUri = new("pack://application:,,,/Themes/Dark.xaml");

    private static ResourceDictionary? _palette;
    private static string _mode = Auto;
    private static bool _hooked;

    /// <summary>Raised after the palette changed; templates and the editor listen.</summary>
    public static event Action? Changed;

    public static bool IsDark => _palette is not null && (bool)_palette["Theme.IsDark"];

    public static string Mode => _mode;

    /// <summary>Installs the palette. Safe to call before any window exists.</summary>
    public static void Initialize(string? mode)
    {
        _mode = Normalize(mode);
        ApplyPalette(ShouldUseDark());
        if (_hooked) return;
        _hooked = true;
        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        catch (Exception)
        {
            // No message pump for system events: manual mode still works.
        }
    }

    /// <summary>Switches the mode and persists it through the settings object.</summary>
    public static void SetMode(string? mode)
    {
        _mode = Normalize(mode);
        ApplyPalette(ShouldUseDark());
    }

    public static void ApplyPalette(bool dark)
    {
        var uri = dark ? DarkUri : LightUri;
        if (_palette is not null && _palette.Source == uri)
        {
            if (!ReferenceEquals(_palette, FindPalette())) return;
            Changed?.Invoke();
            return;
        }

        var next = new ResourceDictionary { Source = uri };
        var merged = Application.Current.Resources.MergedDictionaries;
        var existing = FindPalette();
        if (existing is null)
            merged.Insert(0, next);
        else
            merged[merged.IndexOf(existing)] = next;

        _palette = next;
        Changed?.Invoke();
    }

    /// <summary>Colour from the active palette, for code-built visuals.</summary>
    public static Color Color(string key)
    {
        if (_palette is not null && _palette[key] is SolidColorBrush brush)
            return brush.Color;
        return Colors.Transparent;
    }

    public static Brush Brush(string key) => new SolidColorBrush(Color(key));

    private static ResourceDictionary? FindPalette()
    {
        foreach (var dictionary in Application.Current.Resources.MergedDictionaries)
        {
            if (dictionary.Source == LightUri || dictionary.Source == DarkUri)
                return dictionary;
        }
        return null;
    }

    private static string Normalize(string? mode) =>
        mode?.Trim().ToLowerInvariant() switch
        {
            Light => Light,
            Dark => Dark,
            _ => Auto
        };

    private static bool ShouldUseDark() => _mode switch
    {
        Light => false,
        Dark => true,
        _ => SystemPrefersDark()
    };

    /// <summary>AppsUseLightTheme: 0 means dark.</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || _mode != Auto) return;
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(() => ApplyPalette(ShouldUseDark()));
    }
}
