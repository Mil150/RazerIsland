using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RazerIsland.UI;

/// <summary>Значки з Segoe Fluent Icons / Segoe MDL2 Assets.</summary>
public static class Glyphs
{
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public const string Pin = "\uE718";
    public const string Charging = "\uE945";
    public const string MicChar = "\uE720";

    public static string ForCategory(string category)
    {
        if (category.Contains("MOUSE")) return "\uE962";
        if (Regex.IsMatch(category, "AUDIO|HEADSET")) return "\uE7F6";
        if (category.Contains("KEYBOARD")) return "\uE765";
        if (Regex.IsMatch(category, "CONTROLLER|GAMEPAD")) return "\uE7FC";
        if (category.Contains("PHONE")) return "\uE8EA";
        return "\uE83F";
    }

    public static FrameworkElement ChargingIcon(double size) => new TextBlock
    {
        FontFamily = IconFont, FontSize = size, Text = Charging, Foreground = Palette.Brush(Palette.Green),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Значок мікрофона (перекреслений, якщо вимкнено).</summary>
    public static FrameworkElement MicIcon(bool off, double size, string color)
    {
        var g = new Grid { Width = size + 2, Height = size + 2 };
        g.Children.Add(new TextBlock
        {
            FontFamily = IconFont, FontSize = size, Text = MicChar, Foreground = Palette.Brush(color),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        if (off)
        {
            g.Children.Add(new Line
            {
                X1 = 1, Y1 = 1, X2 = size + 1, Y2 = size + 1,
                Stroke = Palette.Brush(color), StrokeThickness = Math.Max(1.6, size / 9),
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
        }
        return g;
    }
}
