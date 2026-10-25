using System.Windows.Media;
using RazerIsland.Battery;

namespace RazerIsland.UI;

/// <summary>Кольори віджета.</summary>
public static class Palette
{
    public const string Green = "#44D62C";
    public const string Yellow = "#FFD23F";
    public const string Red = "#FF3B3B";
    public const string Gray = "#6B7078";
    public const string Accent = "#4C8DFF";

    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static SolidColorBrush Brush(string hex)
    {
        if (Cache.TryGetValue(hex, out var b)) return b;
        b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return Cache[hex] = b;
    }

    public static string LevelColor(int level, PowerState state)
    {
        if (state == PowerState.Off) return Gray;
        if (level <= 20) return Red;
        if (level <= 50) return Yellow;
        return Green;
    }

    /// <summary>Фон картки з урахуванням налаштованої непрозорості.</summary>
    public static string CardBackground(double opacity, bool island)
    {
        var a = ((int)Math.Round(255 * opacity)).ToString("X2");
        return island ? $"#{a}050506" : $"#{a}141619";
    }
}
