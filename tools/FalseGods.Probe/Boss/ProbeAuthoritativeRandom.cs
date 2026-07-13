using System;
using FalseGods.Core.Simulation;

namespace FalseGods.Probe.Boss
{
    /// <summary>
    /// A probe stand-in for <see cref="IAuthoritativeRandom"/>, backed by <see cref="System.Random"/>.
    /// </summary>
    /// <remarks>
    /// The boss draws from this only on the host, when it selects an attack (Docs/ADRs/ADR-003). In the probe there
    /// is only a host, so a plain seeded PRNG is enough to make attack selection vary between projectile and area.
    /// It is deliberately <b>not</b> <c>UnityEngine.Random</c> — Core is Unity-less and the port keeps randomness on
    /// the authoritative side.
    /// </remarks>
    internal sealed class ProbeAuthoritativeRandom : IAuthoritativeRandom
    {
        private readonly Random _random;

        public ProbeAuthoritativeRandom(int seed) => _random = new Random(seed);

        public int NextInt(int minInclusive, int maxExclusive) => _random.Next(minInclusive, maxExclusive);

        public float NextFloat() => (float)_random.NextDouble();
    }
}
