namespace Autofate.Logic;

/// <summary>
/// Notices a trip that isn't getting anywhere. The travel code has stuck handling for "we aren't
/// moving", but a trip can move plenty and still never get closer: when vnavmesh can't route to the
/// target at all (seen in Elpis, a fate on an island 80y above us), every retry backs out, re-paths
/// to the same spot, fails the same way, and the movement keeps the "not moving" checks happy.
///
/// So this watches the distance itself: no gain of <c>minGain</c> over <c>stallMs</c> of our own
/// time is a stall. Time spent waiting on a pathfind doesn't count against us; a long flying
/// pathfind is slow, not stuck.
/// </summary>
public sealed class TravelProgress
{
    private readonly long _stallMs;
    private readonly float _minGain;
    private long? _lastMs;
    private float _best;
    private long _stalledMs;

    public TravelProgress(long stallMs, float minGain)
    {
        _stallMs = stallMs;
        _minGain = minGain;
    }

    /// <summary>Forget the trip (new destination).</summary>
    public void Reset()
    {
        _lastMs = null;
        _stalledMs = 0;
    }

    /// <summary>Feed the current distance to the destination; true once the trip has stalled.</summary>
    public bool Update(long nowMs, float distance, bool waiting)
    {
        if (_lastMs is not { } last)
        {
            _lastMs = nowMs;
            _best = distance;
            return false;
        }
        _lastMs = nowMs;

        if (_best - distance >= _minGain)
        {
            _best = distance;
            _stalledMs = 0;
            return false;
        }
        if (!waiting) _stalledMs += nowMs - last;
        return _stalledMs >= _stallMs;
    }
}
