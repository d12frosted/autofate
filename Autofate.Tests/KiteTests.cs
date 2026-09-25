using System.Numerics;
using Autofate.Logic;
using Xunit;

namespace Autofate.Tests;

public class KiteTests
{
    private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));

    [Fact]
    public void PullSpot_IsInRangeOfTheTarget_AndClearOfItsNeighbours()
    {
        var target = new Vector3(0, 0, 0);
        var neighbours = new[] { new Vector3(6, 0, 0), new Vector3(5, 0, 5) };
        var spot = Kite.PullSpot(me: new Vector3(40, 0, 0), target, neighbours);

        Assert.Equal(Kite.PullRange, Flat(spot, target), 1);
        foreach (var n in neighbours)
            Assert.True(Flat(spot, n) >= Kite.SafeGap, $"spot {spot} is {Flat(spot, n):F1}y from neighbour {n}");
    }

    [Fact]
    public void PullSpot_PrefersTheSafeSpotClosestToUs()
    {
        // Neighbour to the north; both west and east of the target are safe, we're to the east.
        var spot = Kite.PullSpot(me: new Vector3(50, 0, 0), new Vector3(0, 0, 0), new[] { new Vector3(0, 0, 6) });
        Assert.True(spot.X > 10, $"expected a spot on our (east) side, got {spot}");
    }

    [Fact]
    public void PullSpot_WhenSurrounded_TakesTheLeastCrowdedSide()
    {
        // Neighbours all around but one side is much more open: pick the spot furthest from any of them.
        var target = new Vector3(0, 0, 0);
        var neighbours = new[] { new Vector3(8, 0, 0), new Vector3(0, 0, 8), new Vector3(0, 0, -8), new Vector3(-20, 0, 0) };
        var spot = Kite.PullSpot(me: new Vector3(0, 0, 50), target, neighbours);
        var nearest = neighbours.Min(n => Flat(spot, n));
        // No spot at 18y clears 15y from everything, but the best one keeps a clear margin.
        Assert.True(nearest > 10, $"spot {spot} is only {nearest:F1}y from a neighbour");
    }

    [Fact]
    public void PullSpot_KeepsTheTargetsHeight()
    {
        var spot = Kite.PullSpot(new Vector3(40, 3, 0), new Vector3(0, 12, 0), new[] { new Vector3(-6, 12, 0) });
        Assert.Equal(12f, spot.Y);
    }

    [Fact]
    public void RetreatSpot_ContinuesAwayFromTheTarget()
    {
        var target = new Vector3(0, 0, 0);
        var pull = new Vector3(18, 0, 0);
        var retreat = Kite.RetreatSpot(pull, target);
        Assert.Equal(18 + Kite.RetreatDistance, retreat.X, 1);
        Assert.Equal(0, retreat.Z, 1);
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
