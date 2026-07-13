namespace FalseGods.Core.Simulation
{
    /// <summary>
    /// The source of every random decision the boss makes.
    /// </summary>
    /// <remarks>
    /// One of Core's three permitted ports (Docs/Architecture.md §6): the domain calls it directly when it picks
    /// an attack. It is deliberately <b>not</b> <c>UnityEngine.Random</c> — Core is Unity-less — and, more
    /// importantly, it keeps randomness on the authoritative side. Only the host boss draws from it; clients never
    /// re-run the decision, they receive its result as a replicated event (Docs/ADRs/ADR-003). Injecting a
    /// scripted sequence in a test makes attack selection deterministic.
    /// </remarks>
    public interface IAuthoritativeRandom
    {
        /// <summary>
        /// A value in <c>[minInclusive, maxExclusive)</c>. Behaviour is undefined when
        /// <paramref name="maxExclusive"/> is not greater than <paramref name="minInclusive"/>.
        /// </summary>
        int NextInt(int minInclusive, int maxExclusive);

        /// <summary>A value in <c>[0, 1)</c>.</summary>
        float NextFloat();
    }
}
