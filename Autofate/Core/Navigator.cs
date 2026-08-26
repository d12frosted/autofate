using System.Numerics;
using Autofate.Features;
using Autofate.IPC;
using ECommons.DalamudServices;
using ECommons.GameHelpers;

namespace Autofate.Core;

/// <summary>
/// Movement over vnavmesh that auto-mounts and (where allowed) flies for long trips. Call
/// <see cref="MoveTo"/> each tick toward a target.
/// </summary>
public static class Navigator
{
    private static Vector3 _currentDest;
    private static Vector3 _lastIssuedDest; // last dest we actually sent to vnavmesh (re-issue when this changes)
    private static bool _active;
    // When we started waiting on an in-flight vnavmesh pathfind (0 = not waiting).
    private static long _pathfindWaitStartMs;
    private const long PathfindWaitCapMs = 60000;
    // How far the destination must shift before we re-issue a path. Keeps us from spamming
    // PathfindAndMoveCloseTo for tiny mob/NPC drift each frame.
    private const float RepathThreshold = 2f;

    /// <summary>Distance to the current destination, or float.MaxValue if not navigating.</summary>
    public static float DistanceToDest()
    {
        var me = Player.Object;
        if (me == null || !_active) return float.MaxValue;
        return Vector3.Distance(me.Position, _currentDest);
    }

    public static bool IsNavigating => _active && NavmeshIPC.IsRunning();

    /// <summary>
    /// Drive movement toward <paramref name="dest"/>, stopping within <paramref name="stopRange"/>.
    /// Handles mounting/flying. Returns true once we are within range of the destination.
    /// Call repeatedly (idempotent / throttled internally by vnavmesh).
    /// </summary>
    public static bool MoveTo(Configuration c, Vector3 dest, float stopRange = 3f, bool allowMount = true)
    {
        var me = Player.Object;
        if (me == null) return false;

        _currentDest = dest;
        _active = true;

        var dist = Vector3.Distance(me.Position, dest);
        if (dist <= stopRange)
        {
            Stop();
            return true;
        }

        // Never move until the zone navmesh has finished building, or vnavmesh can't path.
        if (!NavmeshIPC.MeshReady())
        {
            if (ECommons.Throttlers.EzThrottler.Throttle("AF_NavBuilding", 3000))
            {
                var p = NavmeshIPC.BuildProgress();
                Svc.Log.Debug(p is >= 0 and < 1
                    ? $"[Navigator] Waiting for navmesh build: {p * 100:0}%"
                    : "[Navigator] Waiting for navmesh to be ready...");
            }
            return false;
        }

        var fly = MountManager.ShouldFly(c);

        // Mount for long trips if configured. allowMount=false for short hops to avoid a mount loop.
        if (allowMount && c.UseMount && !MountManager.IsMounted && dist > c.MountDistanceThreshold && MountManager.CanMountHere)
        {
            MountManager.Mount(c);
            // Hold off on pathing while the mount animation plays.
            return false;
        }

        // Sprint whenever travelling on foot; no-ops if already sprinting/mounted/occupied.
        if (!MountManager.IsMounted)
            MountManager.Sprint();

        // WAIT OUT AN IN-FLIGHT PATHFIND. vnavmesh runs one pathfind at a time on a worker and
        // REJECTS anything sent while it is busy ("Pathfinding task is in progress..."), so
        // re-issuing during that window achieves nothing. Long flying paths across a big zone can
        // take tens of seconds here, and re-issuing every tick both spams the log and throws away
        // the result we're waiting for. Sit still, let it finish, then act on it.
        if (NavmeshIPC.PathfindInProgress())
        {
            var waitedMs = _pathfindWaitStartMs == 0 ? 0 : Environment.TickCount64 - _pathfindWaitStartMs;
            if (_pathfindWaitStartMs == 0) _pathfindWaitStartMs = Environment.TickCount64;
            // Escape hatch: the flag is reported by vnavmesh, not by us, so never let it wedge
            // movement forever. Past PathfindWaitCapMs we fall through and issue anyway.
            if (waitedMs < PathfindWaitCapMs)
            {
                if (c.VerboseLogging && ECommons.Throttlers.EzThrottler.Throttle("AF_NavPathfinding", 5000))
                    Svc.Log.Information($"[Diag/Travel] waiting for vnavmesh pathfind ({dist:F0}y to go, fly={fly}, waited {waitedMs / 1000}s)");
                return false;
            }
            if (ECommons.Throttlers.EzThrottler.Throttle("AF_NavPathfindingCap", 10000))
                Svc.Log.Warning($"[Navigator] vnavmesh has reported a pathfind in progress for {waitedMs / 1000}s; issuing anyway.");
        }
        else
        {
            _pathfindWaitStartMs = 0;
        }

        // Hand the destination to vnavmesh; let it perform takeoff itself (don't manually jump).
        // RE-ISSUE when the destination has shifted meaningfully since the last path we sent —
        // otherwise after killing mob A we'd stay idling toward A's last position while already
        // targeting mob B (vnav.IsRunning was still true, so we'd skip re-pathing and stand still
        // staring at the new target). We also issue when nothing is running.
        var destChanged = Vector3.DistanceSquared(_lastIssuedDest, dest) > RepathThreshold * RepathThreshold;
        var idle = !NavmeshIPC.IsRunning();
        if (idle || destChanged)
        {
            NavmeshIPC.PathfindAndMoveCloseTo(dest, stopRange, fly && MountManager.IsMounted);
            _lastIssuedDest = dest;
        }

        return false;
    }

    /// <summary>
    /// Follow a moving target (party leader): re-issues the path as the target drifts so we chase a
    /// walking player. No mounting / stop-on-arrival.
    /// </summary>
    public static void FollowMoveTo(Configuration c, Vector3 target, float followDistance)
    {
        var me = Player.Object;
        if (me == null) return;
        if (!NavmeshIPC.MeshReady()) return;

        _active = true;
        var dist = Vector3.Distance(me.Position, target);

        // Close enough: stop and don't jitter.
        if (dist <= Math.Max(1f, followDistance))
        {
            NavmeshIPC.Stop();
            return;
        }

        // Fly to keep pace with a flying leader; fly=true once mounted so vnav starts takeoff.
        var fly = c.UseFlight && MountManager.CanFlyHere && MountManager.IsMounted;

        if (!MountManager.IsMounted)
        {
            // Sprint on foot to keep up.
            MountManager.Sprint();
        }
        else if (fly && !MountManager.IsFlying)
        {
            // Mounted but grounded and we want to fly: take off immediately.
            MountManager.EnsureAirborne(c);
        }

        // Use direct Path.MoveTo (not async A* pathfind) so frequent re-issues don't cancel a
        // pending pathfind. Re-issue when not moving or the target drifts (throttled 100ms).
        var drift = Vector3.Distance(_currentDest, target);
        if (!NavmeshIPC.IsRunning() || drift > 1f)
        {
            if (ECommons.Throttlers.EzThrottler.Throttle("AF_FollowRepath", 100))
            {
                _currentDest = target;
                NavmeshIPC.MoveTo(new List<Vector3> { target }, fly);
            }
        }
    }

    public static void Stop()
    {
        if (_active)
        {
            NavmeshIPC.Stop();
            _active = false;
        }
        // Clear the last-issued dest so the next MoveTo unconditionally re-issues a path (even if
        // it targets roughly the same position as before).
        _lastIssuedDest = default;
        _pathfindWaitStartMs = 0;
    }
}
