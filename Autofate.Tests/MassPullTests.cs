using System.Numerics;
using Autofate.Logic;
using Xunit;

namespace Autofate.Tests;

public class MassPullTests
{
    private const float Radius = 20f;
    private static readonly Vector3 Me = Vector3.Zero;

    private static MassPull.Enemy Mob(ulong id, float x, bool onUs = false)
        => new(id, new Vector3(x, 0, 0), onUs);

    [Fact]
    public void EmptyPile_WalksToNearestMob_HoweverFar()
    {
        var enemies = new[] { Mob(1, 150), Mob(2, 90) };
        Assert.Equal(2UL, MassPull.PickNext(Me, enemies, Radius, sticky: 0));
    }

    [Fact]
    public void WithPile_IgnoresMobsOutsideRadius()
    {
        // A is on us; B is far away. Running to B would drag us away from A until A drops off,
        // and then we'd run back to A: the ping-pong. Fight what we have instead.
        var enemies = new[] { Mob(1, 3, onUs: true), Mob(2, 120) };
        Assert.Null(MassPull.PickNext(Me, enemies, Radius, sticky: 0));
    }

    [Fact]
    public void WithPile_PullsNearbyMob()
    {
        var enemies = new[] { Mob(1, 3, onUs: true), Mob(2, 15), Mob(3, 12) };
        Assert.Equal(3UL, MassPull.PickNext(Me, enemies, Radius, sticky: 0));
    }

    [Fact]
    public void KeepsStickyTarget_WhileValid()
    {
        var enemies = new[] { Mob(1, 3, onUs: true), Mob(2, 15), Mob(3, 12) };
        Assert.Equal(2UL, MassPull.PickNext(Me, enemies, Radius, sticky: 2));
    }

    [Fact]
    public void DropsStickyTarget_OnceItHasUs()
    {
        var enemies = new[] { Mob(2, 5, onUs: true), Mob(3, 12) };
        Assert.Equal(3UL, MassPull.PickNext(Me, enemies, Radius, sticky: 2));
    }

    [Fact]
    public void DropsStickyTarget_WhenItLeavesTheRadiusWithAPile()
    {
        var enemies = new[] { Mob(1, 3, onUs: true), Mob(2, 60) };
        Assert.Null(MassPull.PickNext(Me, enemies, Radius, sticky: 2));
    }

    [Fact]
    public void DropsStickyTarget_WhenItIsGone()
    {
        var enemies = new[] { Mob(3, 12) };
        Assert.Equal(3UL, MassPull.PickNext(Me, enemies, Radius, sticky: 2));
    }

    [Fact]
    public void NothingLeftToPull_ReturnsNull()
    {
        var enemies = new[] { Mob(1, 3, onUs: true), Mob(2, 4, onUs: true) };
        Assert.Null(MassPull.PickNext(Me, enemies, Radius, sticky: 0));
        Assert.Null(MassPull.PickNext(Me, Array.Empty<MassPull.Enemy>(), Radius, sticky: 0));
    }
}
