using Autofate.Logic;
using Xunit;

namespace Autofate.Tests;

public class CombatTrailTests
{
    [Fact]
    public void KeepsOnlyTheWindow()
    {
        var t = new CombatTrail(windowMs: 10000);
        t.Add(0, 1.0f, new[] { "A" });
        t.Add(5000, 0.8f, new[] { "A", "B" });
        t.Add(12000, 0.3f, new[] { "A", "B", "C" });
        Assert.Equal(2, t.Count); // the t=0 sample is older than 10s before the last one
    }

    [Fact]
    public void Summary_ShowsHpAndAttackersOverTime()
    {
        var t = new CombatTrail(windowMs: 10000);
        t.Add(1000, 0.9f, new[] { "Wivre" });
        t.Add(4000, 0.4f, new[] { "Wivre", "Wivre", "Elder Wivre" });
        var s = t.Summary(nowMs: 5000);
        Assert.Contains("-4.0s hp 90% 1 on us (Wivre)", s);
        Assert.Contains("-1.0s hp 40% 3 on us (2x Wivre, Elder Wivre)", s);
    }

    [Fact]
    public void Empty_SaysSo()
        => Assert.Equal("no combat samples", new CombatTrail(10000).Summary(0));
}
