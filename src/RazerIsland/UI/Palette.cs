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

    private static readonly Dictionary<string, string> BrandHex = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Razer"] = "#44D62C", ["Logitech"] = "#00B8FC", ["Samsung"] = "#1428A0", ["Apple"] = "#A2AAAD",
        ["Microsoft"] = "#107C10", ["Sony"] = "#0070D1", ["SteelSeries"] = "#FF5200", ["Corsair"] = "#FFE600",
        ["HyperX"] = "#E31837", ["JBL"] = "#FF3300", ["Google"] = "#4285F4", ["Xiaomi"] = "#FF6900", ["Huawei"] = "#CF0A2C",
    };

    /// <summary>Колір значка пристрою: фірмовий колір бренду, висвітлений до контрасту 3:1 на темній картці.</summary>
    public static string DeviceIconColor(string brand)
    {
        if (!BrandHex.TryGetValue(brand, out var hex)) return "#D6D9DE";
        var c = (Color)ColorConverter.ConvertFromString(hex);
        var bg = Color.FromRgb(0x14, 0x16, 0x19);
        for (int i = 0; i < 10 && Contrast(c, bg) < 3; i++)
            c = Color.FromRgb((byte)(c.R + (255 - c.R) * 0.15), (byte)(c.G + (255 - c.G) * 0.15), (byte)(c.B + (255 - c.B) * 0.15));
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private static double Contrast(Color a, Color b)
    {
        static double Lum(Color c)
        {
            static double Ch(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        double la = Lum(a), lb = Lum(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Фон картки з урахуванням налаштованої непрозорості.</summary>
    public static string CardBackground(double opacity, bool island)
    {
        var a = ((int)Math.Round(255 * opacity)).ToString("X2");
        return island ? $"#{a}050506" : $"#{a}141619";
    }
}
