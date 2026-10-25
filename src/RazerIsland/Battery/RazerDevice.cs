namespace RazerIsland.Battery;

public enum PowerState { Normal, Charging, Off }

/// <summary>Бездротовий пристрій Razer зі станом батареї.</summary>
public sealed record RazerDevice
{
    public required string Serial { get; init; }
    public required string Name { get; init; }
    /// <summary>MOUSE, HEADSET, KEYBOARD тощо (верхній регістр).</summary>
    public string Category { get; init; } = "";
    public int Level { get; init; }
    public PowerState State { get; init; }
    public string ProductId { get; init; } = "";
    public DateTime Time { get; init; } = DateTime.MinValue;
}

public sealed record DeviceSnapshot(DateTime Time, IReadOnlyList<RazerDevice> Devices);
