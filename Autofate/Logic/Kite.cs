using System.Numerics;

namespace Autofate.Logic;

/// <summary>
/// Geometry for the Safe style's kite: pull a mob out of its pack from range and fight it away from
/// its neighbours, instead of walking into the pack to hit it. Pure; the controller drives it.
/// </summary>
public static class Kite
{
    /// <summary>
    /// How far from the target we stand to pull it. The ranged pull attacks reach 20y; 18y leaves
    /// room for a mob that wanders while we get there.
    /// </summary>
    public const float PullRange = 18f;

    /// <summary>How far we want to stay from the target's idle neighbours (the same radius Safe counts a crowd in).</summary>
    public const float SafeGap = SafePull.CrowdRadius;

    /// <summary>How much further melee jobs back off after the pull, so the mob comes to us away from its pack.</summary>
    public const float RetreatDistance = 12f;

    /// <summary>Candidate directions around the target.</summary>
    private const int Directions = 24;

    /// <summary>
    /// Where to stand to pull <paramref name="target"/>: <see cref="PullRange"/> from it, at least
    /// <see cref="SafeGap"/> from each of its idle <paramref name="neighbours"/>, and of those the
    /// spot closest to us. When no spot clears every neighbour, the one furthest from the nearest
    /// neighbour. Horizontal geometry; the spot takes the target's height (the caller snaps it to
    /// the floor).
    /// </summary>
    public static Vector3 PullSpot(Vector3 me, Vector3 target, IReadOnlyList<Vector3> neighbours)
    {
        Vector3? bestSafe = null;
        var bestSafeDist = float.MaxValue;
        var bestOpen = target;
        var bestOpenGap = float.MinValue;

        for (var i = 0; i < Directions; i++)
        {
            var angle = i * MathF.Tau / Directions;
            var spot = new Vector3(target.X + PullRange * MathF.Cos(angle), target.Y, target.Z + PullRange * MathF.Sin(angle));

            var gap = float.MaxValue;
            foreach (var n in neighbours) gap = MathF.Min(gap, Flat(spot, n));

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
        return bestSafe ?? bestOpen;
    }

    /// <summary>Past the pull spot, straight away from the target: where melee jobs wait for the pulled mob.</summary>
    public static Vector3 RetreatSpot(Vector3 pullSpot, Vector3 target)
    {
        var away = new Vector2(pullSpot.X - target.X, pullSpot.Z - target.Z);
        if (away.LengthSquared() < 0.01f) away = Vector2.UnitX;
        away = Vector2.Normalize(away) * RetreatDistance;
        return new Vector3(pullSpot.X + away.X, pullSpot.Y, pullSpot.Z + away.Y);
    }

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
