using System.IO;

namespace RazerIsland.Core;

/// <summary>Ярлик на робочому столі (ярлики в «Пуск» створює інсталятор).</summary>
public static class Shortcuts
{
    public static bool DesktopExists => File.Exists(AppInfo.DesktopShortcutPath);

    public static void SetDesktop(Settings settings, bool on)
    {
        settings.DesktopShortcut = on;
        settings.Save();
        try
        {
            if (on) Create(AppInfo.DesktopShortcutPath, "", AppInfo.Description);
            else if (DesktopExists) File.Delete(AppInfo.DesktopShortcutPath);
        }
        catch (Exception e) { Log.Write("Ярлик на робочому столі: " + e.Message); }
    }

    /// <summary>Якщо ярлик увімкнено, але його немає або він веде на стару версію — перестворити.</summary>
    public static void EnsureDesktop(Settings settings)
    {
        if (!settings.DesktopShortcut) return;
        try
        {
            if (!DesktopExists || !string.Equals(ReadTarget(AppInfo.DesktopShortcutPath), AppInfo.ExePath, StringComparison.OrdinalIgnoreCase))
                Create(AppInfo.DesktopShortcutPath, "", AppInfo.Description);
        }
        catch (Exception e) { Log.Write("Ярлик на робочому столі: " + e.Message); }
    }

    private static dynamic Shell() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell недоступний"))!;

    private static string ReadTarget(string lnk) => (string)Shell().CreateShortcut(lnk).TargetPath;

    public static void Create(string lnkPath, string args, string description)
    {
        var lnk = Shell().CreateShortcut(lnkPath);
        lnk.TargetPath = AppInfo.ExePath;
        lnk.Arguments = args;
        lnk.WorkingDirectory = Path.GetDirectoryName(AppInfo.ExePath);
        lnk.IconLocation = AppInfo.ExePath + ",0";
        lnk.Description = description;
        lnk.Save();
    }
}
