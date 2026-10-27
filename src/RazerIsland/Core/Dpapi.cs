using System.Runtime.InteropServices;
using System.Text;

namespace RazerIsland.Core;

/// <summary>Шифрування рядків ключем поточного користувача Windows (DPAPI). Захищає файли, а не пам'ять.</summary>
public static partial class Dpapi
{
    public const string Prefix = "dpapi1:";
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RazerIsland/v1");

    /// <summary>Код Win32 останньої невдачі (0 — дані не того формату).</summary>
    public static int LastError { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DATA_BLOB dataIn, string? description, ref DATA_BLOB entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr description, ref DATA_BLOB entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr mem);

    /// <summary>"" → ""; інакше Prefix + Base64. Невдача DPAPI — виняток (відкритий текст не повертаємо ніколи).</summary>
    public static string Protect(string plain)
    {
        if (plain.Length == 0) return "";
        var bytes = Run(Encoding.UTF8.GetBytes(plain), protect: true)
            ?? throw new InvalidOperationException($"CryptProtectData: код {LastError}");
        return Prefix + Convert.ToBase64String(bytes);
    }

    /// <summary>"" або null → true і "". Немає префікса, битий Base64 чи чужий ключ → false.</summary>
    public static bool TryUnprotect(string? s, out string plain)
    {
        plain = "";
        if (string.IsNullOrEmpty(s)) return true;
        LastError = 0;
        if (!s.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        byte[] data;
        try { data = Convert.FromBase64String(s[Prefix.Length..].Trim()); }
        catch (FormatException) { return false; }
        if (Run(data, protect: false) is not { } bytes) return false;
        plain = Encoding.UTF8.GetString(bytes);
        return true;
    }

    private static byte[]? Run(byte[] input, bool protect)
    {
        var hIn = GCHandle.Alloc(input, GCHandleType.Pinned);
        var hEnt = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var dataOut = default(DATA_BLOB);
        try
        {
            var dataIn = new DATA_BLOB { cbData = input.Length, pbData = hIn.AddrOfPinnedObject() };
            var ent = new DATA_BLOB { cbData = Entropy.Length, pbData = hEnt.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref dataIn, null, ref ent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out dataOut)
                : CryptUnprotectData(ref dataIn, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out dataOut);
            if (!ok) { LastError = Marshal.GetLastPInvokeError(); return null; }
            var result = new byte[dataOut.cbData];
            Marshal.Copy(dataOut.pbData, result, 0, dataOut.cbData);
            return result;
        }
        finally
        {
            if (dataOut.pbData != IntPtr.Zero) LocalFree(dataOut.pbData);
            hIn.Free();
            hEnt.Free();
        }
    }
}
