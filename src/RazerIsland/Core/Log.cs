using System.IO;
using System.Text;

namespace RazerIsland.Core;

/// <summary>Журнал помилок (error.log у теці даних).</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(AppInfo.LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n", Encoding.UTF8);
        }
        catch { }
    }
}
