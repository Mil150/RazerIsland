using RazerIsland.Core;

namespace RazerIsland.Battery;

/// <summary>
/// Заряд бездротових гарнітур SteelSeries Arctis через їхній USB-приймач (HID).
/// Запити й розташування байтів — за відкритим описом протоколу в проєкті HeadsetControl.
/// shortcut: моделі перевірено лише за описом протоколу, без живих пристроїв; нова модель — новий рядок у Models.
/// </summary>
public static class SteelSeriesReader
{
    private const ushort Vid = 0x1038;

    // Результат розбору відповіді: null — гарнітура вимкнена або не підключена до приймача
    private delegate (int Level, bool Charging)? Parse(byte[] r);

    private sealed record Model(string Name, int Interface, ushort UsagePage, byte[] Request, Parse Parse, params ushort[] Pids);

    private static int Map(int v, int min, int max) => Math.Clamp((v - min) * 100 / (max - min), 0, 100);
    private static byte At(byte[] r, int i) => i < r.Length ? r[i] : (byte)0;

    private static readonly byte[] Status = [0x00, 0xB0];

    private static readonly Model[] Models =
    [
        new("Arctis 1 / 7X / 7P", 3, 0xFF43, [0x06, 0x12], r => At(r, 2) == 0x01 ? null : (At(r, 3), false),
            0x12B3, 0x12B6, 0x12D7, 0x12D5),
        new("Arctis 7 / Pro", 5, 0, [0x06, 0x18], r => (At(r, 2), false),
            0x1260, 0x12AD, 0x1252, 0x1280),
        new("Arctis 7+", 3, 0xFFC0, Status, r => At(r, 1) == 0x01 ? null : (Map(At(r, 2), 0, 4), At(r, 3) == 0x01),
            0x220E, 0x2212, 0x2216, 0x2236),
        new("Arctis 9", 0, 0, [0x00, 0x20], r => (Map(At(r, 3), 0x64, 0x9A), At(r, 4) == 0x01),
            0x12C2, 0x12C4),
        new("Arctis Nova 5", 3, 0xFFC0, Status, r => At(r, 1) == 0x02 ? null : (Math.Min(100, (int)At(r, 3)), At(r, 4) == 0x01),
            0x2232, 0x2253),
        new("Arctis Nova 3P / 3X", 3, 0xFFC0, Status, r => At(r, 1) == 0x02 ? null : (Math.Min(100, (int)At(r, 3)), At(r, 4) == 0x01),
            0x2269, 0x226D),
        // старі прошивки Nova 7 дають рівень 0–4
        new("Arctis Nova 7", 3, 0xFFC0, Status, r => At(r, 3) == 0x00 ? null : (Map(At(r, 2), 0, 4), At(r, 3) is 0x01 or 0x02),
            0x2202, 0x2206, 0x220A, 0x223A, 0x227A, 0x22A4),
        new("Arctis Nova 7", 3, 0xFFC0, Status, r => At(r, 3) == 0x00 ? null : (Math.Min(100, (int)At(r, 2)), At(r, 3) is 0x01 or 0x02),
            0x22A1, 0x227E, 0x2258, 0x229E, 0x22AD, 0x22A9, 0x22A5, 0x22A7, 0x2298),
        new("Arctis Nova Pro Wireless", 4, 0, [0x06, 0xB0], r => At(r, 15) == 0x01 ? null : (Map(At(r, 6), 0, 8), At(r, 15) == 0x02),
            0x12E0, 0x12E5),
        // навушники в кейсі показують 0 % — беремо лише ті, що зовні (статус 0x03)
        new("Arctis GameBuds", 3, 0xFFC0, Status, r =>
        {
            int? level = null;
            if (At(r, 3) == 0x03) level = At(r, 5);
            if (At(r, 4) == 0x03) level = Math.Min(level ?? 100, At(r, 6));
            return level is { } l ? (Math.Min(100, l), false) : null;
        }, 0x230A),
    ];

    public static List<RazerDevice> Read()
    {
        var result = new List<RazerDevice>();
        var hids = HidDevice.Enumerate(v => v == Vid);
        foreach (var model in Models)
            foreach (var info in hids.Where(h => model.Pids.Contains(h.Pid) && h.Interface == model.Interface
                                                 && (model.UsagePage == 0 || h.UsagePage == model.UsagePage)))
            {
                if (result.Any(d => d.Serial == $"ss:{info.Pid:X4}")) continue;
                using var dev = HidDevice.Open(info);
                if (dev is null || !dev.Write(model.Request)) continue;
                var reply = dev.Read(500);
                if (reply is null) continue;
                // null — гарнітура вимкнена: не додаємо, і віджет покаже останній відомий заряд сірим
                if (model.Parse(reply) is not { } parsed) continue;
                result.Add(new RazerDevice
                {
                    Serial = $"ss:{info.Pid:X4}",
                    Name = "SteelSeries " + model.Name,
                    Category = "HEADSET",
                    Brand = "SteelSeries",
                    Level = parsed.Level,
                    State = parsed.Charging ? PowerState.Charging : PowerState.Normal,
                    Time = DateTime.Now,
                });
            }
        return result;
    }
}
