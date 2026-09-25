using Autofate.Logic;
using Xunit;

namespace Autofate.Tests;

public class SharedFateZonesTests
{
    private const uint Labyrinthos = 956, Garlemald = 958, UltimaThule = 960, Elpis = 961;
    private const uint Unknown = 4242;

    [Fact]
    public void KnowsTheZoneFateLevels()
    {
        Assert.Equal(86, SharedFateZones.MinFateLevel(Elpis));
        Assert.Equal(88, SharedFateZones.MinFateLevel(UltimaThule));
        Assert.Null(SharedFateZones.MinFateLevel(Unknown));
    }

    [Fact]
    public void DropsZonesAboveReach()
    {
        var order = SharedFateZones.Order(new[] { UltimaThule, Elpis, Labyrinthos }, playerLevel: 85, levelsAbove: 0);
        Assert.Equal(new[] { Labyrinthos }, order);
    }

    [Fact]
    public void PrefersZonesFullyAtOurLevel_OverOnesOnlyReachableWithLevelsAbove()
    {
        // Level 87 with 2 levels above can touch Ultima Thule's lowest fates, but Elpis is fully
        // doable, so it goes first even though the fixed list has Ultima Thule earlier.
        var order = SharedFateZones.Order(new[] { UltimaThule, Elpis }, playerLevel: 87, levelsAbove: 2);
        Assert.Equal(new[] { Elpis, UltimaThule }, order);
    }

    [Fact]
    public void HighestZoneWeCanFullyDoComesFirst()
    {
        var order = SharedFateZones.Order(new[] { Labyrinthos, Garlemald, Elpis }, playerLevel: 90, levelsAbove: 0);
        Assert.Equal(new[] { Elpis, Garlemald, Labyrinthos }, order);
    }

    [Fact]
    public void ZonesOnlyInReachThroughTheAllowance_ClosestFirst()
    {
        var order = SharedFateZones.Order(new[] { UltimaThule, Elpis }, playerLevel: 84, levelsAbove: 4);
        Assert.Equal(new[] { Elpis, UltimaThule }, order);
    }

    [Fact]
    public void UnknownZonesAreKept_AfterKnownOnes()
    {
        var order = SharedFateZones.Order(new[] { Unknown, Labyrinthos }, playerLevel: 80, levelsAbove: 0);
        Assert.Equal(new[] { Labyrinthos, Unknown }, order);
    }

    [Fact]
    public void NothingInReach_ReturnsEmpty()
    {
        Assert.Empty(SharedFateZones.Order(new[] { UltimaThule, Elpis }, playerLevel: 80, levelsAbove: 2));
    }
}
