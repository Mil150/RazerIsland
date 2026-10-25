using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace RazerIsland.Battery;

/// <summary>Заряд підключених Bluetooth-пристроїв — той самий, що показує «Параметри → Bluetooth і пристрої».</summary>
public static class BluetoothReader
{
    private const string Bt = "{2BD67D8B-8BEB-48D5-87E0-6CDA3428040A}";
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string AddressKey = Bt + " 1";
    private const string VidSourceKey = Bt + " 6";
    private const string VidKey = Bt + " 7";
    private const string ClassKey = Bt + " 10";

    static BluetoothReader()
    {
        Debug.Assert(AddressFromId("Bluetooth#Bluetooth00:11:22:33:44:55-aa:bb:cc:dd:ee:ff") == "AABBCCDDEEFF");
        Debug.Assert(CategoryFromClass(5898764) == "PHONE");
    }

    public static async Task<List<RazerDevice>> ReadAsync()
    {
        // підключені зараз (класичні й BLE); спарені, але вимкнені сюди не потрапляють
        var connected = new Dictionary<string, string>();
        foreach (var sel in new[]
        {
            BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
            BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
        })
            foreach (var d in await DeviceInformation.FindAllAsync(sel))
                connected.TryAdd(AddressFromId(d.Id), d.Name);

        var result = new List<RazerDevice>();
        if (connected.Count == 0) return result;

        // заряд Windows записує на вузли служб пристрою (Hands-Free, HID тощо)
        var nodes = await DeviceInformation.FindAllAsync(
            "System.Devices.DeviceInstanceId:~<\"BTH\" AND System.Devices.Present:=System.StructuredQueryType.Boolean#True",
            [BatteryKey, AddressKey, VidSourceKey, VidKey, ClassKey], DeviceInformationKind.Device);
        foreach (var n in nodes)
        {
            if (n.Properties.GetValueOrDefault(BatteryKey) is not byte level) continue;
            if (n.Properties.GetValueOrDefault(AddressKey) is not string addr || !connected.TryGetValue(addr.ToUpperInvariant(), out var name)) continue;
            if (result.Any(r => r.Serial == "bt:" + addr)) continue;
            var cod = n.Properties.GetValueOrDefault(ClassKey) as uint? ?? 0;
            result.Add(new RazerDevice
            {
                Serial = "bt:" + addr,
                Name = name,
                Category = CategoryFromClass(cod),
                Level = Math.Clamp((int)level, 0, 100),
                State = PowerState.Normal,
                Brand = Brands.Detect(name, n.Properties.GetValueOrDefault(VidSourceKey) as byte? ?? 0, n.Properties.GetValueOrDefault(VidKey) as ushort? ?? 0),
                Time = DateTime.Now,
            });
        }
        return result;
    }

    // "Bluetooth#Bluetooth<адаптер>-aa:bb:cc:dd:ee:ff" → "AABBCCDDEEFF"
    private static string AddressFromId(string id) => id[(id.LastIndexOf('-') + 1)..].Replace(":", "").ToUpperInvariant();

    // Bluetooth Class of Device: основний клас у бітах 8–12, для периферії тип у бітах 6–7
    private static string CategoryFromClass(uint cod) => ((cod >> 8) & 0x1F) switch
    {
        0x02 => "PHONE",
        0x04 => "HEADSET",
        0x05 => (cod & 0xC0) == 0x80 ? "MOUSE" : (cod & 0xC0) == 0x40 ? "KEYBOARD" : (cod & 0x3C) == 0x08 ? "GAMEPAD" : "",
        _ => "",
    };
}
