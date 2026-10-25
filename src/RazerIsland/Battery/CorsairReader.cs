using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RazerIsland.Battery;

/// <summary>
/// Заряд бездротових пристроїв Corsair через офіційний iCUE SDK v4 (iCUESDK.x64_2019.dll поруч із exe).
/// Працює, лише коли запущений iCUE і в ньому дозволено керування стороннім програмам.
/// shortcut: перевірено лише за заголовком SDK, без живих пристроїв.
/// </summary>
public static class CorsairReader
{
    private const string Dll = "iCUESDK.x64_2019.dll";
    private const int CDT_All = -1, CDPI_BatteryLevel = 9, CT_Int32 = 1, CSS_Connected = 6, DeviceCountMax = 64;

    private static volatile int _state;
    private static bool _connectCalled;
    // делегат має жити, поки SDK може його викликати
    private static readonly SessionStateChanged OnState = (_, e) => _state = Marshal.ReadInt32(e);

    public static List<RazerDevice> Read()
    {
        var result = new List<RazerDevice>();
        if (Process.GetProcessesByName("iCUE").Length == 0) return result;
        if (!_connectCalled)
        {
            _connectCalled = true;
            CorsairConnect(OnState, IntPtr.Zero); // асинхронно: пристрої з'являться з наступним опитуванням
            return result;
        }
        if (_state != CSS_Connected) return result;

        var devices = new CorsairDeviceInfo[DeviceCountMax];
        var filter = new CorsairDeviceFilter { deviceTypeMask = CDT_All };
        if (CorsairGetDevices(ref filter, DeviceCountMax, devices, out int count) != 0) return result;
        for (int i = 0; i < count; i++)
        {
            var d = devices[i];
            if (CorsairReadDeviceProperty(d.id, CDPI_BatteryLevel, 0, out var prop) != 0) continue; // дротові пристрої заряду не мають
            try
            {
                if (prop.type != CT_Int32) continue;
                result.Add(new RazerDevice
                {
                    Serial = "corsair:" + Str(d.id),
                    Name = "Corsair " + Str(d.model),
                    Category = d.type switch { 0x1 => "KEYBOARD", 0x2 => "MOUSE", 0x8 or 0x10 => "HEADSET", 0x1000 => "GAMEPAD", _ => "" },
                    Brand = "Corsair",
                    Level = Math.Clamp(prop.int32, 0, 100),
                    State = PowerState.Normal,
                    Time = DateTime.Now,
                });
            }
            finally { CorsairFreeProperty(ref prop); }
        }
        return result;
    }

    private static string Str(byte[] s)
    {
        int n = Array.IndexOf(s, (byte)0);
        return Encoding.UTF8.GetString(s, 0, n < 0 ? s.Length : n);
    }

    // ---- iCUESDK.h (v4.0.84) ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SessionStateChanged(IntPtr context, IntPtr eventData);

    [StructLayout(LayoutKind.Sequential)]
    private struct CorsairDeviceFilter { public int deviceTypeMask; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CorsairDeviceInfo
    {
        public int type;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] id;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] serial;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] model;
        public int ledCount;
        public int channelCount;
    }

    // CorsairProperty: enum type (4 байти) + union, вирівняний на 8 (найбільший член — вказівник + лічильник)
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct CorsairProperty
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(8)] public int int32;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int CorsairConnect(SessionStateChanged onStateChanged, IntPtr context);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int CorsairGetDevices(ref CorsairDeviceFilter filter, int sizeMax, [Out] CorsairDeviceInfo[] devices, out int size);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int CorsairReadDeviceProperty(byte[] deviceId, int propertyId, uint index, out CorsairProperty property);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int CorsairFreeProperty(ref CorsairProperty property);
}
