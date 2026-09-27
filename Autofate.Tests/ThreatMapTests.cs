using System.Numerics;
using Autofate.Logic;
using Xunit;

namespace Autofate.Tests;

public class MobTrackerTests
{
    private static MobTracker.Sample S(ulong id, float x, float z, MobState st = MobState.Idle) => new(id, new Vector3(x, 0, z), st);

    [Fact]
    public void EstimatesVelocity()
    {
        var t = new MobTracker();
        t.Update(0, new[] { S(1, 0, 0) });
        t.Update(1000, new[] { S(1, 3, 0) });
        t.Update(2000, new[] { S(1, 6, 0) });
        var m = Assert.Single(t.Mobs);
        Assert.InRange(m.Velocity.X, 2.0f, 3.1f);
        Assert.Equal(0f, m.Velocity.Z, 2);
    }

    [Fact]
    public void ForgetsMobsThatAreGone_AndKeepsState()
    {
        var t = new MobTracker();
        t.Update(0, new[] { S(1, 0, 0), S(2, 5, 5) });
        t.Update(500, new[] { S(2, 5, 5, MobState.OnUs) });
        var m = Assert.Single(t.Mobs);
        Assert.Equal(2UL, m.Id);
        Assert.Equal(MobState.OnUs, m.State);
    }

    [Fact]
    public void StandingStill_HasNoVelocity()
    {
        var t = new MobTracker();
        for (var ms = 0; ms <= 3000; ms += 250) t.Update(ms, new[] { S(1, 10, 10) });
        Assert.Equal(0f, Assert.Single(t.Mobs).Velocity.Length(), 3);
    }
}

public class ThreatMapTests
{
    private static TrackedMob Idle(ulong id, float x, float z, float vx = 0, float vz = 0)
        => new(id, new Vector3(x, 0, z), new Vector3(vx, 0, vz), MobState.Idle);

    private static Vector3 P(float x, float z) => new(x, 0, z);

    [Fact]
    public void Clearance_OfAStillMob_IsItsDistance()
        => Assert.Equal(30f, ThreatMap.Clearance(P(0, 0), new[] { Idle(1, 30, 0) }), 1);

    [Fact]
    public void Clearance_SeesAMobWalkingTowardsUs()
    {
        // 30y away, walking at us at 3y/s: within the 3s horizon it gets to 21y.
        var c = ThreatMap.Clearance(P(0, 0), new[] { Idle(1, 30, 0, vx: -3) });
        Assert.InRange(c, 20.5f, 21.5f);
    }

    [Fact]
    public void Clearance_IgnoresMobsWalkingAway_AndNonIdleOnes()
    {
        var mobs = new[]
        {
            Idle(1, 30, 0, vx: 3),
            new TrackedMob(2, P(2, 0), Vector3.Zero, MobState.OnUs),
            new TrackedMob(3, P(3, 0), Vector3.Zero, MobState.Busy),
        };
        Assert.Equal(30f, ThreatMap.Clearance(P(0, 0), mobs), 1);
    }

    [Fact]
    public void Clearance_CanExcludeTheTarget()
        => Assert.Equal(float.MaxValue, ThreatMap.Clearance(P(0, 0), new[] { Idle(7, 5, 0) }, exclude: 7));

    [Fact]
    public void PathClearance_CatchesAMobWanderingOntoOurWay()
    {
        // The drawing: we walk north from (0,0) to (0,30); mob 2 starts at (25,40) and wanders
        // south-west. Standing at either end looks fine, the walk itself runs into it.
        var mob2 = Idle(2, 20, 40, vx: -3, vz: -3);
        Assert.True(ThreatMap.PathClearance(P(0, 0), P(0, 30), new[] { mob2 }) < ThreatMap.AggroRadius);
    }

    [Fact]
    public void PathClearance_OfAPathAwayFromMobs_IsLarge()
        => Assert.True(ThreatMap.PathClearance(P(0, 0), P(0, -30), new[] { Idle(2, 0, 40) }) >= 40f);

    [Fact]
    public void Settled_WhenIdleMobsOnlyWander()
        => Assert.True(ThreatMap.IsSettled(P(0, 0), new[] { Idle(1, 10, 0, vx: 2), Idle(2, 20, 5) }));

    [Fact]
    public void NotSettled_WhileAMobNearbyRuns()
        => Assert.False(ThreatMap.IsSettled(P(0, 0), new[] { Idle(1, 10, 0, vx: 6) }));

    [Fact]
    public void Settled_IgnoresRunnersFarAway_AndMobsOnUs()
        => Assert.True(ThreatMap.IsSettled(P(0, 0), new[]
        {
            Idle(1, 90, 0, vx: 6),
            new TrackedMob(2, P(5, 0), new Vector3(6, 0, 0), MobState.OnUs),
        }));

    [Fact]
    public void Crowd_CountsMobsHeadingIntoTheTargetsArea()
    {
        var target = Idle(1, 0, 0);
        var mobs = new[] { target, Idle(2, 30, 0, vx: -4), Idle(3, 60, 0), Idle(4, 10, 0) };
        // mob 4 is already next to it, mob 2 walks into range within the horizon, mob 3 stays away.
        Assert.Equal(2, ThreatMap.Crowd(target, mobs));
    }
}
