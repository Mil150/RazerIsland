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
    // секрети в пам'яті відкриті, у файлі — лише зашифрований блок Secrets
    [JsonIgnore] public string DiscordClientSecret { get; set; } = "";
    [JsonIgnore] public string DiscordToken { get; set; } = "";
    [JsonIgnore] public string DiscordRefresh { get; set; } = "";

    /// <summary>Dpapi.Protect(JSON {secret, token, refresh}); null — секретів немає.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Secrets { get; set; }
    // відкриті поля до 0.2.6: лише читаються для міграції й більше не пишуться
    [JsonPropertyName("DiscordClientSecret"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LegacySecret { get; set; }
    [JsonPropertyName("DiscordToken"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LegacyToken { get; set; }
    [JsonPropertyName("DiscordRefresh"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LegacyRefresh { get; set; }

    /// <summary>Збережений блок Secrets не розшифровується (інший ПК чи користувач). Його не перезаписуємо, поки не введуть нові дані.</summary>
    [JsonIgnore] public bool SecretsLocked { get; private set; }
    private string? _lockedSecrets;
    // DPAPI щоразу дає інший шифротекст — перешифровуємо, лише коли змінилися самі секрети
    private (string, string, string)? _sealedFor;

    public double BgOpacity { get; set; } = 0.8;
    public bool FirstRunDone { get; set; }
    public bool DesktopShortcut { get; set; }
    public bool? V2 { get; set; }
    /// <summary>Колір подій для кожного календаря (#RRGGBB); ключ — <see cref="ColorKey"/>, а не саме посилання.</summary>
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
        s.DiscordClientId ??= "";
        bool dirty = false;
        if (s.Secrets is not null)
        {
            if (TryReadSecrets(s.Secrets, out var t))
            {
                (s.DiscordClientSecret, s.DiscordToken, s.DiscordRefresh) = t;
                s._sealedFor = t;
            }
            else
            {
                s.SecretsLocked = true;
                s._lockedSecrets = s.Secrets;
                Log.Write($"Не вдалося розшифрувати секрети Discord (код {Dpapi.LastError})");
            }
        }
        else (s.DiscordClientSecret, s.DiscordToken, s.DiscordRefresh) = (s.LegacySecret ?? "", s.LegacyToken ?? "", s.LegacyRefresh ?? "");
        if (s.LegacySecret is not null || s.LegacyToken is not null || s.LegacyRefresh is not null)
        {
            s.LegacySecret = s.LegacyToken = s.LegacyRefresh = null;
            dirty = true;
        }
        if (MigrateColorKeys(s.CalendarColors) is { } colors) { s.CalendarColors = colors; dirty = true; }
        // v2: за замовчуванням не поверх вікон
        if (s.V2 is null) { s.Topmost = false; s.V2 = true; }
        // хто вже користувався віджетом — лишається з українською; новий — мова Windows
        if (s.Language is not ("uk" or "en")) s.Language = s.FirstRunDone ? "uk" : L.SystemDefault();
        if (dirty) s.Save(); // відкриті секрети й посилання зникають з файлу одразу
        return s;
    }

    public void Save()
    {
        try
        {
            Secrets = SealSecrets();
            var tmp = AppInfo.SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
            File.Move(tmp, AppInfo.SettingsPath, overwrite: true);
        }
        catch (Exception e) { Log.Write("Не вдалося зберегти settings.json: " + e.Message); }
    }

    private string? SealSecrets()
    {
        var cur = (DiscordClientSecret, DiscordToken, DiscordRefresh);
        if (cur == ("", "", "")) return SecretsLocked ? _lockedSecrets : null;
        if (_sealedFor == cur && Secrets is not null) return Secrets;
        var sealedText = Dpapi.Protect(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["secret"] = cur.DiscordClientSecret, ["token"] = cur.DiscordToken, ["refresh"] = cur.DiscordRefresh,
        }));
        (_sealedFor, SecretsLocked, _lockedSecrets) = (cur, false, null);
        return sealedText;
    }

    /// <summary>Розшифрувати блок Secrets; false — чужий ключ або пошкоджені дані.</summary>
    public static bool TryReadSecrets(string? secrets, out (string Secret, string Token, string Refresh) t)
    {
        t = ("", "", "");
        if (!Dpapi.TryUnprotect(secrets, out var json)) return false;
        if (json.Length == 0) return true;
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
            t = (d.GetValueOrDefault("secret") ?? "", d.GetValueOrDefault("token") ?? "", d.GetValueOrDefault("refresh") ?? "");
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Ключ кольору календаря: перші 16 hex SHA-256 посилання — щоб у settings.json не було самих посилань.</summary>
    public static string ColorKey(string url) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim())))[..16];

    /// <summary>Нові кольори з ключами-хешами замість посилань; null — міняти нічого.</summary>
    public static Dictionary<string, string>? MigrateColorKeys(Dictionary<string, string> colors)
    {
        if (!colors.Keys.Any(k => k.Contains("://"))) return null;
        var result = colors.Where(kv => !kv.Key.Contains("://")).ToDictionary();
        foreach (var (url, hex) in colors.Where(kv => kv.Key.Contains("://"))) result.TryAdd(ColorKey(url), hex);
        return result;
    }

    /// <summary>Непрозорість фону з перевіркою меж.</summary>
    [JsonIgnore] public double SafeBgOpacity => BgOpacity < 0.3 || BgOpacity > 1 ? 0.8 : BgOpacity;
}
