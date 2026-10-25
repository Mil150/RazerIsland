using System.Diagnostics;
using System.Text;
using RazerIsland.Core;

namespace RazerIsland.Battery;

/// <summary>
/// Заряд бездротових пристроїв Logitech через приймачі Lightspeed / Unifying / Bolt (протокол HID++ 2.0).
/// Номери функцій і формати відповідей — за відкритим описом протоколу в проєкті Solaar.
/// shortcut: перевірено лише за описом протоколу, без живих пристроїв.
/// </summary>
public static class LogitechReader
{
    private const ushort Vid = 0x046D;
    private const byte LongReport = 0x11;
    // свій Software ID, щоб не плутати відповіді з G HUB (0x0D), Solaar (0x0B) та іншими
    private const byte SwId = 0x0C;
    private const int TimeoutMs = 250;

    private const ushort DeviceName = 0x0005, UnifiedBattery = 0x1004, BatteryStatus = 0x1000, BatteryVoltage = 0x1001, AdcMeasurement = 0x1F20;

    // напруга Li-ion → відсоток (за таблицею Solaar)
    private static readonly (int Mv, int Pct)[] VoltageCurve =
        [(4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50), (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0)];

    static LogitechReader()
    {
        Debug.Assert(VoltageToPercent(4186) == 100 && VoltageToPercent(3400) == 0 && VoltageToPercent(3835) == 55);
    }

    public static List<RazerDevice> Read()
    {
        var result = new List<RazerDevice>();
        // HID++ живе на вендорській колекції 0xFF00; довгі повідомлення (0x11) — колекція з Usage 0x0002
        foreach (var info in HidDevice.Enumerate(v => v == Vid).Where(h => h.UsagePage == 0xFF00 && h.Usage == 0x0002))
        {
            using var dev = HidDevice.Open(info);
            if (dev is null) continue;
            // 1–6 — пристрої на приймачі, 0xFF — пристрій, підключений напряму
            foreach (byte idx in new byte[] { 1, 2, 3, 4, 5, 6, 0xFF })
                if (ReadDevice(dev, idx) is { } d)
                    result.Add(d with { Serial = $"logi:{info.Pid:X4}:{idx:X2}" });
        }
        return result;
    }

    private static RazerDevice? ReadDevice(HidDevice dev, byte idx)
    {
        (int Level, bool Charging)? battery = null;
        foreach (var f in new[] { UnifiedBattery, BatteryStatus, BatteryVoltage, AdcMeasurement })
        {
            // немає відповіді — на цьому індексі нікого (приймач відповідає помилкою на короткій колекції, тож чекаємо таймаут один раз)
            if (FeatureIndex(dev, idx, f) is not { } fi) return null;
            if (fi == 0) continue;
            battery = f switch
            {
                UnifiedBattery => Call(dev, idx, fi, 1) is { } r ? Unified(r) : null,
                BatteryStatus => Call(dev, idx, fi, 0) is { } r && r[0] > 0 ? (r[0], r[2] is 1 or 2 or 4) : null,
                BatteryVoltage => Call(dev, idx, fi, 0) is { } r ? (VoltageToPercent(r[0] << 8 | r[1]), (r[2] & 0x80) != 0) : null,
                _ => Call(dev, idx, fi, 0) is { } r && (r[2] & 0x01) != 0 ? (VoltageToPercent(r[0] << 8 | r[1]), (r[2] & 0x02) != 0) : null,
            };
            if (battery is not null) break;
        }
        if (battery is not { } b) return null;

        string name = "Logitech", category = "";
        if (FeatureIndex(dev, idx, DeviceName) is { } ni and > 0)
        {
            name = ReadName(dev, idx, ni) ?? name;
            if (Call(dev, idx, ni, 2) is { } k)
                category = k[0] switch { 0 => "KEYBOARD", 3 or 4 or 5 => "MOUSE", 8 => "HEADSET", 0x0B or 0x0C => "GAMEPAD", _ => "" };
        }
        return new RazerDevice
        {
            Serial = "",
            Name = name.StartsWith("Logi", StringComparison.OrdinalIgnoreCase) ? name : "Logitech " + name,
            Category = category,
            Brand = "Logitech",
            Level = Math.Clamp(b.Level, 0, 100),
            State = b.Charging ? PowerState.Charging : PowerState.Normal,
            Time = DateTime.Now,
        };
    }

    private static (int, bool)? Unified(byte[] r)
    {
        int level = r[0] > 0 ? r[0] : r[1] switch { 8 => 100, 4 => 50, 2 => 20, 1 => 5, _ => 0 };
        return (level, r[2] is 1 or 2);
    }

    private static string? ReadName(HidDevice dev, byte idx, byte fi)
    {
        if (Call(dev, idx, fi, 0) is not { } len || len[0] == 0) return null;
        var name = new List<byte>();
        while (name.Count < len[0])
        {
            if (Call(dev, idx, fi, 1, (byte)name.Count) is not { } part) return null;
            name.AddRange(part.Take(len[0] - name.Count));
        }
        return Encoding.UTF8.GetString(name.ToArray()).TrimEnd('\0').Trim();
    }

    // IRoot.GetFeature: номер функції в таблиці пристрою; 0 — функції немає, null — пристрій не відповідає
    private static byte? FeatureIndex(HidDevice dev, byte idx, ushort feature) =>
        Call(dev, idx, 0, 0, (byte)(feature >> 8), (byte)feature)?[0];

    /// <summary>HID++ 2.0 виклик; повертає параметри відповіді (16 байт) або null (помилка / таймаут).</summary>
    private static byte[]? Call(HidDevice dev, byte idx, byte featureIndex, int function, params byte[] args)
    {
        byte fnSw = (byte)(function << 4 | SwId);
        var req = new byte[20];
        req[0] = LongReport; req[1] = idx; req[2] = featureIndex; req[3] = fnSw;
        args.CopyTo(req, 4);
        if (!dev.Write(req)) return null;
        var deadline = Environment.TickCount64 + TimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var r = dev.Read((int)Math.Max(1, deadline - Environment.TickCount64));
            if (r is null || r.Length < 7 || r[0] != LongReport) continue;
            // Bluetooth-пристрої відповідають з індексом 0x00 замість 0xFF
            if (r[1] != idx && r[1] != (idx ^ 0xFF)) continue;
            if (r[2] == 0xFF && r[3] == featureIndex && r[4] == fnSw) return null; // помилка HID++ 2.0
            if (r[2] == featureIndex && r[3] == fnSw) return r[4..];
        }
        return null;
    }

    private static int VoltageToPercent(int mv)
    {
        if (mv >= VoltageCurve[0].Mv) return 100;
        for (int i = 1; i < VoltageCurve.Length; i++)
        {
            var (hiMv, hiPct) = VoltageCurve[i - 1];
            var (loMv, loPct) = VoltageCurve[i];
            if (mv >= loMv) return loPct + (mv - loMv) * (hiPct - loPct) / (hiMv - loMv);
        }
        return 0;
    }
}
