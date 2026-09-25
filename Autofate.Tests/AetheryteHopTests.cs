using System.Numerics;
using Autofate.Logic;
using Xunit;

namespace Autofate.Tests;

public class AetheryteHopTests
{
    [Fact]
    public void MarkerToWorld_MatchesTheGameMap()
    {
        // Anagnorisis, Elpis: map marker (1182, 1150) on a 100 size-factor map, no offset. The
        // in-game map shows it at (24.7, 24.0), i.e. world X/Z (158, 126).
        var p = AetheryteHop.MarkerToWorld(1182, 1150, sizeFactor: 100, offsetX: 0, offsetY: 0);
        Assert.Equal(158f, p.X, 0);
        Assert.Equal(126f, p.Y, 0);
    }

    [Fact]
    public void MarkerToWorld_AppliesScaleAndOffset()
    {
        // Size factor 200 halves the distances; the offset shifts the origin.
        var p = AetheryteHop.MarkerToWorld(1224, 824, sizeFactor: 200, offsetX: 10, offsetY: -20);
        Assert.Equal(90f, p.X, 3);   // (1224 - 1024) / 2 - 10
        Assert.Equal(-80f, p.Y, 3);  // (824 - 1024) / 2 + 20
    }

    private static readonly AetheryteHop.Settings Cfg = new(MinDistance: 350, MinSaving: 200, ArrivalDistance: 40);

    private static AetheryteHop.Aetheryte At(uint id, float x, float z) => new(id, new Vector2(x, z));

    [Fact]
    public void Hops_ToTheAetheryteClosestToTheFate()
    {
        var d = AetheryteHop.Decide(me: new(0, 0), fate: new(800, 0),
            new[] { At(1, 100, 0), At(2, 750, 0) }, Cfg);
        Assert.Equal(AetheryteHop.Verdict.Hop, d.Verdict);
        Assert.Equal(2u, d.AetheryteId);
        Assert.Equal(750f, d.Saved, 1);
    }

    [Fact]
    public void Flies_WhenTheFateIsClose()
    {
        var d = AetheryteHop.Decide(new(0, 0), new(300, 0), new[] { At(1, 290, 0) }, Cfg);
        Assert.Equal(AetheryteHop.Verdict.FateTooClose, d.Verdict);
    }

    [Fact]
    public void Flies_WhenThereIsNoAetheryte()
    {
        var d = AetheryteHop.Decide(new(0, 0), new(800, 0), Array.Empty<AetheryteHop.Aetheryte>(), Cfg);
        Assert.Equal(AetheryteHop.Verdict.NoAetheryte, d.Verdict);
    }

    [Fact]
    public void Flies_WhenTheSavingIsSmall()
    {
        var d = AetheryteHop.Decide(new(0, 0), new(800, 0), new[] { At(1, 150, 0) }, Cfg);
        Assert.Equal(AetheryteHop.Verdict.NotEnoughSaving, d.Verdict);
    }

    [Fact]
    public void Flies_WhenAlreadyAtTheAetheryte()
    {
        // Standing next to the aetheryte closest to the fate already: nothing to gain.
        var d = AetheryteHop.Decide(new(0, 0), new(800, 0), new[] { At(1, 20, 0) }, Cfg with { MinSaving = 0 });
        Assert.Equal(AetheryteHop.Verdict.AlreadyThere, d.Verdict);
    }

    [Fact]
    public void UsesHorizontalDistance()
    {
        // Positions are X/Z only: the markers carry no height, and Elpis stacks islands on top of
        // each other, so a height guess would only add noise.
        var d = AetheryteHop.Decide(new(0, 0), new(0, 800), new[] { At(1, 0, 760) }, Cfg);
        Assert.Equal(AetheryteHop.Verdict.Hop, d.Verdict);
        Assert.Equal(800f, d.Distance, 1);
    }
}
