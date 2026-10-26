using System.IO;
using System.Text;
using RazerIsland.Core;

namespace RazerIsland.Calendar;

/// <summary>Список iCal-посилань (calendars.txt) і завантажені з них події.</summary>
public sealed class CalendarService
{
    private readonly Dictionary<string, List<CalendarEvent>> _data = new();

    public bool LastError { get; private set; }
    /// <summary>Назва календаря (X-WR-CALNAME) за посиланням.</summary>
    public Dictionary<string, string> Names { get; } = new();
    public int LoadedCount => _data.Count;
    public IEnumerable<CalendarEvent> AllEvents => _data.Values.SelectMany(v => v);

    /// <summary>Викликається на UI-потоці після кожного завантаження.</summary>
    public event Action? Changed;

    public static List<string> GetUrls()
    {
        try
        {
            if (!File.Exists(AppInfo.CalendarsPath)) return [];
            return File.ReadAllLines(AppInfo.CalendarsPath, Encoding.UTF8)
                .Select(l => l.Trim().TrimStart('\uFEFF'))
                .Where(l => l.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || l.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>Зберегти посилання; true, якщо список змінився (тоді дані перезавантажуються).</summary>
    public bool SaveUrls(IEnumerable<string> lines)
    {
        var urls = lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (string.Join("|", urls) == string.Join("|", GetUrls())) return false;
        if (urls.Count > 0) File.WriteAllLines(AppInfo.CalendarsPath, urls, new UTF8Encoding(false));
        else if (File.Exists(AppInfo.CalendarsPath)) File.Delete(AppInfo.CalendarsPath);
        Reload();
        return true;
    }

    public void Reload()
    {
        _data.Clear();
        Refresh();
    }

    public void Refresh()
    {
        var urls = GetUrls();
        foreach (var k in _data.Keys.Where(k => !urls.Contains(k)).ToList()) _data.Remove(k);
        if (urls.Count == 0) { Changed?.Invoke(); return; }
        foreach (var u in urls) _ = LoadAsync(u);
    }

    private async Task LoadAsync(string url)
    {
        try
        {
            var ics = await Http.Client.GetStringAsync(url);
            var from = DateTime.Today;
            var events = await Task.Run(() => IcsParser.Parse(ics, from, from.AddDays(8)));
            _data[url] = events.Select(e => e with { Calendar = url }).ToList();
            // назва календаря, як у Google (iCal не передає кольорів — їх задають у налаштуваннях)
            var name = System.Text.RegularExpressions.Regex.Match(ics, @"^X-WR-CALNAME:(.+?)\r?$", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (name.Success) Names[url] = name.Groups[1].Value.Trim();
            LastError = false;
        }
        catch (Exception e)
        {
            LastError = true;
            Log.Write("Календар: " + e.Message);
        }
        Changed?.Invoke();
    }
}
