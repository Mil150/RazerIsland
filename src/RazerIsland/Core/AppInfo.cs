using System.IO;
using System.Reflection;

namespace RazerIsland.Core;

/// <summary>Назва, версія і шляхи застосунку.</summary>
public static class AppInfo
{
    public const string Name = "Razer Island";
    public static string Description => L.T("Заряд Razer, годинник, погода, розклад і мікрофон Discord",
                                            "Razer battery, clock, weather, schedule and Discord microphone");

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string ExePath { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "RazerIsland.exe");

    /// <summary>Тека для налаштувань, календарів і журналу. RAZER_ISLAND_HOME — окрема тека (для тестів).</summary>
    public static string DataDir { get; } = ResolveDataDir();

    /// <summary>true, якщо тека даних задана вручну через RAZER_ISLAND_HOME.</summary>
    public static bool IsCustomDataDir { get; } = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RAZER_ISLAND_HOME"));

    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string CalendarsPath => Path.Combine(DataDir, "calendars.dat");
    /// <summary>Відкритий список посилань до 0.2.6 (і з PowerShell-версії); переноситься в calendars.dat.</summary>
    public static string LegacyCalendarsPath => Path.Combine(DataDir, "calendars.txt");
    public static string LogPath => Path.Combine(DataDir, "error.log");

    public static string SynapseLogDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Razer", "RazerAppEngine", "User Data", "Logs");

    public static string DesktopShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk");

    private static string ResolveDataDir()
    {
        var custom = Environment.GetEnvironmentVariable("RAZER_ISLAND_HOME");
        var dir = !string.IsNullOrWhiteSpace(custom)
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
