using System.Globalization;
using System.Text.RegularExpressions;

namespace RazerIsland.Calendar;

/// <summary>Розбір iCal (секретна адреса Google Календаря) з повтореннями й винятками.</summary>
public static partial class IcsParser
{
    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    /// <summary>Дата/час з iCal: «настінний» час у своєму поясі.</summary>
    internal sealed record IcsDate(DateTime Wall, bool AllDay, TimeZoneInfo? Tz, bool Utc);

    private sealed class RawEvent
    {
        public IcsDate? Start, End, RecId;
        public TimeSpan? Duration;
        public TimeSpan Dur;
        public string Title = "", Location = "", RRule = "", Uid = "", Status = "";
        public readonly List<DateTime> ExDates = [];
    }

    // Windows-ідентифікатори для IANA-поясів, яких може не бути в системі
    private static readonly Dictionary<string, string> TzMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Europe/Kiev"] = "FLE Standard Time", ["Europe/Kyiv"] = "FLE Standard Time", ["Europe/Helsinki"] = "FLE Standard Time",
        ["Europe/Riga"] = "FLE Standard Time", ["Europe/Tallinn"] = "FLE Standard Time", ["Europe/Vilnius"] = "FLE Standard Time",
        ["Europe/Sofia"] = "FLE Standard Time", ["Europe/Stockholm"] = "W. Europe Standard Time", ["Europe/Berlin"] = "W. Europe Standard Time",
        ["Europe/Oslo"] = "W. Europe Standard Time", ["Europe/Warsaw"] = "Central European Standard Time", ["Europe/Paris"] = "Romance Standard Time",
        ["Europe/London"] = "GMT Standard Time", ["Europe/Moscow"] = "Russian Standard Time", ["UTC"] = "UTC", ["Etc/UTC"] = "UTC", ["Etc/GMT"] = "UTC",
    };
    private static readonly Dictionary<string, TimeZoneInfo> TzCache = new();
    private static readonly object TzGate = new();

    private static TimeZoneInfo GetTz(string id)
    {
        if (id.Length == 0) return TimeZoneInfo.Local;
        lock (TzGate)
        {
            if (TzCache.TryGetValue(id, out var cached)) return cached;
            var tz = TimeZoneInfo.Local;
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch
            {
                if (TzMap.TryGetValue(id, out var win))
                    try { tz = TimeZoneInfo.FindSystemTimeZoneById(win); } catch { }
            }
            TzCache[id] = tz;
            return tz;
        }
    }

    internal static IcsDate? ParseDate(string parameters, string val)
    {
        val = val.Trim();
        if (RxDate().IsMatch(val))
            return new IcsDate(DateTime.ParseExact(val, "yyyyMMdd", CI), true, null, false);
        var m = RxUtc().Match(val);
        if (m.Success)
            return new IcsDate(DateTime.ParseExact(m.Groups[1].Value, "yyyyMMdd'T'HHmmss", CI), false, TimeZoneInfo.Utc, true);
        if (RxLocal().IsMatch(val))
        {
            var tz = RxTzid().Match(parameters);
            return new IcsDate(DateTime.ParseExact(val, "yyyyMMdd'T'HHmmss", CI), false, GetTz(tz.Success ? tz.Groups[1].Value : ""), false);
        }
        return null;
    }

    internal static DateTime ToLocal(DateTime wall, TimeZoneInfo? tz, bool allDay)
    {
        if (allDay) return wall;
        var w = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        try
        {
            if (tz is null) return w;
            if (tz == TimeZoneInfo.Utc) return TimeZoneInfo.ConvertTimeFromUtc(w, TimeZoneInfo.Local);
            return TimeZoneInfo.ConvertTime(w, tz, TimeZoneInfo.Local);
        }
        catch { return w; }
    }

    private static TimeSpan ParseDuration(string v)
    {
        var m = RxDuration().Match(v);
        if (!m.Success) return TimeSpan.Zero;
        int N(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CI) : 0;
        return new TimeSpan(N(2) * 7 + N(3), N(4), N(5), N(6));
    }

    private static string Unescape(string v) =>
        RxNewline().Replace(v, " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();

    public static List<CalendarEvent> Parse(string text, DateTime from, DateTime to)
    {
        var raw = text.Replace("\r\n ", "").Replace("\r\n\t", "").Replace("\n ", "").Replace("\n\t", "");
        var events = new List<RawEvent>();
        RawEvent? cur = null;
        foreach (var l in RxLines().Split(raw))
        {
            if (l == "BEGIN:VEVENT") { cur = new RawEvent(); continue; }
            if (l == "END:VEVENT") { if (cur is not null) events.Add(cur); cur = null; continue; }
            if (cur is null) continue;
            var m = RxProp().Match(l);
            if (!m.Success) continue;
            var name = m.Groups[1].Value.ToUpperInvariant();
            var parameters = m.Groups[2].Value;
            var val = m.Groups[3].Value;
            switch (name)
            {
                case "DTSTART": cur.Start = ParseDate(parameters, val); break;
                case "DTEND": cur.End = ParseDate(parameters, val); break;
                case "DURATION": cur.Duration = ParseDuration(val); break;
                case "SUMMARY": cur.Title = Unescape(val); break;
                case "LOCATION": cur.Location = Unescape(val); break;
                case "RRULE": cur.RRule = val; break;
                case "EXDATE":
                    foreach (var v in val.Split(','))
                        if (ParseDate(parameters, v) is { } d) cur.ExDates.Add(ToLocal(d.Wall, d.Tz, d.AllDay));
                    break;
                case "RECURRENCE-ID": cur.RecId = ParseDate(parameters, val); break;
                case "UID": cur.Uid = val; break;
                case "STATUS": cur.Status = val.ToUpperInvariant(); break;
            }
        }

        // перенесені/змінені окремі повторення серії
        var overrides = new Dictionary<string, List<DateTime>>();
        foreach (var e in events)
        {
            if (e.Start is null) continue;
            if (e.End is not null) e.Dur = e.End.Wall - e.Start.Wall;
            else if (e.Duration is { } dur) e.Dur = dur;
            else if (e.Start.AllDay) e.Dur = TimeSpan.FromDays(1);
            else e.Dur = TimeSpan.Zero;
            if (e.RecId is not null)
            {
                if (!overrides.TryGetValue(e.Uid, out var list)) overrides[e.Uid] = list = [];
                list.Add(ToLocal(e.RecId.Wall, e.RecId.Tz, e.RecId.AllDay));
            }
        }

        var result = new List<CalendarEvent>();
        foreach (var e in events)
        {
            if (e.Start is null || e.Status == "CANCELLED") continue;
            bool ad = e.Start.AllDay;
            bool series = e.RRule.Length > 0 && e.RecId is null;
            var walls = series ? RRule.Expand(e.Start.Wall, e.RRule, e.Start.Tz, from, to) : [e.Start.Wall];
            foreach (var wall in walls)
            {
                var s = ToLocal(wall, e.Start.Tz, ad);
                if (series)
                {
                    if (e.ExDates.Contains(s)) continue;
                    if (overrides.TryGetValue(e.Uid, out var ov) && ov.Contains(s)) continue;
                }
                var en = ad ? s + e.Dur : ToLocal(wall + e.Dur, e.Start.Tz, false);
                if ((en > from || s >= from) && s < to)
                    result.Add(new CalendarEvent(s, en, ad, e.Title, e.Location));
            }
        }
        return result;
    }

    [GeneratedRegex(@"^\d{8}$")] private static partial Regex RxDate();
    [GeneratedRegex(@"^(\d{8}T\d{6})Z$")] private static partial Regex RxUtc();
    [GeneratedRegex(@"^\d{8}T\d{6}$")] private static partial Regex RxLocal();
    [GeneratedRegex("TZID=\"?([^\";:]+)", RegexOptions.IgnoreCase)] private static partial Regex RxTzid();
    [GeneratedRegex(@"^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$")] private static partial Regex RxDuration();
    [GeneratedRegex(@"\\[nN]")] private static partial Regex RxNewline();
    [GeneratedRegex("\r?\n")] private static partial Regex RxLines();
    [GeneratedRegex("^([A-Za-z\\-]+)((?:;[^:;=]+=(?:\"[^\"]*\"|[^:;]*))*):(.*)$")] private static partial Regex RxProp();
}
