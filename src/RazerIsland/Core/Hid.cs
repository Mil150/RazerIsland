using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RazerIsland.Core;

/// <summary>HID-інтерфейс пристрою (одна HID-колекція = окремий шлях у Windows).</summary>
public sealed record HidInfo(string Path, ushort Vid, ushort Pid, ushort UsagePage, ushort Usage, int InputLength, int OutputLength)
{
    /// <summary>Номер USB-інтерфейсу з шляху ("…&amp;mi_03…"), -1 якщо немає.</summary>
    public int Interface
    {
        get
        {
            int i = Path.IndexOf("&mi_", StringComparison.OrdinalIgnoreCase);
            return i >= 0 && int.TryParse(Path.AsSpan(i + 4, 2), System.Globalization.NumberStyles.HexNumber, null, out var n) ? n : -1;
        }
    }
}

/// <summary>Мінімальний доступ до HID: перелік, запис вихідного звіту, читання вхідного з таймаутом.</summary>
public sealed class HidDevice : IDisposable
{
    private readonly FileStream _stream;
    public HidInfo Info { get; }

    private HidDevice(HidInfo info, FileStream stream) { Info = info; _stream = stream; }

    public static List<HidInfo> Enumerate(Func<ushort, bool> vidFilter)
    {
        var result = new List<HidInfo>();
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return result;
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                var path = InterfacePath(set, ref data);
                if (path is null) continue;
                // без прав читання/запису — так можна відкрити навіть системні клавіатури й мишки
                using var h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) continue;
                var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                if (!HidD_GetAttributes(h, ref attr) || !vidFilter(attr.VendorID)) continue;
                if (!HidD_GetPreparsedData(h, out var pre)) continue;
                try
                {
                    if (HidP_GetCaps(pre, out var caps) != HIDP_STATUS_SUCCESS) continue;
                    result.Add(new HidInfo(path, attr.VendorID, attr.ProductID, caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength));
                }
                finally { HidD_FreePreparsedData(pre); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    public static HidDevice? Open(HidInfo info)
    {
        var h = CreateFileW(info.Path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        return new HidDevice(info, new FileStream(h, FileAccess.ReadWrite, 0, isAsync: true));
    }

    /// <summary>Перший байт — номер звіту (0, якщо пристрій їх не нумерує); доповнюється нулями до довжини звіту.</summary>
    public bool Write(ReadOnlySpan<byte> report)
    {
        if (Info.OutputLength <= 0 || report.Length > Info.OutputLength) return false;
        var buf = new byte[Info.OutputLength];
        report.CopyTo(buf);
        try { _stream.Write(buf); return true; }
        catch (IOException) { return false; }
    }

    /// <summary>Вхідний звіт або null після таймауту. Ненумерований звіт (номер 0) повертається без цього байта.</summary>
    public byte[]? Read(int timeoutMs)
    {
        if (Info.InputLength <= 0) return null;
        var buf = new byte[Info.InputLength];
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            int n = _stream.ReadAsync(buf, cts.Token).AsTask().GetAwaiter().GetResult();
            if (n <= 0) return null;
            return buf[0] == 0 ? buf[1..n] : buf[..n];
        }
        catch (OperationCanceledException) { return null; }
        catch (IOException) { return null; }
    }

    public void Dispose() => _stream.Dispose();

    private static string? InterfacePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data)
    {
        SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
        if (size <= 0) return null;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6); // cbSize SP_DEVICE_INTERFACE_DETAIL_DATA_W
            if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, buf, size, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(buf + 4);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ---- WinAPI ----
    private const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attr);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("setupapi.dll")] private static extern IntPtr SetupDiGetClassDevsW(ref Guid cls, IntPtr enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll")] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid cls, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
