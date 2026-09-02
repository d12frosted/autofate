using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Fates;
using ECommons.DalamudServices;
using ECommons.GameHelpers;

namespace Autofate.Core;

/// <summary>
/// Classifies and prioritizes FATEs in the player's zone. Filters by MinFateTimeSeconds, level
/// window, blacklist, and enabled types; picks lowest-timer or nearest per PrioritizeLowTimer.
/// </summary>
public static class FateSelector
{
    // Client FATE icon ids used as a fallback classification signal.
    private static readonly HashSet<uint> BossIcons = new() { 60722, 60723 };
    private static readonly HashSet<uint> CollectIcons = new() { 60729, 60730 };
    private static readonly HashSet<uint> DefendIcons = new() { 60727, 60728 };
    private static readonly HashSet<uint> EscortIcons = new() { 60725, 60726 };

    // Fate ids already logged a classification diagnostic (one line per fate).
    private static readonly HashSet<uint> _diagLogged = new();
    private static int SafeHandIn(IFate fate) { try { return fate.HandInCount; } catch { return -1; } }

    /// <summary>Distinct FATE names seen this session (first-seen order), for the blacklist UI.</summary>
    public static readonly List<string> SeenFateNames = new();

    private static void NoteSeenFate(IFate fate)
    {
        var name = fate.Name.ToString();
        if (string.IsNullOrEmpty(name)) return;
        if (!SeenFateNames.Contains(name)) SeenFateNames.Add(name);
    }

    /// <summary>
    /// Classify a FATE's type from the Fate excel sheet's Rule/item fields, falling back to the
    /// client icon id. Rule: 1=Battle, 2/3=Collect, 4=Boss, 5=Defend, 6=Escort; an EventItem/
    /// TurnInEventItem is the strongest collect indicator.
    /// </summary>
    public static FateType Classify(IFate fate)
    {
        try
        {
            var data = fate.GameData;
            if (data.ValueNullable is { } row)
            {
                // Collect fates expose a turn-in / event item.
                if (row.TurnInEventItem.RowId != 0 || row.EventItem.RowId != 0 || row.ReqEventItem.RowId != 0)
                    return FateType.Collect;

                switch (row.Rule)
                {
                    case 4: return FateType.Boss;
                    case 5: return FateType.Defend;
                    case 6: return FateType.Escort;
                }
            }
        }
        catch { /* fall through to icon heuristics */ }

        var icon = fate.MapIconId != 0 ? fate.MapIconId : fate.IconId;
        if (BossIcons.Contains(icon)) return FateType.Boss;
        if (CollectIcons.Contains(icon)) return FateType.Collect;
        if (DefendIcons.Contains(icon)) return FateType.Defend;
        if (EscortIcons.Contains(icon)) return FateType.Escort;

        try { if (fate.HandInCount > 0) return FateType.Collect; }
        catch { /* HandInCount may throw in some states */ }

        return FateType.Battle;
    }

    /// <summary>
    /// The hand-in item id for a collect FATE (0 if not a collect fate). The authoritative source —
    /// the one BMR/DWD key off — is the Fate sheet's <c>EventItem</c> row: that's the collectable
    /// you pick up off the ground AND hand in. We prefer it, falling back to TurnIn/ReqEventItem
    /// only if EventItem is somehow unset (older/edge fates), since those can resolve to a different
    /// (wrong) row on some fates.
    /// </summary>
    public static uint GetCollectItemId(IFate fate)
    {
        try
        {
            var data = fate.GameData;
            if (data.ValueNullable is not { } row) return 0;
            if (row.EventItem.RowId != 0) return row.EventItem.RowId;       // authoritative
            if (row.TurnInEventItem.RowId != 0) return row.TurnInEventItem.RowId;
            if (row.ReqEventItem.RowId != 0) return row.ReqEventItem.RowId;
        }
        catch { /* ignore */ }
        return 0;
    }

    /// <summary>
    /// A FATE the server has spawned into the table but has NOT started yet. Players cannot see it
    /// at all — no ring, no map icon, no mobs — and its <c>TimeRemaining</c> is nonsense, because
    /// StartTimeEpoch is still 0 and the value comes out as (duration - now), i.e. a huge negative
    /// number. That garbage timer is the ONLY thing a preparing fate has in common with a fate we
    /// have to start via an NPC; do not read it as one.
    /// </summary>
    public static bool IsPreparing(IFate fate) => fate.State == FateState.Preparing;

    public readonly record struct Candidate(IFate Fate, FateType Type, float Distance, long TimeRemaining, bool Preparing);

    /// <summary>A fate this close (yalms) is engaged immediately, ignoring timer priority.</summary>
    private const float PickNearbyDist = 50f;

