using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Microsoft.Win32;
using RazerIsland.Battery;

namespace RazerIsland.UI;

/// <summary>Мінімалістичні монохромні іконки трею (білі на темній темі, чорні на світлій).</summary>
public static class TrayIconRenderer
{
    private static readonly string GlyphFontName = DetectGlyphFont();

    private static string DetectGlyphFont()
    {
        try
        {
            using var fonts = new InstalledFontCollection();
            if (fonts.Families.Any(f => f.Name == "Segoe Fluent Icons")) return "Segoe Fluent Icons";
        }
        catch { }
        return "Segoe MDL2 Assets";
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

    /// <summary>32×32: зверху значок пристрою або цифри, знизу батарейка з рівнем.</summary>
    public static Bitmap Render(int level, string category, PowerState state, bool light, bool numberStyle)
    {
        const int size = 32;
        int alpha = state == PowerState.Off ? 120 : 255;
        var col = light ? Color.FromArgb(alpha, 0, 0, 0) : Color.FromArgb(alpha, 255, 255, 255);
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(col);
        using var pen = new Pen(col, 2);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        if (numberStyle)
        {
            var text = level.ToString();
            using var font = new Font("Segoe UI Semibold", text.Length >= 3 ? 15 : 20, FontStyle.Regular, GraphicsUnit.Pixel);
            g.DrawString(text, font, brush, new RectangleF(0, -1, size, 23), sf);
        }
        else
        {
            using var font = new Font(GlyphFontName, 19, FontStyle.Regular, GraphicsUnit.Pixel);
            g.DrawString(Glyphs.ForCategory(category), font, brush, new RectangleF(0, 0, size, 22), sf);
        }

        // батарейка з рівнем заряду
        const int bx = 3, by = 22, bw = 23, bh = 8;
        g.DrawRectangle(pen, bx, by, bw, bh);
        g.FillRectangle(brush, bx + bw + 1, by + 2, 3, bh - 4);
        int inner = (int)Math.Round((bw - 4) * Math.Clamp(level, 0, 100) / 100.0);
        if (level > 0 && inner < 1) inner = 1;
        if (inner > 0) g.FillRectangle(brush, bx + 2, by + 2, inner, bh - 4 + 1);

        // заряджання — маленька блискавка у правому верхньому куті
        if (state == PowerState.Charging)
        {
            g.FillPolygon(brush, new PointF[]
            {
                new(27, 0), new(22, 9), new(26, 9), new(24, 16), new(31, 6), new(27, 6), new(30, 0),
            });
        }
        return bmp;
    }
}
