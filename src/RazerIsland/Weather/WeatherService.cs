using System.Globalization;
using System.Text.Json;
using RazerIsland.Core;

namespace RazerIsland.Weather;

public sealed record WeatherNow(string Icon, string Text, int Temp, int Feels, int High, int Low);
public sealed record GeoPlace(string Name, string Country, double Latitude, double Longitude);

/// <summary>Погода й пошук міста через open-meteo.com (без ключа).</summary>
public static class WeatherService
{
    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    public static async Task<WeatherNow> GetAsync(double lat, double lon)
    {
        var url = "https://api.open-meteo.com/v1/forecast?latitude=" + lat.ToString(CI) + "&longitude=" + lon.ToString(CI)
                + "&current=temperature_2m,apparent_temperature,weather_code&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1";
        using var doc = JsonDocument.Parse(await Http.Client.GetStringAsync(url));
        var cur = doc.RootElement.GetProperty("current");
        var daily = doc.RootElement.GetProperty("daily");
        var (icon, text) = Describe(cur.GetProperty("weather_code").GetInt32());
        return new WeatherNow(icon, text,
            Round(cur.GetProperty("temperature_2m").GetDouble()),
            Round(cur.GetProperty("apparent_temperature").GetDouble()),
            Round(daily.GetProperty("temperature_2m_max")[0].GetDouble()),
            Round(daily.GetProperty("temperature_2m_min")[0].GetDouble()));
    }

    /// <summary>Перше знайдене місто або null.</summary>
    public static async Task<GeoPlace?> FindCityAsync(string name)
    {
        var url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=uk&name=" + Uri.EscapeDataString(name);
        using var doc = JsonDocument.Parse(await Http.Client.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var r = results[0];
        return new GeoPlace(
            r.GetProperty("name").GetString() ?? name,
            r.TryGetProperty("country", out var c) ? c.GetString() ?? "" : "",
            r.GetProperty("latitude").GetDouble(),
            r.GetProperty("longitude").GetDouble());
    }

    // PowerShell [Math]::Round — банківське округлення; лишаємо так само
    private static int Round(double v) => (int)Math.Round(v);

    public static (string Icon, string Text) Describe(int code)
    {
        const string sun = "\u2600", part = "\u26C5", cloud = "\u2601", rain = "\u2602", snow = "\u2744", storm = "\u26A1";
        if (code == 0) return (sun, "Ясно");
        if (code <= 2) return (part, "Мінлива хмарність");
        if (code == 3) return (cloud, "Хмарно");
        if (code <= 48) return (cloud, "Туман");
        if (code <= 57) return (rain, "Мряка");
        if (code <= 67) return (rain, "Дощ");
        if (code <= 77) return (snow, "Сніг");
        if (code <= 82) return (rain, "Злива");
        if (code <= 86) return (snow, "Снігопад");
        return (storm, "Гроза");
    }
}
