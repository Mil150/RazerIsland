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
        var settings = new Forms.ToolStripMenuItem("Налаштування…");
        settings.Font = new System.Drawing.Font(settings.Font, System.Drawing.FontStyle.Bold);
        settings.Click += (_, _) => openSettings();
        _menu.Items.Add(settings);
        _menu.Items.Add("Показати / сховати віджет", null, (_, _) => toggleWidget());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Вийти", null, (_, _) => exit());
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

        var suffix = state switch { PowerState.Charging => " (заряджається)", PowerState.Off => " (вимкнено)", _ => "" };
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
