using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RazerIsland.Battery;

/// <summary>Читає заряд пристроїв із логів Razer Synapse 4.</summary>
public sealed partial class SynapseLogReader(string logDir)
{
    private sealed record ProductReading(DateTime Time, int Level, PowerState State);
    private sealed record CacheEntry(string Key, object? Value);

    // кеш розбору: файл перечитується лише коли змінився його розмір/час
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public void ClearCache() => _cache.Clear();

    public DeviceSnapshot? Read()
    {
        if (!Directory.Exists(logDir)) return null;
        var all = new DirectoryInfo(logDir).GetFiles("*.log");

        DeviceSnapshot? best = null;
        foreach (var f in all.Where(f => f.Name.StartsWith("systray_systrayv", StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(f => f.LastWriteTime).Take(4))
        {
            var r = Cached(f, ParseSystrayFile);
            if (r is not null && (best is null || r.Time > best.Time)) best = r;
        }
        if (best is null) return null;

        var latest = best.Time;
        var output = new List<RazerDevice>();
        foreach (var d in best.Devices)
        {
            var dev = d with { Time = best.Time };
            if (dev.ProductId.Length > 0)
            {
                var prefix = $"products_{dev.ProductId}_mw";
                foreach (var f in all.Where(f => f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                     .OrderByDescending(f => f.LastWriteTime).Take(2))
                {
                    var p = Cached(f, ParseProductFile);
                    if (p is not null && p.Time > dev.Time) dev = dev with { Level = p.Level, State = p.State, Time = p.Time };
                }
            }
            if (dev.Time > latest) latest = dev.Time;
            output.Add(dev);
        }
        return new DeviceSnapshot(latest, output);
    }

    private T? Cached<T>(FileInfo f, Func<FileInfo, T?> parser) where T : class
    {
        var key = $"{f.Length}:{f.LastWriteTime.Ticks}";
        if (_cache.TryGetValue(f.FullName, out var c) && c.Key == key) return (T?)c.Value;
        var v = parser(f);
        _cache[f.FullName] = new CacheEntry(key, v);
        return v;
    }

    // лог трею Synapse: повний список пристроїв із зарядом
    private static DeviceSnapshot? ParseSystrayFile(FileInfo f)
    {
        string text;
        try { text = ReadTail(f.FullName, 4L << 20); } catch { return null; }
        var lines = text.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (!line.Contains("\"powerStatus\"", StringComparison.Ordinal)) continue;
            var devs = ParseLine(line);
            if (devs.Count == 0) continue;
            return new DeviceSnapshot(GetLineTime(line), devs);
        }
        return null;
    }

    // лог конкретного пристрою (products_<id>_mw): тут заряд оновлюється найчастіше
    private static ProductReading? ParseProductFile(FileInfo f)
    {
        string text;
        try { text = ReadTail(f.FullName, 1L << 20); } catch { return null; }
        var lines = text.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (!line.Contains("\"chargingStatus\"", StringComparison.Ordinal) || line.Contains("\"powerStatus\"", StringComparison.Ordinal)) continue;
            var m = RxProductLevel().Match(line);
            if (!m.Success) continue;
            return new ProductReading(GetLineTime(line), Clamp(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)), GetState(m.Groups[1].Value));
        }
        return null;
    }

    private static List<RazerDevice> ParseLine(string line)
    {
        var devices = new List<RazerDevice>();
        var segments = line.Split("{\"serialNumber\":\"");
        for (int k = 1; k < segments.Length; k++)
        {
            var seg = segments[k];
            var ps = RxPowerStatus().Match(seg);
            if (!ps.Success) continue;
            var lv = RxLevel().Match(ps.Groups[1].Value);
            if (!lv.Success) continue;
            var cs = RxChargingStatus().Match(ps.Groups[1].Value);
            var nm = RxName().Match(seg);
            var ct = RxCategory().Match(seg);
            var pr = RxProductId().Match(seg);
            int q = seg.IndexOf('"');
            devices.Add(new RazerDevice
            {
                Serial = q > 0 ? seg[..q] : "",
                Name = nm.Success ? RxRazerPrefix().Replace(nm.Groups[1].Value, "") : "Razer",
                Category = ct.Success ? ct.Groups[1].Value.ToUpperInvariant() : "",
                Level = Clamp(int.Parse(lv.Groups[1].Value, CultureInfo.InvariantCulture)),
                State = GetState(cs.Success ? cs.Groups[1].Value : ""),
                ProductId = pr.Success ? pr.Groups[1].Value : "",
                Brand = "Razer",
            });
        }
        return devices;
    }

    public static PowerState GetState(string cs)
    {
        var s = cs.ToLowerInvariant();
        if (s == "off" || RxOff().IsMatch(s)) return PowerState.Off;
        if (RxNotCharging().IsMatch(s)) return PowerState.Normal;
        if (s.Contains("charg")) return PowerState.Charging;
        return PowerState.Normal;
    }

    private static DateTime GetLineTime(string line)
    {
        var m = RxLineTime().Match(line);
        if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return t;
        return DateTime.MinValue;
    }

    private static int Clamp(int v) => Math.Clamp(v, 0, 100);

    private static string ReadTail(string path, long maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long len = fs.Length;
        long start = Math.Max(0, len - maxBytes);
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[len - start];
        int read = 0;
        while (read < buf.Length)
        {
            int n = fs.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return Encoding.UTF8.GetString(buf, 0, read);
    }

    [GeneratedRegex("\"powerStatus\":\\{([^}]*)\\}")] private static partial Regex RxPowerStatus();
    [GeneratedRegex("\"level\":(-?\\d+)")] private static partial Regex RxLevel();
    [GeneratedRegex("\"chargingStatus\":\"([^\"]*)\"")] private static partial Regex RxChargingStatus();
    [GeneratedRegex("\"name\":\\{\"en\":\"([^\"]+)\"")] private static partial Regex RxName();
    [GeneratedRegex("\"category\":\"([A-Za-z_]+)\"")] private static partial Regex RxCategory();
    [GeneratedRegex("\"productId\":(\\d+)")] private static partial Regex RxProductId();
    [GeneratedRegex("^Razer\\s+", RegexOptions.IgnoreCase)] private static partial Regex RxRazerPrefix();
    [GeneratedRegex("\"chargingStatus\":\"([^\"]*)\",\"level\":(-?\\d+)")] private static partial Regex RxProductLevel();
    [GeneratedRegex("disconnect|offline|sleep|unknown")] private static partial Regex RxOff();
    [GeneratedRegex("nocharge|discharg|notcharg")] private static partial Regex RxNotCharging();
    [GeneratedRegex("^\\[(\\d{4}/\\d\\d/\\d\\d \\d\\d:\\d\\d:\\d\\d)")] private static partial Regex RxLineTime();
}
