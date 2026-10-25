using System.IO;
using Microsoft.Win32;

namespace RazerIsland.Core;

/// <summary>Автозапуск через HKCU\...\Run (той самий запис, що й у PowerShell-версії).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "RazerBatteryWidget";

    private static string Command => $"\"{AppInfo.ExePath}\"";

    public static string? Current
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(RunName) as string;
            }
            catch { return null; }
        }
    }

    public static bool IsEnabled => Current is not null;

    public static void Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(RunName, Command);
            else k.DeleteValue(RunName, false);
        }
        catch (Exception e) { Log.Write("Автозапуск: " + e.Message); }
    }

    /// <summary>Перший запуск — увімкнути; запис від старої версії чи неіснуючого файлу — перевести на цей exe.</summary>
    public static void Ensure(Settings settings)
    {
        if (!settings.AutostartSet)
        {
            Set(true);
            settings.AutostartSet = true;
            settings.Save();
            return;
        }
        var cur = Current;
        if (cur is null) return;
        var target = ExtractPath(cur);
        bool stale = target is null || !File.Exists(target) || LegacyImport.IsLegacyLauncher(target);
        if (stale && !string.Equals(target, AppInfo.ExePath, StringComparison.OrdinalIgnoreCase))
        {
            Log.Write($"Автозапуск вказував на {cur} — оновлюю.");
            Set(true);
        }
    }

    /// <summary>Шлях до програми з командного рядка запису Run.</summary>
    public static string? ExtractPath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        int sp = command.IndexOf(' ');
        return sp > 0 ? command[..sp] : command;
    }
}
