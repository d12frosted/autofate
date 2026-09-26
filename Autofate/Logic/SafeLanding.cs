using System.Numerics;

namespace Autofate.Logic;

/// <summary>
/// Where to land in a fate for the Safe style. A random spot can drop us in the middle of a pack,
/// and everything that notices us on the way down is on us before the first pull. So we pick from
/// several landable spots the one clear of hostiles.
/// </summary>
public static class SafeLanding
{
    /// <summary>How far a landing spot wants to be from any hostile: past the crowd radius, with room to spare.</summary>
    public const float Clearance = SafePull.CrowdRadius + 5f;

    /// <summary>
    /// The closest spot to us with <see cref="Clearance"/> from every hostile; when none is that
    /// clear, the spot furthest from its nearest hostile. Horizontal distances.
    /// </summary>
    public static Vector3 Pick(Vector3 me, IReadOnlyList<Vector3> spots, IReadOnlyList<Vector3> hostiles)
    {
        Vector3? bestClear = null;
        var bestClearDist = float.MaxValue;
        var bestOpen = spots[0];
        var bestOpenGap = float.MinValue;
        foreach (var s in spots)
        {
            var gap = float.MaxValue;
            foreach (var h in hostiles) gap = MathF.Min(gap, Flat(s, h));
            if (gap >= Clearance)
            {
                var d = Flat(s, me);
                if (d < bestClearDist) { bestClearDist = d; bestClear = s; }
            }
            else if (gap > bestOpenGap)
            {
                bestOpenGap = gap;
                bestOpen = s;
            }
        }
        return bestClear ?? bestOpen;
    }

    private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
}
