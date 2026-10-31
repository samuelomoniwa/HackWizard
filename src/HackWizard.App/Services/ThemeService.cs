using System.Windows;
using Microsoft.Win32;

namespace HackWizard.App.Services;

public static class ThemeService
{
    public static bool IsDark { get; private set; }

    public static void ApplySystemTheme()
    {
        var dark = false;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { }
        Apply(dark);
    }

    public static void Toggle() => Apply(!IsDark);

    public static void Apply(bool dark)
    {
        IsDark = dark;
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/HackWizard;component/Themes/{(dark ? "Dark" : "Light")}.xaml") };
        var merged = Application.Current.Resources.MergedDictionaries;
        var existing = merged.FirstOrDefault(d => d.Source?.OriginalString is { } s && (s.EndsWith("Dark.xaml") || s.EndsWith("Light.xaml")));
        if (existing != null) merged.Remove(existing);
        merged.Insert(0, dict);
    }
}
