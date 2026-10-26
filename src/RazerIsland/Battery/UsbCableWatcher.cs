using Windows.Devices.Enumeration;

namespace RazerIsland.Battery;

/// <summary>
/// Миттєво помічає, що до ПК кабелем підключили пристрій Razer: Windows бачить новий USB-пристрій одразу,
/// а Synapse пише «Charging» у лог лише секунд через 30.
/// Викликай <see cref="Check"/> на кожне WM_DEVICECHANGE — тут лише порівняння списку USB-пристроїв Razer.
/// </summary>
public sealed class UsbCableWatcher
{
    private const string Aqs = "System.Devices.DeviceInstanceId:~<\"USB\\VID_1532\" AND System.Devices.Present:=System.StructuredQueryType.Boolean#True";

    private Dictionary<string, string>? _present; // id → назва; null до першого знімка
    private int _pending;

    /// <summary>Новий USB-пристрій Razer: ідентифікатор і назва, як її показує Windows (напр. «Razer Naga V2 Pro»).</summary>
    public event Action<string, string>? Connected;
    /// <summary>USB-пристрій Razer зник (кабель від'єднано).</summary>
    public event Action<string>? Disconnected;

    public UsbCableWatcher() => _ = SnapshotAsync(notify: false);

    /// <summary>Набір пристроїв змінився. Кілька повідомлень поспіль зливаються в одну перевірку.</summary>
    public async void Check()
    {
        int me = Interlocked.Increment(ref _pending);
        // підключений вузол отримує назву, коли Windows поставить драйвер, — трохи чекаємо
        await Task.Delay(1500);
        if (me == _pending) await SnapshotAsync(notify: true);
    }

    private async Task SnapshotAsync(bool notify)
    {
        Dictionary<string, string> now;
        try { now = (await DeviceInformation.FindAllAsync(Aqs, null, DeviceInformationKind.Device)).ToDictionary(d => d.Id, d => d.Name ?? ""); }
        catch { return; }
        var before = _present;
        _present = now;
        if (!notify || before is null) return;
        foreach (var (id, name) in now)
            if (!before.ContainsKey(id) && name.Length > 0) Connected?.Invoke(id, name);
        foreach (var id in before.Keys)
            if (!now.ContainsKey(id)) Disconnected?.Invoke(id);
    }
}
