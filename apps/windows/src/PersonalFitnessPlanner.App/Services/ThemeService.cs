using System.Windows;
using System.Windows.Media;

namespace PersonalFitnessPlanner.App.Services;

public static class ThemeService
{
    public static void Apply(string? theme)
    {
        if (Application.Current is null) return;
        var dark = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase);
        Set("WindowBrush", dark ? "#0F172A" : "#F5F7FB");
        Set("SurfaceBrush", dark ? "#111827" : "#FFFFFF");
        Set("AlternateSurfaceBrush", dark ? "#182235" : "#F8FAFC");
        Set("TextBrush", dark ? "#F8FAFC" : "#172033");
        Set("MutedBrush", dark ? "#CBD5E1" : "#667085");
        Set("BorderBrush", dark ? "#334155" : "#DDE3EC");
    }

    private static void Set(string key, string color) =>
        Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
}
