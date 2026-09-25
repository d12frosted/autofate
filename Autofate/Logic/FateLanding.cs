using System.Numerics;

namespace Autofate.Logic;

/// <summary>
/// The last leg of fate travel: getting onto the landing spot (the "dropoff", a point on the floor
/// inside the fate) from the right side of the floor.
///
/// vnavmesh stops a fly-to once it is within the stop range of the target, in 3D. Aimed at a point
/// ON a floating island's surface, it can get "within range" from underneath, through the floor,
/// and consider itself done: we then hover under the island forever (seen in Ultima Thule and
/// Elpis). So we aim the flight at a point above the floor, and only call it arrived when we're at
/// the spot horizontally and at its height or a little above.
/// </summary>
public static class FateLanding
{
    /// <summary>Horizontal distance from the dropoff that counts as being on it.</summary>
    public const float ArriveRadius = 4f;

    /// <summary>How far above the floor we aim a flight: more than vnav's stop range, so it can't stop through the floor.</summary>
    private const float HoverHeight = 6f;
    /// <summary>Where we aim while climbing out from under the floor. Open air well above it, so vnav routes around the island.</summary>
    private const float ClimbOutHeight = 30f;
    /// <summary>Below the dropoff by more than this, we're under the floor rather than standing on a slope.</summary>
    private const float BelowTolerance = 2f;
    /// <summary>Above the dropoff by more than this, we're not about to land on it (in Elpis, likely another island).</summary>
    private const float AboveTolerance = HoverHeight + 6f;

    public enum State { EnRoute, Arrived, UnderFloor }

    public static State Classify(Vector3 me, Vector3 dropoff)
    {
        if (Horizontal(me, dropoff) > ArriveRadius) return State.EnRoute;
        var dy = me.Y - dropoff.Y;
        if (dy < -BelowTolerance) return State.UnderFloor;
        if (dy > AboveTolerance) return State.EnRoute;
        return State.Arrived;
    }

    /// <summary>Where to fly: above the dropoff, and much higher while climbing out from under the floor.</summary>
    public static Vector3 FlightTarget(Vector3 dropoff, bool climbingOut)
        => dropoff + new Vector3(0, climbingOut ? ClimbOutHeight : HoverHeight, 0);

    /// <summary>Climbing out is done once we're clearly above the floor.</summary>
    public static bool ClimbedOut(Vector3 me, Vector3 dropoff) => me.Y - dropoff.Y >= HoverHeight - 1f;

    private static float Horizontal(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
}
