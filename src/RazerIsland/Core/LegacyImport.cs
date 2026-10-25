using System.IO;

namespace RazerIsland.Core;

/// <summary>Перенесення settings.json і calendars.txt з теки PowerShell-версії віджета.</summary>
public static class LegacyImport
{
    private const string LegacyScript = "RazerBatteryWidget.ps1";

    public static bool IsLegacyLauncher(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            return dir is not null && File.Exists(Path.Combine(dir, LegacyScript));
        }
        catch { return false; }
    }

    public static void Run()
    {
        if (File.Exists(AppInfo.SettingsPath)) return;
        var dir = FindLegacyDir();
        if (dir is null) return;
        try
        {
            foreach (var name in new[] { "settings.json", "calendars.txt" })
            {
                var src = Path.Combine(dir, name);
                if (File.Exists(src)) File.Copy(src, Path.Combine(AppInfo.DataDir, name), false);
            }
            Log.Write("Імпортовано налаштування зі старої версії: " + dir);
        }
        catch (Exception e) { Log.Write("Імпорт налаштувань: " + e.Message); }
    }

    private static string? FindLegacyDir()
    {
        var candidates = new List<string>();
        var run = Autostart.Current;
        if (run is not null)
        {
            // "…\Razer Island.exe" або wscript.exe "…\Запустити.vbs"
            var p = Autostart.ExtractPath(run);
            if (p is not null) candidates.Add(Path.GetDirectoryName(p) ?? "");
            int q = run.LastIndexOf(".vbs\"", StringComparison.OrdinalIgnoreCase);
            if (q > 0)
            {
                int s = run.LastIndexOf('"', q - 1);
                if (s >= 0) candidates.Add(Path.GetDirectoryName(run[(s + 1)..(q + 4)]) ?? "");
            }
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RazerBatteryWidget"));
        return candidates.FirstOrDefault(d => d.Length > 0
            && File.Exists(Path.Combine(d, LegacyScript))
            && File.Exists(Path.Combine(d, "settings.json")));
    }
}
