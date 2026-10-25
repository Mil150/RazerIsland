namespace RazerIsland.Battery;

/// <summary>Бренд пристрою: за ідентифікатором виробника Bluetooth/USB, інакше за назвою.</summary>
public static class Brands
{
    // VID-джерело 1 — Bluetooth SIG Company ID, 2 — USB-IF Vendor ID
    private static readonly Dictionary<(byte, ushort), string> ByVid = new()
    {
        [(1, 0x004C)] = "Apple", [(1, 0x0006)] = "Microsoft", [(1, 0x0075)] = "Samsung", [(1, 0x012D)] = "Sony",
        [(1, 0x00E0)] = "Google", [(1, 0x0057)] = "JBL", [(1, 0x038F)] = "Xiaomi",
        [(2, 0x1532)] = "Razer", [(2, 0x046D)] = "Logitech", [(2, 0x045E)] = "Microsoft", [(2, 0x054C)] = "Sony",
        [(2, 0x05AC)] = "Apple", [(2, 0x1038)] = "SteelSeries", [(2, 0x1B1C)] = "Corsair", [(2, 0x04E8)] = "Samsung",
    };

    private static readonly (string Word, string Brand)[] ByName =
    [
        ("razer", "Razer"), ("logi", "Logitech"), ("mx ", "Logitech"), ("galaxy", "Samsung"), ("samsung", "Samsung"),
        ("airpods", "Apple"), ("apple", "Apple"), ("magic", "Apple"), ("xbox", "Microsoft"), ("surface", "Microsoft"),
        ("sony", "Sony"), ("wh-", "Sony"), ("wf-", "Sony"), ("dualsense", "Sony"), ("dualshock", "Sony"),
        ("steelseries", "SteelSeries"), ("arctis", "SteelSeries"), ("corsair", "Corsair"), ("hyperx", "HyperX"),
        ("jbl", "JBL"), ("pixel", "Google"), ("xiaomi", "Xiaomi"), ("redmi", "Xiaomi"), ("huawei", "Huawei"), ("freebuds", "Huawei"),
    ];

    public static string Detect(string name, byte vidSource, ushort vid)
    {
        if (ByVid.TryGetValue((vidSource, vid), out var b)) return b;
        var n = name.ToLowerInvariant() + " ";
        foreach (var (word, brand) in ByName)
            if (n.Contains(word)) return brand;
        return "";
    }
}
