using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RazerIsland.Core;

/// <summary>Налаштування (settings.json). Формат сумісний з PowerShell-версією віджета.</summary>
public sealed class Settings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Topmost { get; set; }
    public bool Visible { get; set; } = true;
    public bool AutostartSet { get; set; }
    public bool Pinned { get; set; }
    /// <summary>"strip" — острівець зверху, "full" — плаваюча картка.</summary>
    public string Mode { get; set; } = "strip";
    /// <summary>"icon" — значок пристрою, "number" — цифри.</summary>
    public string TrayStyle { get; set; } = "icon";

    public double WeatherLat { get; set; } = 50.4501;
    public double WeatherLon { get; set; } = 30.5234;
    public string WeatherCity { get; set; } = "Kyiv";

    public string DiscordClientId { get; set; } = "";
    public string DiscordClientSecret { get; set; } = "";
    public string DiscordToken { get; set; } = "";
    public string DiscordRefresh { get; set; } = "";

    public double BgOpacity { get; set; } = 0.8;
    public bool FirstRunDone { get; set; }
    public bool DesktopShortcut { get; set; }
    public bool? V2 { get; set; }
    /// <summary>Колір подій для кожного iCal-посилання (#RRGGBB).</summary>
    public Dictionary<string, string> CalendarColors { get; set; } = new();
    /// <summary>"uk" або "en".</summary>
    public string? Language { get; set; }

    [JsonIgnore] public bool IsIsland => Mode != "full";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Settings Load()
    {
        Settings s = new();
        try
        {
            if (File.Exists(AppInfo.SettingsPath))
                s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppInfo.SettingsPath), Json) ?? new();
        }
        catch (Exception e) { Log.Write("Не вдалося прочитати settings.json: " + e.Message); }

        s.Mode ??= "strip";
        s.TrayStyle ??= "icon";
        s.WeatherCity ??= "";
        s.CalendarColors ??= new();
        s.DiscordClientId ??= ""; s.DiscordClientSecret ??= ""; s.DiscordToken ??= ""; s.DiscordRefresh ??= "";
        // v2: за замовчуванням не поверх вікон
        if (s.V2 is null) { s.Topmost = false; s.V2 = true; }
        // хто вже користувався віджетом — лишається з українською; новий — мова Windows
        if (s.Language is not ("uk" or "en")) s.Language = s.FirstRunDone ? "uk" : L.SystemDefault();
        return s;
    }

    public void Save()
    {
        try { File.WriteAllText(AppInfo.SettingsPath, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false)); }
        catch (Exception e) { Log.Write("Не вдалося зберегти settings.json: " + e.Message); }
    }

    /// <summary>Непрозорість фону з перевіркою меж.</summary>
    [JsonIgnore] public double SafeBgOpacity => BgOpacity < 0.3 || BgOpacity > 1 ? 0.8 : BgOpacity;
}
