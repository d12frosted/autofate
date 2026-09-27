using System.Numerics;
using Autofate.Logic;
using Xunit;

namespace Autofate.Tests;

public class KiteTests
{
    private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));

    private static TrackedMob Mob(ulong id, Vector3 p, float vx = 0, float vz = 0)
        => new(id, p, new Vector3(vx, 0, vz), MobState.Idle);

    private static TrackedMob[] With(TrackedMob target, params Vector3[] others)
        => others.Select((o, i) => Mob((ulong)(100 + i), o)).Prepend(target).ToArray();

    private static readonly TrackedMob Target = Mob(1, Vector3.Zero);

    [Fact]
    public void PullSpot_IsInRangeOfTheTarget_AndClearOfItsNeighbours()
    {
        var target = new Vector3(0, 0, 0);
        var neighbours = new[] { new Vector3(6, 0, 0), new Vector3(5, 0, 5) };
        var (spot, clear) = Kite.PullSpot(me: new Vector3(-40, 0, 0), Target, With(Target, neighbours));

        Assert.True(clear);
        Assert.Equal(Kite.PullRange, Flat(spot, target), 1);
        foreach (var n in neighbours)
            Assert.True(Flat(spot, n) >= Kite.SafeGap, $"spot {spot} is {Flat(spot, n):F1}y from neighbour {n}");
    }

    [Fact]
    public void PullSpot_PrefersTheSafeSpotClosestToUs()
    {
        // Neighbour to the north; both west and east of the target are safe, we're to the east.
        var (spot, _) = Kite.PullSpot(me: new Vector3(50, 0, 0), Target, With(Target, new Vector3(0, 0, 6)));
        Assert.True(spot.X > 10, $"expected a spot on our (east) side, got {spot}");
    }

    [Fact]
    public void PullSpot_WhenSurrounded_TakesTheLeastCrowdedSide()
    {
        // Neighbours all around but one side is much more open: pick the spot furthest from any of them.
        var target = new Vector3(0, 0, 0);
        var neighbours = new[] { new Vector3(8, 0, 0), new Vector3(0, 0, 8), new Vector3(0, 0, -8), new Vector3(-20, 0, 0) };
        var (spot, clear) = Kite.PullSpot(me: new Vector3(0, 0, 50), Target, With(Target, neighbours));
        Assert.False(clear);
        var nearest = neighbours.Min(n => Flat(spot, n));
        // No spot at 18y clears 15y from everything, but the best one keeps a clear margin.
        Assert.True(nearest > 10, $"spot {spot} is only {nearest:F1}y from a neighbour");
    }

    [Fact]
    public void PullSpot_KeepsTheTargetsHeight()
    {
        var high = Mob(1, new Vector3(0, 12, 0));
        var (spot, _) = Kite.PullSpot(new Vector3(40, 3, 0), high, With(high, new Vector3(-6, 12, 0)));
        Assert.Equal(12f, spot.Y);
    }

    [Fact]
    public void PullSpot_IsNotClear_WhenTheOnlyWayRunsThroughThePack()
    {
        // We're on the pack's side: every spot clear of it means walking through it.
        var (_, clear) = Kite.PullSpot(me: new Vector3(40, 0, 0), Target,
            With(Target, new Vector3(6, 0, 0), new Vector3(5, 0, 5), new Vector3(5, 0, -5)));
        Assert.False(clear);
    }

    [Fact]
    public void PullsFromWhereWeStand_WhenInRangeAndClear()
    {
        var target = new Vector3(0, 0, 0);
        var others = With(Target, new Vector3(-5, 0, 0));      // the target's pack, behind it
        Assert.True(Kite.CanPullFromHere(me: new Vector3(16, 0, 0), Target, others));
    }

    [Fact]
    public void DoesNotPullFromHere_WhenOutOfRange()
    {
        Assert.False(Kite.CanPullFromHere(me: new Vector3(25, 0, 0), Target, new[] { Target }));
    }

    [Fact]
    public void DoesNotPullFromHere_WhenAnotherMobIsCloseToUs()
    {
        // In range of the target, but standing next to another idle mob: pulling from here pulls both.
        var others = With(Target, new Vector3(16, 0, 8));
        Assert.False(Kite.CanPullFromHere(me: new Vector3(16, 0, 0), Target, others));
    }

    [Fact]
    public void DoesNotPullFromHere_WhenAMobIsWalkingTowardsUs()
    {
        // Clear right now, but a wanderer 26y away walks our way at 3y/s.
        var mobs = new[] { Target, Mob(2, new Vector3(16, 0, 26), vz: -3) };
        Assert.False(Kite.CanPullFromHere(me: new Vector3(16, 0, 0), Target, mobs));
    }

    [Fact]
    public void PullSpot_AvoidsTheWayAMobIsWandering()
    {
        // The drawing: we're south of the target (mob 1); a mob 2 wanders down towards the north
        // side. The spot must be clear of where it's going and so must the walk there.
        var me = new Vector3(0, 0, -30);
        var mobs = new[] { Target, Mob(2, new Vector3(0, 0, 55), vz: -3) };
        var (spot, clear) = Kite.PullSpot(me, Target, mobs);
        Assert.True(clear);
        Assert.True(ThreatMap.Clearance(spot, mobs, exclude: Target.Id) >= Kite.SafeGap);
        Assert.True(ThreatMap.PathClearance(me, spot, mobs, exclude: Target.Id) >= Kite.SafeGap);
    }

    [Fact]
    public void SpotStillSafe_TurnsFalse_WhenAMobWandersOntoOurWay()
    {
        var spot = new Vector3(0, 0, 18);
        var me = new Vector3(0, 0, -5);
        Assert.True(Kite.SpotStillSafe(me, spot, Target, new[] { Target, Mob(2, new Vector3(40, 0, 18)) }));
        Assert.False(Kite.SpotStillSafe(me, spot, Target, new[] { Target, Mob(2, new Vector3(30, 0, 18), vx: -4) }));
    }

    [Theory]
    [InlineData(32u, 3624u)]   // DRK Unmend
    [InlineData(22u, 90u)]     // DRG Piercing Talon
    [InlineData(4u, 90u)]      // LNC Piercing Talon
    [InlineData(19u, 24u)]     // PLD Shield Lob
    [InlineData(1u, 24u)]      // GLA Shield Lob
    [InlineData(21u, 46u)]     // WAR Tomahawk
    [InlineData(3u, 46u)]      // MRD Tomahawk
    [InlineData(37u, 16143u)]  // GNB Lightning Shot
    [InlineData(34u, 7486u)]   // SAM Enpi
    [InlineData(30u, 2247u)]   // NIN Throwing Dagger
    [InlineData(29u, 2247u)]   // ROG Throwing Dagger
    [InlineData(39u, 24386u)]  // RPR Harpe
    [InlineData(41u, 34632u)]  // VPR Writhing Snap
    public void RangedPullAction_ForMeleeJobs(uint classJob, uint action)
        => Assert.Equal(action, Kite.RangedPullAction(classJob));

    [Theory]
    [InlineData(20u)] // MNK: no ranged attack
    [InlineData(2u)]  // PGL
    [InlineData(25u)] // BLM: ranged anyway, pulls with its normal attack
    public void RangedPullAction_NoneOtherwise(uint classJob)
        => Assert.Null(Kite.RangedPullAction(classJob));
}
