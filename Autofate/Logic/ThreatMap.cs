using System.Numerics;

namespace Autofate.Logic;

/// <summary>What a hostile is doing, as far as danger to us goes.</summary>
public enum MobState
{
    /// <summary>Not fighting anyone: it joins in when we come near it.</summary>
    Idle,
    /// <summary>Targeting us or our chocobo.</summary>
    OnUs,
    /// <summary>Fighting someone else: it won't turn on us.</summary>
    Busy,
}

/// <summary>A hostile with where it's heading: velocity in yalms per second, horizontal.</summary>
public readonly record struct TrackedMob(ulong Id, Vector3 Position, Vector3 Velocity, MobState State);

/// <summary>
/// Follows hostiles across ticks so each has a velocity. Mobs wander, and a decision made from where
/// they are (a pull spot, the way there) is stale a few seconds later; with velocity we can ask
/// where they're going instead.
/// </summary>
public sealed class MobTracker
{
    public readonly record struct Sample(ulong Id, Vector3 Position, MobState State);

    private sealed class Track
    {
        public Vector3 Position, Velocity;
        public long Ms;
        public MobState State;
    }

    /// <summary>Samples closer together than this don't update the velocity: too noisy.</summary>
    private const long MinDtMs = 200;

    private readonly Dictionary<ulong, Track> _tracks = new();

    public IReadOnlyList<TrackedMob> Mobs
        => _tracks.Select(kv => new TrackedMob(kv.Key, kv.Value.Position, kv.Value.Velocity, kv.Value.State)).ToList();

    /// <summary>Feed the hostiles seen now; ones not in <paramref name="samples"/> are forgotten.</summary>
    public void Update(long nowMs, IEnumerable<Sample> samples)
    {
        var seen = new HashSet<ulong>();
        foreach (var s in samples)
        {
            seen.Add(s.Id);
            if (!_tracks.TryGetValue(s.Id, out var t))
            {
                _tracks[s.Id] = new Track { Position = s.Position, Ms = nowMs, State = s.State };
                continue;
            }
            t.State = s.State;
            var dt = nowMs - t.Ms;
            if (dt < MinDtMs) continue;
            var step = (s.Position - t.Position) * (1000f / dt);
            step.Y = 0;
            t.Velocity = t.Velocity * 0.5f + step * 0.5f; // smoothed: a single jittery sample shouldn't swing it
            t.Position = s.Position;
            t.Ms = nowMs;
        }
        foreach (var id in _tracks.Keys.Where(id => !seen.Contains(id)).ToList()) _tracks.Remove(id);
    }

    public void Clear() => _tracks.Clear();
}

/// <summary>
/// Danger from idle hostiles, looking a few seconds ahead along each mob's velocity. Everything Safe
/// decides about where to stand and where to walk goes through here.
/// </summary>
public static class ThreatMap
{
    /// <summary>An idle mob this close notices us. A Wild Ibruq aggroed from over 21y; 20y plus the look-ahead covers it.</summary>
    public const float AggroRadius = SafePull.CrowdRadius;

    /// <summary>How far ahead we extrapolate a mob's walk. Wandering changes direction; beyond a few seconds it's a guess.</summary>
    public const float HorizonS = 3f;

    /// <summary>A mob moving faster than this is running (just spawned, charging somewhere), not wandering.</summary>
    public const float RunningSpeed = 4f;

    /// <summary>Runners within this of us mean the area hasn't settled.</summary>
    public const float SettleRadius = 40f;

    /// <summary>Our speed on foot, for timing a walk against mobs' walks.</summary>
    private const float OurSpeed = 6f;

    private const float StepS = 0.5f;

    private static Vector3 At(TrackedMob m, float t) => m.Position + m.Velocity * MathF.Min(t, HorizonS * 2);

