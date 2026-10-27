using System.IO;
using System.Text;
using RazerIsland.Core;

namespace RazerIsland.Calendar;

/// <summary>Список iCal-посилань (calendars.txt) і завантажені з них події.</summary>
public sealed class CalendarService
{
    // словник без локів: продовження await повертаються на UI-потік, тож не додавати ConfigureAwait(false)
    private readonly Dictionary<string, CalendarSource> _sources = new();
    private List<string> _urls = [];
    private int _gen;

    /// <summary>Джерела в порядку calendars.txt.</summary>
    public IReadOnlyList<CalendarSource> Sources => _urls.Where(_sources.ContainsKey).Select(u => _sources[u]).ToList();
    /// <summary>Події з джерел, які оновлювалися не пізніше <see cref="CalendarSource.StaleAfter"/> тому.</summary>
    public IEnumerable<CalendarEvent> AllEvents => _sources.Values.Where(s => !s.IsStale).SelectMany(s => s.Events);
    public int OkCount => _sources.Values.Count(s => s.Status == CalStatus.Ok);
    public IReadOnlyList<CalendarSource> Failed => Sources.Where(s => s.Status == CalStatus.Error).ToList();
    public bool AllFailed => _sources.Count > 0 && _sources.Values.All(s => s.Status == CalStatus.Error);

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
        _sources.Clear();
        _gen++; // завантаження, що ще йдуть, свій результат уже не запишуть
        Refresh();
    }

    public void Refresh()
    {
        _urls = GetUrls().Distinct().ToList();
        foreach (var k in _sources.Keys.Except(_urls).ToList()) _sources.Remove(k);
        if (_urls.Count == 0) { Changed?.Invoke(); return; }
        foreach (var u in _urls)
        {
            if (!_sources.TryGetValue(u, out var s)) _sources[u] = s = new CalendarSource { Url = u };
            if (!s.Busy) _ = LoadAsync(s);
        }
    }

    private async Task LoadAsync(CalendarSource s)
    {
        int gen = _gen;
        s.Busy = true;
        CalError error;
        string detail;
        try
        {
            using var resp = await Http.Client.GetAsync(s.Url);
            var ics = resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync() : "";
            if (!resp.IsSuccessStatusCode) (error, detail) = (CalendarSource.Classify(resp.StatusCode), $"HTTP {(int)resp.StatusCode}");
            else if (!ics.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)) (error, detail) = (CalError.NotIcs, "немає BEGIN:VCALENDAR");
            else
            {
                var from = DateTime.Today;
                var events = await Task.Run(() => IcsParser.Parse(ics, from, from.AddDays(8)));
                if (gen != _gen || _sources.GetValueOrDefault(s.Url) != s) return;
                s.Events = events.Select(e => e with { Calendar = s.Url }).ToList();
                // назва календаря, як у Google (iCal не передає кольорів — їх задають у налаштуваннях)
                var name = System.Text.RegularExpressions.Regex.Match(ics, @"^X-WR-CALNAME:(.+?)\r?$", System.Text.RegularExpressions.RegexOptions.Multiline);
                if (name.Success) s.Name = name.Groups[1].Value.Trim();
                (s.Status, s.Error, s.LastSuccess) = (CalStatus.Ok, CalError.None, DateTime.Now);
                Changed?.Invoke();
                return;
            }
        }
        catch (Exception e) { (error, detail) = (CalendarSource.Classify(e), e.Message); }
        finally { s.Busy = false; }
        if (gen != _gen || _sources.GetValueOrDefault(s.Url) != s) return;
        // події й LastSuccess не чіпаємо — це кеш до StaleAfter
        (s.Status, s.Error) = (CalStatus.Error, error);
        // без повного URL: він приватний
        var host = Uri.TryCreate(s.Url, UriKind.Absolute, out var uri) ? uri.Host : "?";
        Log.Write($"Календар «{s.Name ?? host}»: {error} — {detail}");
        Changed?.Invoke();
    }
}
