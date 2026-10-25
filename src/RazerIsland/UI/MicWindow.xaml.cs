using System.Windows;
using System.Windows.Interop;
using RazerIsland.Core;

namespace RazerIsland.UI;

/// <summary>Окремий маленький острівець мікрофона (для режиму «плаваюча картка» або коли віджет схований).</summary>
public partial class MicWindow : Window
{
    public MicWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            try
            {
                var h = new WindowInteropHelper(this).Handle;
                var ex = Native.GetLong(h, Native.GWL_EXSTYLE);
                // tool + click-through + no-activate
                Native.SetLong(h, Native.GWL_EXSTYLE,
                    (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE) & ~Native.WS_EX_APPWINDOW);
            }
            catch { }
        };
    }

    public void SetNotice(string text, string color, bool off)
    {
        MText.Text = text;
        MText.Foreground = Palette.Brush(color);
        MIconHost.Child = Glyphs.MicIcon(off, 15, color);
    }

    /// <summary>Показати/сховати; без сповіщення — лише перекреслений мікрофон.</summary>
    public void Update(bool need, bool noticeActive)
    {
        if (!need)
        {
            if (IsVisible) Hide();
            return;
        }
        if (!noticeActive)
        {
            MIconHost.Child = Glyphs.MicIcon(true, 14, Palette.Red);
            MText.Visibility = Visibility.Collapsed;
        }
        else MText.Visibility = Visibility.Visible;
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top;
        if (!IsVisible) Show();
        Topmost = false;
        Topmost = true;
    }
}
