using System;
using FalseGods.Core.Simulation;

namespace FalseGods.Integration.Sulfur.Simulation
{
    /// <summary>
    /// The single-player implementation of <see cref="IAuthoritativeRandom"/>: a plain seeded
    /// <see cref="System.Random"/>.
    /// </summary>
    /// <remarks>
    /// The boss draws from this only on the host, when it selects an attack (Docs/ADRs/ADR-003). In single-player
    /// there is only a host, so a seeded PRNG is enough to make attack selection vary. It is deliberately
    /// <b>not</b> <c>UnityEngine.Random</c> — that is a global, non-deterministic-across-peers static, whereas an
    /// injected instance keeps randomness authoritative and reproducible from a seed. The type is pure (no game
    /// dependency); it lives in the single-player adapter alongside the other two Core-port implementations so the
    /// Composition Root wires one cohesive bundle.
    /// </remarks>
    public sealed class SeededAuthoritativeRandom : IAuthoritativeRandom
    {
        private readonly Random _random;

        /// <param name="seed">Seeds the sequence; the same seed reproduces the same attack selection.</param>
        public SeededAuthoritativeRandom(int seed) => _random = new Random(seed);

        public int NextInt(int minInclusive, int maxExclusive) => _random.Next(minInclusive, maxExclusive);

        public float NextFloat() => (float)_random.NextDouble();
    }
}
