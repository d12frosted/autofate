using System.Numerics;
using Autofate.Logic;
using Xunit;

namespace Autofate.Tests;

public class FateLandingTests
{
    // The spot from the Ultima Thule log: 'Staring into the Void', dropoff on the island's surface.
    private static readonly Vector3 Dropoff = new(293.8f, 294.1f, -312.9f);

    [Fact]
    public void UnderTheIsland_IsNotArrival()
    {
        // Where vnav left us: 4y from the dropoff, but through the floor.
        var me = new Vector3(291.0f, 290.5f, -313.0f);
        Assert.Equal(FateLanding.State.UnderFloor, FateLanding.Classify(me, Dropoff));
    }

    [Fact]
    public void HoveringAboveTheDropoff_IsArrival()
    {
        Assert.Equal(FateLanding.State.Arrived, FateLanding.Classify(Dropoff + new Vector3(1, 5, 1), Dropoff));
    }

    [Fact]
    public void StandingOnTheDropoff_IsArrival()
    {
        Assert.Equal(FateLanding.State.Arrived, FateLanding.Classify(Dropoff + new Vector3(2, 0.3f, -2), Dropoff));
    }

    [Fact]
    public void FarAboveTheDropoff_IsNotArrival()
    {
        // Same X/Z but 40y up: in Elpis that's another island, and dismounting would land us on it.
        Assert.Equal(FateLanding.State.EnRoute, FateLanding.Classify(Dropoff + new Vector3(0, 40, 0), Dropoff));
    }

    [Fact]
    public void HorizontallyAway_IsEnRoute()
    {
        Assert.Equal(FateLanding.State.EnRoute, FateLanding.Classify(Dropoff + new Vector3(30, 0, 0), Dropoff));
        // Below the floor but not under the spot yet: still just en route.
        Assert.Equal(FateLanding.State.EnRoute, FateLanding.Classify(Dropoff + new Vector3(30, -10, 0), Dropoff));
    }

    [Fact]
    public void FlightTarget_IsAboveTheFloor()
    {
        var t = FateLanding.FlightTarget(Dropoff, climbingOut: false);
        Assert.Equal(Dropoff.X, t.X);
        Assert.Equal(Dropoff.Z, t.Z);
        Assert.True(t.Y - Dropoff.Y > FateLanding.ArriveRadius, "the target must be further above the floor than vnav's stop range");
        Assert.Equal(FateLanding.State.Arrived, FateLanding.Classify(t, Dropoff));
    }

    [Fact]
    public void ClimbOutTarget_IsWellAboveTheHoverTarget()
    {
        var hover = FateLanding.FlightTarget(Dropoff, climbingOut: false);
        var climb = FateLanding.FlightTarget(Dropoff, climbingOut: true);
        Assert.True(climb.Y > hover.Y + 10);
    }

    [Fact]
    public void ClimbOut_EndsOnceAboveTheFloor()
    {
        Assert.False(FateLanding.ClimbedOut(Dropoff + new Vector3(0, -3, 0), Dropoff));
        Assert.False(FateLanding.ClimbedOut(Dropoff + new Vector3(0, 1, 0), Dropoff));
        Assert.True(FateLanding.ClimbedOut(Dropoff + new Vector3(0, 6, 0), Dropoff));
    }
}
