using Autofate.Logic;
using Xunit;

namespace Autofate.Tests;

public class TravelProgressTests
{
    private static TravelProgress New() => new(stallMs: 20000, minGain: 10f);

    [Fact]
    public void GettingCloser_IsNeverStalled()
    {
        var p = New();
        for (long t = 0, d = 500; t <= 120000; t += 1000, d -= 4)
            Assert.False(p.Update(t, d, waiting: false), $"stalled at t={t}");
    }

    [Fact]
    public void NoProgress_StallsAfterTheWindow()
    {
        var p = New();
        Assert.False(p.Update(0, 97, false));
        Assert.False(p.Update(19000, 96, false));
        Assert.True(p.Update(21000, 97, false));
    }

    [Fact]
    public void WaitingOnAPathfind_DoesNotCount()
    {
        var p = New();
        p.Update(0, 97, false);
        // A 30s pathfind: time spent waiting is not time spent failing to move.
        for (long t = 1000; t <= 30000; t += 1000) Assert.False(p.Update(t, 97, waiting: true));
        Assert.False(p.Update(31000, 97, false));
        Assert.True(p.Update(52000, 97, false));
    }

    [Fact]
    public void TheUltimaThuleLoop_Stalls()
    {
        // From the log: pathfind ~1.5s, then ~4.5s of backing out and retrying, distance stuck at 92-99y.
        var p = New();
        var stalled = false;
        long t = 0;
        for (var cycle = 0; cycle < 10 && !stalled; cycle++)
        {
            for (var i = 0; i < 3; i++) { t += 500; stalled |= p.Update(t, 97, waiting: true); }
            for (var i = 0; i < 9; i++) { t += 500; stalled |= p.Update(t, 92 + (i % 7), waiting: false); }
        }
        Assert.True(stalled, "a stuck retry loop must eventually count as stalled");
        Assert.True(t < 40000, $"took {t}ms to notice");
    }

    [Fact]
    public void SmallJitter_IsNotProgress()
    {
        var p = New();
        p.Update(0, 100, false);
        p.Update(10000, 95, false);   // 5y: not enough
        Assert.True(p.Update(21000, 96, false));
    }

    [Fact]
    public void Reset_StartsOver()
    {
        var p = New();
        p.Update(0, 100, false);
        p.Update(19000, 100, false);
        p.Reset();
        Assert.False(p.Update(20000, 100, false));
        Assert.False(p.Update(39000, 100, false));
        Assert.True(p.Update(41000, 100, false));
    }
}
