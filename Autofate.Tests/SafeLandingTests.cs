using System.Numerics;
using Autofate.Logic;
using Xunit;

namespace Autofate.Tests;

public class SafeLandingTests
{
    private static Vector3 P(float x, float z) => new(x, 0, z);

    [Fact]
    public void AvoidsSpotsNextToMobs()
    {
        var spots = new[] { P(0, 0), P(30, 0) };
        var mobs = new[] { P(5, 0) };
        Assert.Equal(P(30, 0), SafeLanding.Pick(me: P(-100, 0), spots, mobs));
    }

    [Fact]
    public void AmongClearSpots_TakesTheClosestToUs()
    {
        var spots = new[] { P(40, 0), P(-40, 0) };
        var mobs = new[] { P(0, 0) };
        Assert.Equal(P(-40, 0), SafeLanding.Pick(me: P(-100, 0), spots, mobs));
    }

    [Fact]
    public void NothingClear_TakesTheMostOpenSpot()
    {
        var spots = new[] { P(0, 0), P(10, 0), P(18, 0) };
        var mobs = new[] { P(2, 0), P(30, 0) };
        Assert.Equal(P(18, 0), SafeLanding.Pick(me: P(0, 0), spots, mobs)); // 12y from the nearest mob, vs 8y and 2y
    }

    [Fact]
    public void NoMobs_TakesTheClosest()
    {
        var spots = new[] { P(40, 0), P(20, 0) };
        Assert.Equal(P(20, 0), SafeLanding.Pick(me: P(0, 0), spots, Array.Empty<Vector3>()));
    }
}
