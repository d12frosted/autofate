namespace Autofate.Logic;

/// <summary>Game object ids as they come out of the client.</summary>
public static class ObjectIds
{
    /// <summary>The id the game uses for "no object" (an empty target, an unset fate NPC).</summary>
    public const ulong None = 0xE0000000;

    /// <summary>
    /// Does this id point at an object? "No target" is <see cref="None"/>, not 0, so a plain
    /// <c>!= 0</c> check calls every idle mob busy. 0 is accepted as empty too, for fields that
    /// really are zeroed.
    /// </summary>
    public static bool IsSome(ulong id) => id != 0 && id != None;
}
