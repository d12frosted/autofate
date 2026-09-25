using System.Numerics;

namespace Autofate.Logic;

/// <summary>
/// Whether to teleport to an aetheryte closer to a FATE instead of flying there. Pure: positions
/// are world X/Z (as <see cref="Vector2"/>, Y holding Z), since aetherytes only have a map position.
/// </summary>
public static class AetheryteHop
{
    /// <summary>An aetheryte we may teleport to (attuned, not written off), at world X/Z.</summary>
    public readonly record struct Aetheryte(uint Id, Vector2 Position);

    public readonly record struct Settings(float MinDistance, float MinSaving, float ArrivalDistance);

    public enum Verdict
    {
        Hop,
        /// <summary>The FATE is closer than <see cref="Settings.MinDistance"/>.</summary>
        FateTooClose,
        /// <summary>No aetheryte we may use in this zone.</summary>
        NoAetheryte,
        /// <summary>The best aetheryte cuts less than <see cref="Settings.MinSaving"/> off the trip.</summary>
        NotEnoughSaving,
        /// <summary>We're already standing at the best aetheryte.</summary>
        AlreadyThere,
    }

    /// <summary>
    /// The verdict plus what it was based on: the aetheryte closest to the FATE (0 if none), our
    /// distance to the FATE, and how much of it teleporting would save.
    /// </summary>
    public readonly record struct Decision(Verdict Verdict, uint AetheryteId, float Distance, float Saved);

    /// <summary>
    /// World X/Z of a map marker. Markers sit on a 2048 pixel map texture centred on 1024, scaled
    /// by the map's size factor (percent) and shifted by its offset.
    /// </summary>
    public static Vector2 MarkerToWorld(short x, short y, ushort sizeFactor, short offsetX, short offsetY)
    {
        var scale = sizeFactor / 100f;
        return new Vector2((x - 1024f) / scale - offsetX, (y - 1024f) / scale - offsetY);
    }

    public static Decision Decide(Vector2 me, Vector2 fate, IEnumerable<Aetheryte> aetherytes, Settings s)
    {
        var distance = Vector2.Distance(me, fate);
        if (distance < s.MinDistance) return new(Verdict.FateTooClose, 0, distance, 0);

        Aetheryte? best = null;
        var bestToFate = float.MaxValue;
        foreach (var a in aetherytes)
        {
            var d = Vector2.Distance(a.Position, fate);
            if (d >= bestToFate) continue;
            best = a;
            bestToFate = d;
        }
        if (best is not { } target) return new(Verdict.NoAetheryte, 0, distance, 0);

        var saved = distance - bestToFate;
        if (Vector2.Distance(me, target.Position) <= s.ArrivalDistance)
            return new(Verdict.AlreadyThere, target.Id, distance, saved);
        if (saved < s.MinSaving) return new(Verdict.NotEnoughSaving, target.Id, distance, saved);
        return new(Verdict.Hop, target.Id, distance, saved);
    }
}
