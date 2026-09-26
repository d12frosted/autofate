using System.Numerics;
using Autofate.Logic;
using Xunit;

namespace Autofate.Tests;

public class PullStyleTests
{
    [Theory]
    [InlineData(PullStyle.Auto, 1, PullStyle.Yolo)]  // tank
    [InlineData(PullStyle.Auto, 2, PullStyle.Safe)]  // melee DPS
    [InlineData(PullStyle.Auto, 3, PullStyle.Safe)]  // ranged DPS
    [InlineData(PullStyle.Auto, 4, PullStyle.Safe)]  // healer
    [InlineData(PullStyle.Auto, 0, PullStyle.Safe)]  // unknown / not logged in
    [InlineData(PullStyle.Yolo, 4, PullStyle.Yolo)]  // explicit choice wins
    [InlineData(PullStyle.Safe, 1, PullStyle.Safe)]
    public void Resolve(PullStyle chosen, byte role, PullStyle expected)
        => Assert.Equal(expected, SafePull.Resolve(chosen, role));

    private static SafePull.Mob M(ulong id, float x, float z = 0) => new(id, new Vector3(x, 0, z));

    [Fact]
    public void PicksNearest_WhenNothingIsAround()
    {
        var pick = SafePull.PickTarget(Vector3.Zero, new[] { M(1, 30), M(2, 20) }, idleHostiles: Array.Empty<SafePull.Mob>());
        Assert.Equal(2UL, pick);
    }

    [Fact]
    public void AvoidsMobStandingInAPack()
    {
        // Mob 2 is closer, but two idle mobs stand right next to it: engaging it pulls all three.
        var candidates = new[] { M(1, 60), M(2, 20) };
        var idle = new[] { M(2, 20), M(3, 24), M(4, 22, 3), M(1, 60) };
        Assert.Equal(1UL, SafePull.PickTarget(Vector3.Zero, candidates, idle));
    }

    [Fact]
    public void IgnoresHostilesOutsideTheCrowdRadius()
    {
        var candidates = new[] { M(1, 40), M(2, 20) };
        var idle = new[] { M(3, 20 + SafePull.CrowdRadius + 5) };
        Assert.Equal(2UL, SafePull.PickTarget(Vector3.Zero, candidates, idle));
    }

    [Fact]
    public void StillPicksSomething_WhenEverythingIsPacked()
    {
        var candidates = new[] { M(1, 20), M(2, 60) };
        var idle = new[] { M(3, 22), M(4, 58), M(5, 62), M(6, 61, 2) };
        Assert.Equal(1UL, SafePull.PickTarget(Vector3.Zero, candidates, idle));
    }

    [Fact]
    public void CrowdRadius_CoversObservedAggroRange()
        // A Wild Ibruq in Yak T'el aggroed from over 21y; 15y missed it.
        => Assert.True(SafePull.CrowdRadius >= 20f);

    [Theory]
    [InlineData(1.0f, true)]
    [InlineData(0.6f, true)]
    [InlineData(0.59f, false)]
    [InlineData(0.2f, false)]
    public void NewPulls_WaitForHp(float hp, bool expected)
        => Assert.Equal(expected, SafePull.MayStartNewPull(hp));

    [Fact]
    public void NoCandidates_ReturnsNull()
        => Assert.Null(SafePull.PickTarget(Vector3.Zero, Array.Empty<SafePull.Mob>(), Array.Empty<SafePull.Mob>()));
}