    /// <summary>
    /// Returns the list of valid candidate fates in the current zone, already filtered.
    /// <paramref name="skipPreparing"/> holds fate ids we gave up waiting on: they stay out of the
    /// running WHILE they are still preparing, and become normal candidates once they do start.
    /// </summary>
    public static List<Candidate> GetCandidates(Configuration c, IReadOnlySet<ushort>? skipPreparing = null)
    {
        var list = new List<Candidate>();
        var me = Player.Object;
        if (me == null) return list;
        var myPos = me.Position;
        var myLevel = Player.Level;

        foreach (var fate in Svc.Fates)
        {
            if (fate == null) continue;
            if (fate.State != FateState.Running && fate.State != FateState.Preparing) continue;

            var preparing = IsPreparing(fate);
            if (preparing && skipPreparing != null && skipPreparing.Contains(fate.FateId)) continue;

            // The minimum-time cutoff only means anything for a fate that is actually running: a
            // preparing one has no timer to read yet, only the garbage value described on
            // IsPreparing, so it must never be measured against the cutoff.
            var time = fate.TimeRemaining;
            if (!preparing && time < c.MinFateTimeSeconds) continue;

            // Skip fates more than N levels above the player.
            if (fate.Level > myLevel + c.LevelsAbovePlayer) continue;

            // Record every fate seen (for the blacklist UI), even if it doesn't qualify.
            NoteSeenFate(fate);

            // Never navigate to a fate the user blacklisted by name.
            if (c.FateBlacklist.Contains(fate.Name.ToString())) continue;

            var type = Classify(fate);

            // One-shot diagnostic per fate id (verbose only): dump classification inputs.
            if (c.VerboseLogging && _diagLogged.Add(fate.FateId))
            {
                uint rule = 0; uint sheetIcon = 0;
                try { if (fate.GameData.ValueNullable is { } r) { rule = r.Rule; sheetIcon = (uint)r.Icon; } }
                catch { }
                Svc.Log.Information($"[FateDiag] '{fate.Name}' id={fate.FateId} -> {type} | Rule={rule} "
                    + $"MapIcon={fate.MapIconId} Icon={fate.IconId} SheetIcon={sheetIcon} HandIn={SafeHandIn(fate)}");
            }

            var dist = Vector3.Distance(myPos, fate.Position);
            list.Add(new Candidate(fate, type, dist, time, preparing));
        }

        return list;
    }

    /// <summary>Picks the best fate to run next, or null if none qualify.</summary>
    public static Candidate? PickBest(Configuration c, IReadOnlySet<ushort>? skipPreparing = null)
    {
        var candidates = GetCandidates(c, skipPreparing);
        if (candidates.Count == 0) return null;

        // PROXIMITY OVERRIDE: ignore the lowest-timer recommendation when a fate is right on top of
        // us — a fate within PickNearbyDist yalms, OR one whose ring we're already standing inside,
        // is taken immediately (nearest first). This avoids running off to a far expiring fate when
        // there's one we could engage instantly, and makes chained replacement fates (which spawn at
        // our current spot) get picked at once.
        var nearby = candidates
            .Where(x => x.Distance <= PickNearbyDist || x.Distance <= x.Fate.Radius)
            .OrderBy(x => x.Distance)
            .ToList();
        if (nearby.Count > 0) return nearby[0];

        if (c.PrioritizeLowTimer)
        {
            // Running fates: lowest remaining time first. A preparing fate has no timer to sort on,
            // so instead of dumping it last we interleave it by DISTANCE against the running fates'
            // distances: it sorts as if its remaining time equalled that of the nearest running fate
            // it's closer than. In practice this picks a nearby preparing fate over a far timed one,
            // while still grabbing an expiring timed fate that's right next to us.
            var timed = candidates.Where(x => !x.Preparing).OrderBy(x => x.Distance).ToList();
            return candidates
                .OrderBy(x => x.Preparing
                    ? EffectiveTimerByDistance(x, timed)
                    : x.TimeRemaining)
                .ThenBy(x => x.Distance)
                .First();
        }

        return candidates.OrderBy(x => x.Distance).First();
    }

    /// <summary>
    /// Effective timer for a preparing (not yet started) fate so it can be interleaved among the
    /// running fates by distance: it takes the TimeRemaining of the nearest running fate that is
    /// FARTHER than it, so it sorts just ahead of every running fate it's closer than. If it's
    /// farther than all of them (or there are none), it sorts last (long.MaxValue).
    /// </summary>
    private static long EffectiveTimerByDistance(Candidate preparing, System.Collections.Generic.List<Candidate> timedByDistance)
    {
        foreach (var t in timedByDistance) // nearest running fate first
            if (t.Distance > preparing.Distance)
                return t.TimeRemaining; // slot just ahead of this (farther) running fate
        return long.MaxValue; // farther than all running fates -> last
    }

    /// <summary>Find the fate the player is currently standing inside (within its radius), if any.</summary>
    public static IFate? GetCurrentFate()
    {
        var me = Player.Object;
        if (me == null) return null;
        var pos = me.Position;
        foreach (var fate in Svc.Fates)
        {
            if (fate == null) continue;
            if (fate.State != FateState.Running) continue;
            if (Vector3.Distance(pos, fate.Position) <= fate.Radius)
                return fate;
        }
        return null;
    }

    public static IFate? GetFateById(ushort fateId)
        => Svc.Fates.FirstOrDefault(f => f != null && f.FateId == fateId);
}
