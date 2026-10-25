using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Microsoft.Win32;
using RazerIsland.Battery;

namespace RazerIsland.UI;

/// <summary>
/// Монохромні іконки трею: великі цифри (або значок пристрою) і смужка заряду знизу.
/// Малюються одразу в розмірі трею (без розмиття при масштабуванні) і з обводкою,
/// щоб читатися на прозорій панелі завдань поверх будь-яких шпалер.
/// </summary>
public static class TrayIconRenderer
{
    private static readonly FontFamily GlyphFont = DetectGlyphFont();
    private static readonly FontFamily DigitFont = new("Segoe UI");

    private static FontFamily DetectGlyphFont()
    {
        try
        {
            using var fonts = new InstalledFontCollection();
            if (fonts.Families.Any(f => f.Name == "Segoe Fluent Icons")) return new FontFamily("Segoe Fluent Icons");
        }
        catch { }
        return new FontFamily("Segoe MDL2 Assets");
    }

    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>Розмір іконки в треї для поточного DPI (16 при 100%, 24 при 150%).</summary>
    public static int TraySize => Math.Max(16, System.Windows.Forms.SystemInformation.SmallIconSize.Width);

    public static Bitmap Render(int level, string category, PowerState state, bool light, bool numberStyle, int size)
    {
        level = Math.Clamp(level, 0, 100);
        int alpha = state == PowerState.Off ? 130 : 255;
        var fg = light ? Color.FromArgb(alpha, 0, 0, 0) : Color.FromArgb(alpha, 255, 255, 255);
        var halo = light ? Color.FromArgb(alpha * 170 / 255, 255, 255, 255) : Color.FromArgb(alpha * 170 / 255, 0, 0, 0);

        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        using var fgBrush = new SolidBrush(fg);
        using var haloPen = new Pen(halo, Math.Max(2f, size / 9f)) { LineJoin = LineJoin.Round };
        using var haloBrush = new SolidBrush(halo);

        // смужка заряду знизу
        int barH = Math.Max(2, (int)Math.Round(size * 0.13));
        int barY = size - barH;
        g.FillRectangle(haloBrush, 0, barY - 1, size, barH + 1);
        using (var track = new SolidBrush(Color.FromArgb(fg.A * 80 / 255, fg)))
            g.FillRectangle(track, 1, barY, size - 2, barH - 1);
        int fill = (int)Math.Round((size - 2) * level / 100.0);
        if (level > 0 && fill < 1) fill = 1;
        if (fill > 0) g.FillRectangle(fgBrush, 1, barY, fill, barH - 1);

        // верх: цифри або значок пристрою, вписані в усю вільну висоту;
        // під час заряджання праворуч окреме місце для блискавки
        bool charging = state == PowerState.Charging;
        float inset = haloPen.Width / 2;
        float textH = barY - 1.5f;
        float boltW = charging ? (float)Math.Round(size * 0.3) : 0;
        using var path = new GraphicsPath();
        if (numberStyle)
            path.AddString(level.ToString(), DigitFont, (int)FontStyle.Bold, 100, PointF.Empty, StringFormat.GenericTypographic);
        else
            path.AddString(Glyphs.ForCategory(category), GlyphFont, (int)FontStyle.Regular, 100, PointF.Empty, StringFormat.GenericTypographic);
        FitInto(path, RectangleF.Inflate(new RectangleF(0, 0, size - boltW, textH), -inset, -inset));
        g.DrawPath(haloPen, path);
        g.FillPath(fgBrush, path);

        if (charging)
        {
            using var bolt = new GraphicsPath();
            bolt.AddPolygon(new PointF[] { new(6, 0), new(0, 10), new(4.5f, 10), new(2, 18), new(10.5f, 7), new(6, 7), new(9.5f, 0) });
            FitInto(bolt, RectangleF.Inflate(new RectangleF(size - boltW, 0, boltW, textH), -inset * 0.6f, -inset));
            g.DrawPath(haloPen, bolt);
            g.FillPath(fgBrush, bolt);
        }
        return bmp;
    }

    // масштабувати контур, щоб він заповнив box (зберігаючи пропорції), і відцентрувати
    private static void FitInto(GraphicsPath path, RectangleF box)
    {
        var b = path.GetBounds();
        if (b.Width <= 0 || b.Height <= 0) return;
        float k = Math.Min(box.Width / b.Width, box.Height / b.Height);
        using var m = new Matrix();
        m.Translate(box.X + (box.Width - b.Width * k) / 2, box.Y + (box.Height - b.Height * k) / 2);
        m.Scale(k, k);
        m.Translate(-b.X, -b.Y);
        path.Transform(m);
    }
}
