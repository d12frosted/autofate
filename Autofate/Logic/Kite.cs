using System.Numerics;

namespace Autofate.Logic;

/// <summary>
/// Geometry for the Safe style's kite: pull a mob out of its pack from range and let it come to us,
/// instead of walking into the pack to hit it. Pure; the controller drives it.
/// </summary>
public static class Kite
{
    /// <summary>
    /// How far from the target we stand to pull it. The ranged pull attacks reach 20y; 18y leaves
    /// room for a mob that wanders while we get there.
    /// </summary>
    public const float PullRange = 18f;

    /// <summary>How far we want to stay from every idle hostile besides the target (the radius Safe counts a crowd in).</summary>
    public const float SafeGap = SafePull.CrowdRadius;

    /// <summary>Candidate directions around the target.</summary>
    private const int Directions = 24;

    /// <summary>
    /// Where to stand to pull <paramref name="target"/>: <see cref="PullRange"/> from it, with the
    /// walk there and the wait there clear of every other idle mob by <see cref="SafeGap"/>, where
    /// they'll be as well as where they are (<see cref="ThreatMap.PathClearance"/>). Of those, the
    /// spot closest to us, with Clear = true. When none is clear (typically: we're on the pack's
    /// side and every clear spot means walking through it; vnav takes the short way, it doesn't
    /// walk around mobs), the one that keeps the most room, with Clear = false, and the caller
    /// should wait for the mobs to move rather than go. Horizontal geometry; the spot takes the
    /// target's height (the caller snaps it to the floor).
    /// </summary>
    public static (Vector3 Spot, bool Clear) PullSpot(Vector3 me, TrackedMob target, IReadOnlyList<TrackedMob> mobs)
    {
        Vector3? bestSafe = null;
        var bestSafeDist = float.MaxValue;
        var bestOpen = target.Position;
        var bestOpenGap = float.MinValue;

        for (var i = 0; i < Directions; i++)
        {
            var angle = i * MathF.Tau / Directions;
            var spot = new Vector3(target.Position.X + PullRange * MathF.Cos(angle), target.Position.Y,
                                   target.Position.Z + PullRange * MathF.Sin(angle));
            var gap = ThreatMap.PathClearance(me, spot, mobs, exclude: target.Id);
            if (gap >= SafeGap)
            {
                var d = Flat(spot, me);
                if (d < bestSafeDist) { bestSafeDist = d; bestSafe = spot; }
            }
            else if (gap > bestOpenGap)
            {
                bestOpenGap = gap;
                bestOpen = spot;
            }
        }
        return bestSafe is { } safe ? (safe, true) : (bestOpen, false);
    }

    /// <summary>
    /// Can we pull <paramref name="target"/> from where we stand: within <see cref="PullRange"/> of
    /// it, and no other idle mob coming within <see cref="SafeGap"/> of us while we wait? Then we
    /// don't move at all, which is the whole point of kiting.
    /// </summary>
    public static bool CanPullFromHere(Vector3 me, TrackedMob target, IReadOnlyList<TrackedMob> mobs)
        => Flat(me, target.Position) <= PullRange
           && ThreatMap.Clearance(me, mobs, exclude: target.Id) >= SafeGap;

    /// <summary>
    /// Is the spot we're walking to, and the rest of the walk, still clear given where mobs are
    /// heading now? A little slack against <see cref="SafeGap"/>, so a spot doesn't flip back and
    /// forth on the edge.
    /// </summary>
    public static bool SpotStillSafe(Vector3 me, Vector3 spot, TrackedMob target, IReadOnlyList<TrackedMob> mobs)
        => ThreatMap.PathClearance(me, spot, mobs, exclude: target.Id) >= SafeGap * 0.9f;

    /// <summary>
    /// The ranged attack a melee job pulls with (all 20y, learned at 15, Harpe 25y), by ClassJob
    /// row id. Null for jobs without one (Monk / Pugilist) and for ranged jobs and healers, which
    /// pull with their normal attack.
    /// </summary>
    public static uint? RangedPullAction(uint classJob) => classJob switch
    {
        1 or 19 => 24,      // GLA / PLD: Shield Lob
        3 or 21 => 46,      // MRD / WAR: Tomahawk
        32 => 3624,         // DRK: Unmend
        37 => 16143,        // GNB: Lightning Shot
        4 or 22 => 90,      // LNC / DRG: Piercing Talon
        34 => 7486,         // SAM: Enpi
        29 or 30 => 2247,   // ROG / NIN: Throwing Dagger
        39 => 24386,        // RPR: Harpe
        41 => 34632,        // VPR: Writhing Snap
        _ => null,
    };

    private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
}
