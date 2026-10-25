using RazerIsland.Battery;
using RazerIsland.Core;
using Forms = System.Windows.Forms;

namespace RazerIsland.UI;

/// <summary>Іконки з відсотком у треї — по одній на пристрій — і спільне меню.</summary>
public sealed class TrayManager : IDisposable
{
    private sealed class Item(Forms.NotifyIcon icon)
    {
        public Forms.NotifyIcon Icon { get; } = icon;
        public IntPtr Handle;
        public string Key = "";
    }

    private readonly Dictionary<string, Item> _items = new();
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Action _toggleWidget;

    public TrayManager(Action openSettings, Action toggleWidget, Action exit)
    {
        _toggleWidget = toggleWidget;
        _menu.Renderer = new Forms.ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false };
        _menu.BackColor = DarkMenuColors.Back;
        _menu.ForeColor = System.Drawing.Color.FromArgb(0xE6, 0xE8, 0xEB);
        _menu.ShowImageMargin = false;
        var settings = new Forms.ToolStripMenuItem(L.T("Налаштування…", "Settings…"));
        settings.Font = new System.Drawing.Font(settings.Font, System.Drawing.FontStyle.Bold);
        settings.Click += (_, _) => openSettings();
        _menu.Items.Add(settings);
        _menu.Items.Add(L.T("Показати / сховати віджет", "Show / hide widget"), null, (_, _) => toggleWidget());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(L.T("Вийти", "Exit"), null, (_, _) => exit());
        foreach (Forms.ToolStripItem item in _menu.Items)
            if (item is not Forms.ToolStripSeparator) item.Padding = new Forms.Padding(6, 5, 16, 5);
    }

    // кольори як у вікні налаштувань і меню віджета
    private sealed class DarkMenuColors : Forms.ProfessionalColorTable
    {
        public static readonly System.Drawing.Color Back = System.Drawing.Color.FromArgb(0x1B, 0x1E, 0x23);
        private static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(0x2A, 0x2D, 0x33);
        private static readonly System.Drawing.Color Border = System.Drawing.Color.FromArgb(0x33, 0x37, 0x3D);
        public override System.Drawing.Color ToolStripDropDownBackground => Back;
        public override System.Drawing.Color MenuBorder => Border;
        public override System.Drawing.Color MenuItemBorder => Hover;
        public override System.Drawing.Color MenuItemSelected => Hover;
        public override System.Drawing.Color SeparatorDark => Hover;
        public override System.Drawing.Color SeparatorLight => Back;
    }

    /// <summary>Скинути кеш іконок (зміна стилю або теми).</summary>
    public void Invalidate()
    {
        foreach (var it in _items.Values) it.Key = "";
    }

    public void Update(RazerDevice d, PowerState state, string trayStyle)
    {
        var it = GetItem(d.Serial);
        bool light = TrayIconRenderer.SystemUsesLightTheme();
        int size = TrayIconRenderer.TraySize;
        var key = $"{d.Level}|{state}|{light}|{trayStyle}|{d.Category}|{size}";
        if (it.Key != key)
        {
            it.Key = key;
            using var bmp = TrayIconRenderer.Render(d.Level, d.Category, state, light, trayStyle == "number", size);
            var h = bmp.GetHicon();
            it.Icon.Icon = System.Drawing.Icon.FromHandle(h);
            if (it.Handle != IntPtr.Zero) Native.DestroyIcon(it.Handle);
            it.Handle = h;
        }
        if (!it.Icon.Visible) it.Icon.Visible = true;

        var suffix = state switch { PowerState.Charging => L.T(" (заряджається)", " (charging)"), PowerState.Off => L.T(" (вимкнено)", " (off)"), _ => "" };
        var tip = $"{d.Name}: {d.Level}%{suffix}";
        it.Icon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    private Item GetItem(string serial)
    {
        if (_items.TryGetValue(serial, out var it)) return it;
        var ni = new Forms.NotifyIcon { ContextMenuStrip = _menu };
        ni.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) _toggleWidget(); };
        it = new Item(ni);
        _items[serial] = it;
        return it;
    }

    public void Dispose()
    {
        foreach (var it in _items.Values)
        {
            it.Icon.Visible = false;
            it.Icon.Dispose();
            if (it.Handle != IntPtr.Zero) Native.DestroyIcon(it.Handle);
        }
        _items.Clear();
        _menu.Dispose();
    }
}
