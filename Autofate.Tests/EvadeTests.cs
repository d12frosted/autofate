using System.Numerics;
using Autofate.Logic;
using Xunit;

namespace Autofate.Tests;

public class EvadeTests
{
    private static TrackedMob Idle(ulong id, float x, float z, float vx = 0, float vz = 0)
        => new(id, new Vector3(x, 0, z), new Vector3(vx, 0, vz), MobState.Idle);

    private static readonly Vector3 Me = Vector3.Zero;

    [Fact]
    public void StaysPut_WhenNothingIsComing()
        => Assert.Null(ThreatMap.EvadeSpot(Me, new[] { Idle(1, 40, 0), Idle(2, 0, -35, vz: -2) }));

    [Fact]
    public void StepsAway_FromAWandererHeadingOurWay()
    {
        // The drawing: mob 2 wanders down at us while we wait for mob 1 to come.
        var mob2 = Idle(2, 0, 28, vz: -3);
        var spot = ThreatMap.EvadeSpot(Me, new[] { mob2 });
        Assert.NotNull(spot);
        Assert.True(ThreatMap.PathClearance(Me, spot!.Value, new[] { mob2 }) > ThreatMap.Clearance(Me, new[] { mob2 }));
        Assert.True(spot.Value.Z < 0, $"expected to step away (south), got {spot}");
    }

    [Fact]
    public void IgnoresMobsOnUs()
    {
        // A mob that's already ours coming at us is the point of kiting, not a reason to move.
        var ours = new TrackedMob(1, new Vector3(0, 0, 10), new Vector3(0, 0, -5), MobState.OnUs);
        Assert.Null(ThreatMap.EvadeSpot(Me, new[] { ours }));
    }

    [Fact]
    public void DoesNotMove_WhenNowhereIsBetter()
    {
        // Mobs all around, all closing in: no step makes it meaningfully better, so don't dance.
        var mobs = new[] { Idle(1, 15, 0, vx: -1), Idle(2, -15, 0, vx: 1), Idle(3, 0, 15, vz: -1), Idle(4, 0, -15, vz: 1) };
        var spot = ThreatMap.EvadeSpot(Me, mobs);
        if (spot is { } s)
            Assert.True(ThreatMap.PathClearance(Me, s, mobs) >= ThreatMap.Clearance(Me, mobs) + 3f);
    }
}
