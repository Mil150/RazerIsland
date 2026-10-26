using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RazerIsland.UI;

/// <summary>Світло, що біжить по гранях острівця на час сповіщення (верхньої грані в острівця немає — він прилягає до краю екрана).</summary>
public static class EdgeGlow
{
    private sealed record Saved(Brush Border, Thickness Thickness);
    private static readonly Dictionary<Border, Saved> State = new();

    public static void Start(Border card, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        State.TryAdd(card, new Saved(card.BorderBrush, card.BorderThickness));

        // градієнт зі світлою смугою посередині обертається — смуга біжить по гранях
        var spin = new RotateTransform(0, 0.5, 0.5);
        card.BorderBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5), RelativeTransform = spin,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x30, color.R, color.G, color.B), 0.0),
                new GradientStop(color, 0.5),
                new GradientStop(Color.FromArgb(0x30, color.R, color.G, color.B), 1.0),
            },
        };
        // товщають лише наявні грані: у острівця верхня має товщину 0 і такою лишається
        var t = card.BorderThickness;
        card.BorderThickness = new Thickness(t.Left > 0 ? 2 : 0, t.Top > 0 ? 2 : 0, t.Right > 0 ? 2 : 0, t.Bottom > 0 ? 2 : 0);
        spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.3)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    public static void Stop(Border card)
    {
        if (!State.Remove(card, out var s)) return;
        card.BorderBrush = s.Border;
        card.BorderThickness = s.Thickness;
    }
}
