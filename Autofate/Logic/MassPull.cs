using System.Numerics;

namespace Autofate.Logic;

/// <summary>
/// Which fate mob to body-pull next during mass pull. Pure: the controller snapshots the fate's
/// enemies and asks here, so the rule can be tested without the game.
/// </summary>
public static class MassPull
{
    /// <summary>A live fate enemy. <paramref name="OnUs"/>: it targets us or our chocobo.</summary>
    public readonly record struct Enemy(ulong Id, Vector3 Position, bool OnUs);

    /// <summary>
    /// The mob to walk to next, or null to stop pulling and fight what we have.
    ///
    /// With nothing on us, the nearest mob wins however far it is: that's how we get into the fight
    /// at all. Once something is on us, only mobs within <paramref name="radius"/> of us count. The
    /// pile follows us, so walking further means dragging it along until it drops aggro, and then
    /// the mob we left counts as unpulled again and we walk back to it, forever.
    ///
    /// <paramref name="sticky"/> is the mob we're already walking to; we keep it while it is still
    /// a valid pick, so two mobs at similar distance can't make us flip between them.
    /// </summary>
    public static ulong? PickNext(Vector3 me, IReadOnlyList<Enemy> enemies, float radius, ulong sticky)
    {
        var havePile = false;
        foreach (var e in enemies)
            if (e.OnUs) { havePile = true; break; }

        var radiusSq = radius * radius;
        bool InReach(Enemy e) => !havePile || Vector3.DistanceSquared(me, e.Position) <= radiusSq;

        if (sticky != 0)
            foreach (var e in enemies)
                if (e.Id == sticky && !e.OnUs && InReach(e))
                    return e.Id;

        ulong? best = null;
        var bestSq = float.MaxValue;
        foreach (var e in enemies)
        {
            if (e.OnUs || !InReach(e)) continue;
            var d = Vector3.DistanceSquared(me, e.Position);
            if (d >= bestSq) continue;
            bestSq = d;
            best = e.Id;
        }
        return best;
    }
}
