using System;

namespace FalseGods.Core.Bosses
{
    /// <summary>
    /// The immutable tuning of the test boss: health, phase threshold, movement, and attack timings.
    /// </summary>
    /// <remarks>
    /// A definition holds <b>no</b> live state — it is the design of the boss, not an instance of it. One
    /// <see cref="BossSimulation"/> is constructed from a definition and owns all the mutable state. Timings are in
    /// seconds of host simulation time (Docs/ADRs/ADR-003), and the constructor rejects a nonsensical definition up
    /// front so a bad tuning fails at construction rather than mid-encounter.
    /// </remarks>
    public sealed record BossDefinition
    {
        public BossDefinition(
            int maxHealth,
            float phaseTwoHealthFraction,
            float moveSpeed,
            float idleSeconds,
            float telegraphSeconds,
            float commitSeconds,
            float recoverSeconds,
            int weakPointDamageMultiplier)
        {
            if (maxHealth <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxHealth), maxHealth, "Max health must be positive.");
            }

            if (phaseTwoHealthFraction <= 0f || phaseTwoHealthFraction >= 1f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(phaseTwoHealthFraction),
                    phaseTwoHealthFraction,
                    "Phase-two health fraction must be strictly between 0 and 1.");
            }

            if (moveSpeed < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(moveSpeed), moveSpeed, "Move speed cannot be negative.");
            }

            RequireNonNegative(idleSeconds, nameof(idleSeconds));
            RequirePositive(telegraphSeconds, nameof(telegraphSeconds));
            RequirePositive(commitSeconds, nameof(commitSeconds));
            RequirePositive(recoverSeconds, nameof(recoverSeconds));

            if (weakPointDamageMultiplier < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(weakPointDamageMultiplier),
                    weakPointDamageMultiplier,
                    "Weak-point damage multiplier must be at least 1.");
            }

            MaxHealth = maxHealth;
            PhaseTwoHealthFraction = phaseTwoHealthFraction;
            MoveSpeed = moveSpeed;
            IdleSeconds = idleSeconds;
            TelegraphSeconds = telegraphSeconds;
            CommitSeconds = commitSeconds;
            RecoverSeconds = recoverSeconds;
            WeakPointDamageMultiplier = weakPointDamageMultiplier;
        }

        /// <summary>Starting health, in phase one, at full.</summary>
        public int MaxHealth { get; }

        /// <summary>The health fraction (of <see cref="MaxHealth"/>) at or below which the boss enters phase two.</summary>
        public float PhaseTwoHealthFraction { get; }

        /// <summary>Movement speed toward the target while idle, in metres per second.</summary>
        public float MoveSpeed { get; }

        /// <summary>How long the boss idles between attacks before selecting the next one.</summary>
        public float IdleSeconds { get; }

        /// <summary>How long an attack telegraphs before it commits.</summary>
        public float TelegraphSeconds { get; }

        /// <summary>How long the commit (the landing/active window of the attack) lasts.</summary>
        public float CommitSeconds { get; }

        /// <summary>How long the post-attack recovery lasts — the window in which the weak point is exposed.</summary>
        public float RecoverSeconds { get; }

        /// <summary>Damage multiplier applied to a hit that lands while the weak point is exposed.</summary>
        public int WeakPointDamageMultiplier { get; }

        /// <summary>The health value at which phase two begins (rounded down from the fraction).</summary>
        public int PhaseTwoHealthThreshold => (int)Math.Floor(MaxHealth * PhaseTwoHealthFraction);

        private static void RequireNonNegative(float value, string name)
        {
            if (value < 0f || float.IsNaN(value))
            {
                throw new ArgumentOutOfRangeException(name, value, "Duration cannot be negative or NaN.");
            }
        }

        private static void RequirePositive(float value, string name)
        {
            if (value <= 0f || float.IsNaN(value))
            {
                throw new ArgumentOutOfRangeException(name, value, "Duration must be positive.");
            }
        }
    }
}
