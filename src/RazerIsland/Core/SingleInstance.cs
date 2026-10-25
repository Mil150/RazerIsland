using System.Diagnostics;
using System.Management;

namespace RazerIsland.Core;

/// <summary>
/// Один екземпляр: повторний запуск закриває старий і займає його місце.
/// Старий екземпляр отримує сигнал і завершується сам (іконки трею прибираються);
/// якщо не відповідає — процес закривається примусово.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // те саме ім'я, що й у PowerShell-версії — щоб нова версія замінила стару
    private const string BaseName = @"Local\RazerBatteryWidget_mil";

    private Mutex? _mutex;
    private EventWaitHandle? _quit;
    private RegisteredWaitHandle? _wait;

    /// <summary>Інший екземпляр попросив цей завершитися (викликається з фонового потоку).</summary>
    public event Action? QuitRequested;

    private static string MutexName =>
        AppInfo.IsCustomDataDir ? BaseName + "_" + StableHash(AppInfo.DataDir.ToLowerInvariant()) : BaseName;

    private static string QuitName => MutexName + "_quit";

    public static SingleInstance? Acquire()
    {
        var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            Log.Write("Знайдено запущений екземпляр — перезапускаю.");
            bool graceful = EventWaitHandle.TryOpenExisting(QuitName, out var other);
            if (graceful) using (other) other!.Set();
            else if (!AppInfo.IsCustomDataDir) KillLegacy();

            bool got = Wait(mutex, 5000);
            if (!got && graceful)
            {
                KillSameExe();
                got = Wait(mutex, 3000);
            }
            if (!got)
            {
                Log.Write("Старий екземпляр не закрився — вихід.");
                mutex.Dispose();
                return null;
            }
        }

        var inst = new SingleInstance { _mutex = mutex };
        inst._quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitName);
        inst._wait = ThreadPool.RegisterWaitForSingleObject(inst._quit, (_, _) => inst.QuitRequested?.Invoke(), null, -1, true);
        return inst;
    }

    private static bool Wait(Mutex m, int ms)
    {
        try { return m.WaitOne(ms); }
        catch (AbandonedMutexException) { return true; }
    }

    // string.GetHashCode() у .NET відрізняється між процесами
    private static string StableHash(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..12];

    private static void KillSameExe()
    {
        int self = Environment.ProcessId;
        try
        {
            foreach (var p in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
                using (p)
                    if (p.Id != self) { Log.Write($"Закриваю старий процес {p.Id}"); p.Kill(); }
        }
        catch (Exception e) { Log.Write("Не вдалося закрити старий процес: " + e.Message); }
    }

    // стара PowerShell-версія віджета сигналу не слухає
    private static void KillLegacy()
    {
        try
        {
            using var q = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='powershell.exe'");
            foreach (ManagementObject mo in q.Get())
                using (mo)
                {
                    var cmd = mo["CommandLine"] as string ?? "";
                    if (!cmd.Contains("RazerBatteryWidget.ps1", StringComparison.OrdinalIgnoreCase)) continue;
                    int pid = Convert.ToInt32(mo["ProcessId"]);
                    Log.Write($"Закриваю PowerShell-версію віджета {pid}");
                    try { using var p = Process.GetProcessById(pid); p.Kill(); } catch { }
                }
        }
        catch (Exception e) { Log.Write("Не вдалося знайти PowerShell-версію: " + e.Message); }
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _wait = null;
        _quit?.Dispose();
        _quit = null;
        if (_mutex is null) return;
        try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
        _mutex = null;
    }
}