    private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));

    private static IEnumerable<TrackedMob> Idle(IReadOnlyList<TrackedMob> mobs, ulong exclude)
        => mobs.Where(m => m.State == MobState.Idle && m.Id != exclude);

    /// <summary>
    /// How close any idle mob (other than <paramref name="exclude"/>) gets to <paramref name="p"/>
    /// if we stand there for the next <see cref="HorizonS"/> seconds. float.MaxValue with none around.
    /// </summary>
    public static float Clearance(Vector3 p, IReadOnlyList<TrackedMob> mobs, ulong exclude = 0)
    {
        var best = float.MaxValue;
        foreach (var m in Idle(mobs, exclude))
            for (var t = 0f; t <= HorizonS + 0.001f; t += StepS)
                best = MathF.Min(best, Flat(p, At(m, t)));
        return best;
    }

    /// <summary>
    /// How close any idle mob gets to us while we walk from <paramref name="from"/> to
    /// <paramref name="to"/> and then stand there: our position and theirs, moment by moment.
    /// </summary>
    public static float PathClearance(Vector3 from, Vector3 to, IReadOnlyList<TrackedMob> mobs, ulong exclude = 0)
    {
        var duration = Flat(from, to) / OurSpeed;
        var best = float.MaxValue;
        foreach (var m in Idle(mobs, exclude))
        {
            for (var t = 0f; t <= duration + HorizonS + 0.001f; t += StepS)
            {
                var us = t >= duration ? to : Vector3.Lerp(from, to, duration <= 0 ? 1 : t / duration);
                best = MathF.Min(best, Flat(us, At(m, t)));
            }
        }
        return best;
    }

    /// <summary>
    /// No idle mob near us is running. Fresh spawns run towards their spots or at players; picking a
    /// fight before that calms down picks it with whoever is passing by.
    /// </summary>
    public static bool IsSettled(Vector3 me, IReadOnlyList<TrackedMob> mobs)
        => !mobs.Any(m => m.State == MobState.Idle
                          && Flat(me, m.Position) <= SettleRadius
                          && m.Velocity.Length() > RunningSpeed);

    /// <summary>
    /// Idle mobs besides the target whose walk takes them within <see cref="AggroRadius"/> of the
    /// target's within the horizon: the ones that would join in, now or by the time we get there.
    /// </summary>
    public static int Crowd(TrackedMob target, IReadOnlyList<TrackedMob> mobs)
    {
        var n = 0;
        foreach (var m in Idle(mobs, target.Id))
        {
            for (var t = 0f; t <= HorizonS + 0.001f; t += StepS)
            {
                if (Flat(At(m, t), At(target, t)) > AggroRadius) continue;
                n++;
                break;
            }
        }
        return n;
    }

    /// <summary>How far we consider stepping aside, and how much better a step must be to bother.</summary>
    private static readonly float[] EvadeSteps = { 8f, 14f };
    private const int EvadeDirections = 16;
    private const float EvadeMinGain = 3f;

    /// <summary>
    /// While we stand and wait (for a pulled mob, or before the next pull): if an idle mob is heading
    /// within <see cref="AggroRadius"/> of us, the nearby spot whose walk and wait keep the most
    /// room. Null when we're fine where we are, or when no step is meaningfully better (mobs
    /// all around: moving would only be dancing). Mobs already on us don't count: them coming to us
    /// is the point.
    /// </summary>
    public static Vector3? EvadeSpot(Vector3 me, IReadOnlyList<TrackedMob> mobs)
    {
        var here = Clearance(me, mobs);
        if (here >= AggroRadius) return null;

        Vector3? best = null;
        var bestGap = here + EvadeMinGain;
        foreach (var r in EvadeSteps)
        {
            for (var i = 0; i < EvadeDirections; i++)
            {
                var angle = i * MathF.Tau / EvadeDirections;
                var spot = new Vector3(me.X + r * MathF.Cos(angle), me.Y, me.Z + r * MathF.Sin(angle));
                var gap = PathClearance(me, spot, mobs);
                if (gap < bestGap) continue;
                bestGap = gap;
                best = spot;
            }
        }
        return best;
    }
}
