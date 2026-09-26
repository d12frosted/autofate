using System.Numerics;

namespace Autofate.Logic;

/// <summary>How we take on a fate's mobs.</summary>
public enum PullStyle
{
    /// <summary>Yolo on a tank, Safe on everything else.</summary>
    Auto,
    /// <summary>One at a time: fight what's on us first, then the mob with the fewest idle neighbours.</summary>
    Safe,
    /// <summary>Mass pull: gather a pile up to the cap and AOE it down.</summary>
    Yolo,
}

/// <summary>Target choice for the Safe pull style. Pure, so it can be tested without the game.</summary>
public static class SafePull
{
    /// <summary>
    /// Idle hostiles this close to a mob (or to where we stand) are assumed to join in. 20y: a Wild
    /// Ibruq in Yak T'el aggroed from over 21y away, so 15y was too trusting.
    /// </summary>
    public const float CrowdRadius = 20f;

    /// <summary>Below this share of HP, don't start on anything new; wait for it to come back.</summary>
    private const float MinHpForNewPull = 0.6f;

    /// <summary>
    /// Whether we're healthy enough to engage a new mob. Out of combat HP comes back in seconds,
    /// while starting the next pull at a third of it is how a melee DPS dies to the one after.
    /// </summary>
    public static bool MayStartNewPull(float hpFraction) => hpFraction >= MinHpForNewPull;

    /// <summary>
    /// What one extra mob joining the fight costs, in yalms of walking. High enough that a lone mob
    /// a good way off beats a close one standing in a pack.
    /// </summary>
    private const float CrowdPenalty = 40f;

    public readonly record struct Mob(ulong Id, Vector3 Position);

    /// <summary>
    /// The style to actually use. Auto picks by job role (ClassJob.Role: 1 = tank): a tank can hold
    /// a pile, everyone else dies to one.
    /// </summary>
    public static PullStyle Resolve(PullStyle chosen, byte role) => chosen switch
    {
        PullStyle.Auto => role == 1 ? PullStyle.Yolo : PullStyle.Safe,
        _ => chosen,
    };

    /// <summary>
    /// The fate mob to engage next when nothing is on us: the nearest one, penalised for every idle
    /// hostile standing around it (those would likely join in). <paramref name="candidates"/> are the
    /// fate's mobs not on us; <paramref name="idleHostiles"/> are hostiles not fighting anyone, fate
    /// or not, and may include the candidates themselves.
    /// </summary>
    public static ulong? PickTarget(Vector3 me, IReadOnlyList<Mob> candidates, IReadOnlyList<Mob> idleHostiles)
    {
        var radiusSq = CrowdRadius * CrowdRadius;
        ulong? best = null;
        var bestScore = float.MaxValue;
        foreach (var c in candidates)
        {
            var crowd = 0;
            foreach (var h in idleHostiles)
                if (h.Id != c.Id && Vector3.DistanceSquared(h.Position, c.Position) <= radiusSq)
                    crowd++;
            var score = Vector3.Distance(me, c.Position) + CrowdPenalty * crowd;
            if (score >= bestScore) continue;
            bestScore = score;
            best = c.Id;
        }
        return best;
    }
}
