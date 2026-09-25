namespace Autofate.Logic;

/// <summary>
/// Which Shared FATE zones are worth going to at a given level, best first. Without this the zone
/// rotation is a fixed list, so a level 87 would happily fly to Ultima Thule, find nothing it may
/// run, and sit there "waiting for FATEs".
/// </summary>
public static class SharedFateZones
{
    /// <summary>
    /// Level of the lowest FATEs in each Shared FATE zone, by TerritoryType id. The game sheets
    /// don't link a FATE to its zone in a readable way, so this is a table; zones missing from it
    /// (a future expansion) are treated as always in reach.
    /// </summary>
    private static readonly Dictionary<uint, int> MinLevels = new()
    {
        // Shadowbringers
        [813] = 70, // Lakeland
        [814] = 70, // Kholusia
        [815] = 76, // Amh Araeng
        [816] = 72, // Il Mheg
        [817] = 74, // The Rak'tika Greatwood
        [818] = 79, // The Tempest
        // Endwalker
        [956] = 80, // Labyrinthos
        [957] = 80, // Thavnair
        [958] = 82, // Garlemald
        [959] = 83, // Mare Lamentorum
        [960] = 88, // Ultima Thule
        [961] = 86, // Elpis
        // Dawntrail
        [1187] = 90, // Urqopacha
        [1188] = 90, // Kozama'uka
        [1189] = 93, // Yak T'el
        [1190] = 95, // Shaaloani
        [1191] = 97, // Heritage Found
        [1192] = 99, // Living Memory
    };

    /// <summary>Level of the zone's lowest FATEs, or null when the zone isn't in the table.</summary>
    public static int? MinFateLevel(uint territoryId)
        => MinLevels.TryGetValue(territoryId, out var l) ? l : null;

    /// <summary>
    /// The zones we can run FATEs in, best first. A zone is in reach when its lowest FATEs are at
    /// most <paramref name="levelsAbove"/> above us (the same allowance the FATE picker uses).
    /// Zones whose lowest FATEs are at or below our level come first, highest first, since all of
    /// their low end is ours; zones only reachable through the allowance come after, lowest first,
    /// and unknown zones last.
    /// </summary>
    public static uint[] Order(IEnumerable<uint> zones, int playerLevel, int levelsAbove)
    {
        var reach = playerLevel + Math.Max(0, levelsAbove);
        return zones
            .Select(z => (Id: z, Min: MinFateLevel(z)))
            .Where(z => z.Min is not { } m || m <= reach)
            .OrderBy(z => z.Min switch
            {
                { } m when m <= playerLevel => 0,
                not null => 1,
                null => 2,
            })
            // Fully in reach: highest first. Only through the allowance: closest to us first.
            .ThenBy(z => z.Min is { } m ? (m <= playerLevel ? -m : m) : 0)
            .Select(z => z.Id)
            .ToArray();
    }
}
