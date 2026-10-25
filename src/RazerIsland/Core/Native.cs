using System.Runtime.InteropServices;

namespace RazerIsland.Core;

internal static partial class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int GWL_HWNDPARENT = -8;
    public const long WS_EX_TRANSPARENT = 0x20;
    public const long WS_EX_TOOLWINDOW = 0x80;
    public const long WS_EX_APPWINDOW = 0x40000;
    public const long WS_EX_NOACTIVATE = 0x08000000;

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindWindow(string? cls, string? name);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr h, int i);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr h);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Темний заголовок вікна (Windows 10 20H1+ / 11; на старіших нічого не робить).</summary>
    public static void UseDarkTitleBar(IntPtr hwnd)
    {
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    public static long GetLong(IntPtr h, int i) => GetWindowLongPtr(h, i).ToInt64();
    public static void SetLong(IntPtr h, int i, long v) => SetWindowLongPtr(h, i, new IntPtr(v));
}
