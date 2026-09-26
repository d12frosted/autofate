using Autofate.Logic;
using Xunit;

namespace Autofate.Tests;

public class ObjectIdsTests
{
    [Theory]
    [InlineData(0UL, false)]
    [InlineData(0xE0000000UL, false)] // what the game actually uses for "no target"
    [InlineData(0x10000001UL, true)]  // a player
    [InlineData(0x400012A4UL, true)]  // a battle npc
    public void IsSome(ulong id, bool expected) => Assert.Equal(expected, ObjectIds.IsSome(id));
}
