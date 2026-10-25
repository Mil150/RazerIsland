using System.Globalization;
using System.Text.RegularExpressions;

namespace RazerIsland.Calendar;

/// <summary>Розгортання RRULE (DAILY/WEEKLY/MONTHLY/YEARLY) у «настінні» часи початку в межах вікна.</summary>
internal static partial class RRule
{
    private static readonly Dictionary<string, int> DayMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SU"] = 0, ["MO"] = 1, ["TU"] = 2, ["WE"] = 3, ["TH"] = 4, ["FR"] = 5, ["SA"] = 6,
    };

    public static List<DateTime> Expand(DateTime start, string rule, TimeZoneInfo? tz, DateTime winFrom, DateTime winTo)
    {
        var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in rule.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2) p[kv[0].ToUpperInvariant()] = kv[1];
        }
        string Get(string k) => p.TryGetValue(k, out var v) ? v : "";

        var freq = Get("FREQ").ToUpperInvariant();
        int interval = Get("INTERVAL") is { Length: > 0 } iv ? Math.Max(1, int.Parse(iv, CultureInfo.InvariantCulture)) : 1;
        int count = Get("COUNT") is { Length: > 0 } cv ? int.Parse(cv, CultureInfo.InvariantCulture) : int.MaxValue;
        var until = DateTime.MaxValue;
        if (Get("UNTIL") is { Length: > 0 } uv && IcsParser.ParseDate("", uv) is { } u)
        {
            if (u.AllDay) until = u.Wall.AddDays(1).AddSeconds(-1);
            else if (u.Utc && tz is not null && tz != TimeZoneInfo.Utc)
            {
                try { until = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(u.Wall, DateTimeKind.Utc), tz); }
                catch { until = u.Wall; }
            }
            else until = u.Wall;
        }

        var lo = winFrom.AddDays(-2);
        var hi = winTo.AddDays(2);
        var tod = start.TimeOfDay;
        var output = new List<DateTime>();
        int n = 0, guard = 0;
        bool stop = false;

        switch (freq)
        {
            case "DAILY":
            {
                var d = start;
                while (guard < 200000)
                {
                    if (d > until || n >= count || d > hi) break;
                    n++;
                    if (d >= lo) output.Add(d);
                    d = d.AddDays(interval); guard++;
                }
                break;
            }
            case "WEEKLY":
            {
                var dows = new List<int>();
                foreach (var t in Get("BYDAY").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (DayMap.TryGetValue(t[Math.Max(0, t.Length - 2)..], out var dw)) dows.Add(dw);
                if (dows.Count == 0) dows.Add((int)start.DayOfWeek);
                var offs = dows.Select(x => (x + 6) % 7).Distinct().Order().ToList();
                var weekStart = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7));
                for (int k = 0; !stop && guard < 200000; k++, guard++)
                {
                    var ws = weekStart.AddDays(7 * interval * k);
                    if (ws > hi || ws > until) break;
                    foreach (var o in offs)
                    {
                        var occ = ws.AddDays(o) + tod;
                        if (occ < start) continue;
                        if (occ > until || n >= count) { stop = true; break; }
                        n++;
                        if (occ >= lo && occ <= hi) output.Add(occ);
                    }
                }
                break;
            }
            case "MONTHLY":
            {
                var first = start.Date.AddDays(1 - start.Day);
                for (int k = 0; !stop && guard < 5000; k++, guard++)
                {
                    var b = first.AddMonths(interval * k);
                    if (b > hi || b > until) break;
                    int dim = DateTime.DaysInMonth(b.Year, b.Month);
                    var cands = new List<DateTime>();
                    if (Get("BYDAY").Length > 0)
                    {
                        foreach (var t in Get("BYDAY").Split(','))
                        {
                            var m = RxByDay().Match(t);
                            if (!m.Success) continue;
                            int ord = m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                            int dw = DayMap[m.Groups[2].Value];
                            var days = Enumerable.Range(0, dim).Select(i => b.AddDays(i)).Where(dd => (int)dd.DayOfWeek == dw).ToList();
                            if (ord > 0 && ord <= days.Count) cands.Add(days[ord - 1]);
                            else if (ord < 0 && -ord <= days.Count) cands.Add(days[days.Count + ord]);
                            else if (ord == 0) cands.AddRange(days);
                        }
                    }
                    else if (Get("BYMONTHDAY").Length > 0)
                    {
                        foreach (var t in Get("BYMONTHDAY").Split(','))
                        {
                            if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int md)) continue;
                            if (md < 0) md = dim + md + 1;
                            if (md >= 1 && md <= dim) cands.Add(b.AddDays(md - 1));
                        }
                    }
                    else if (start.Day <= dim) cands.Add(b.AddDays(start.Day - 1));

                    foreach (var c in cands.Order())
                    {
                        var occ = c + tod;
                        if (occ < start) continue;
                        if (occ > until || n >= count) { stop = true; break; }
                        n++;
                        if (occ >= lo && occ <= hi) output.Add(occ);
                    }
                }
                break;
            }
            case "YEARLY":
            {
                for (int k = 0; guard < 500; k++, guard++)
                {
                    var occ = start.AddYears(interval * k);
                    if (occ > until || n >= count || occ > hi) break;
                    n++;
                    if (occ >= lo) output.Add(occ);
                }
                break;
            }
        }
        return output;
    }

    [GeneratedRegex("^([+-]?\\d+)?(MO|TU|WE|TH|FR|SA|SU)$", RegexOptions.IgnoreCase)] private static partial Regex RxByDay();
}
