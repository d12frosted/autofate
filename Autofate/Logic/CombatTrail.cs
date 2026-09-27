namespace Autofate.Logic;

/// <summary>
/// The last few seconds of a fight: our HP and who was on us, sampled while in combat. Logged when
/// we die, so a death says what killed us (one mob out-damaging us, or a pile) instead of leaving
/// only the target we happened to be hitting.
/// </summary>
public sealed class CombatTrail
{
    private readonly long _windowMs;
    private readonly Queue<(long Ms, float Hp, IReadOnlyList<string> Attackers)> _samples = new();

    public CombatTrail(long windowMs) => _windowMs = windowMs;

    public int Count => _samples.Count;

    public void Add(long nowMs, float hpFraction, IReadOnlyList<string> attackers)
    {
        _samples.Enqueue((nowMs, hpFraction, attackers));
        while (_samples.Count > 0 && nowMs - _samples.Peek().Ms > _windowMs) _samples.Dequeue();
    }

    public void Clear() => _samples.Clear();

    /// <summary>One line per sample, oldest first: "-4.0s hp 90% 2 on us (2x Wivre)".</summary>
    public string Summary(long nowMs)
    {
        if (_samples.Count == 0) return "no combat samples";
        return string.Join("; ", _samples.Select(s =>
        {
            var who = string.Join(", ", s.Attackers
                .GroupBy(a => a)
                .Select(g => g.Count() > 1 ? $"{g.Count()}x {g.Key}" : g.Key));
            var ago = FormattableString.Invariant($"{(s.Ms - nowMs) / 1000.0:0.0}s");
            return $"{ago} hp {s.Hp * 100:0}% {s.Attackers.Count} on us ({who})";
        }));
    }
}
