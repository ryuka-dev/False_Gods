using System;
using System.Collections.Generic;

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
        // The relocation shape, taken from the vanilla cave boss's own submerge/reappear clips (measured on
        // v0.18.5): its root sinks about three metres in 0.42 s, waits underground, then rises in 0.33 s, and its
        // invulnerability is dropped 0.60 s into the reappearance — while it is still visibly settling. The hold
        // is shorter here than vanilla's ~1.8 s, which it spends spawning an arm we have no equivalent of.
        private const float DefaultVanishSeconds = 0.45f;
        private const float DefaultHiddenSeconds = 0.6f;
        private const float DefaultAppearSeconds = 0.6f;

        public BossDefinition(
            int maxHealth,
            float phaseTwoHealthFraction,
            float moveSpeed,
            float idleSeconds,
            float telegraphSeconds,
            float commitSeconds,
            float recoverSeconds,
            int weakPointDamageMultiplier,
            int attackDamage,
            float aimedHitRadius,
            float areaHitRadius,
            IReadOnlyList<BossStation>? stations = null,
            float vanishSeconds = DefaultVanishSeconds,
            float hiddenSeconds = DefaultHiddenSeconds,
            float appearSeconds = DefaultAppearSeconds,
            float openingSeconds = 0f,
            bool attacksOnItsOwn = true)
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

            if (attackDamage <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(attackDamage), attackDamage, "Attack damage must be positive.");
            }

            RequirePositive(aimedHitRadius, nameof(aimedHitRadius));
            RequirePositive(areaHitRadius, nameof(areaHitRadius));

            RequirePositive(vanishSeconds, nameof(vanishSeconds));
            RequirePositive(hiddenSeconds, nameof(hiddenSeconds));
            RequirePositive(appearSeconds, nameof(appearSeconds));
            RequireNonNegative(openingSeconds, nameof(openingSeconds));

            Stations = ValidateItinerary(stations, moveSpeed);
            VanishSeconds = vanishSeconds;
            HiddenSeconds = hiddenSeconds;
            AppearSeconds = appearSeconds;
            OpeningSeconds = openingSeconds;
            AttacksOnItsOwn = attacksOnItsOwn;

            MaxHealth = maxHealth;
            PhaseTwoHealthFraction = phaseTwoHealthFraction;
            MoveSpeed = moveSpeed;
            IdleSeconds = idleSeconds;
            TelegraphSeconds = telegraphSeconds;
            CommitSeconds = commitSeconds;
            RecoverSeconds = recoverSeconds;
            WeakPointDamageMultiplier = weakPointDamageMultiplier;
            AttackDamage = attackDamage;
            AimedHitRadius = aimedHitRadius;
            AreaHitRadius = areaHitRadius;
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

        /// <summary>Damage dealt to each participant caught by an attack when it commits.</summary>
        public int AttackDamage { get; }

        /// <summary>Hit radius, in metres, around the aim point of an aimed-projectile attack — tight, a near-direct hit.</summary>
        public float AimedHitRadius { get; }

        /// <summary>Hit radius, in metres, around the aim point of an area-telegraph attack — wide, the danger zone to leave.</summary>
        public float AreaHitRadius { get; }

        /// <summary>The health value at which phase two begins (rounded down from the fraction).</summary>
        public int PhaseTwoHealthThreshold => (int)Math.Floor(MaxHealth * PhaseTwoHealthFraction);

        /// <summary>
        /// Where this boss stands over the course of the fight, in order: the first station is where it starts,
        /// and each later one is entered when health falls to its fraction. Empty for a boss with no itinerary,
        /// which simply stands where it spawned.
        /// </summary>
        /// <remarks>
        /// Independent of <see cref="Phase"/>-like coarse phases (<see cref="PhaseTwoHealthFraction"/>, which
        /// drives the arena): a phase says what the encounter is doing, a station says where the boss is. They are
        /// deliberately not folded together — the fight wants more standing changes than it wants phases.
        /// </remarks>
        public IReadOnlyList<BossStation> Stations { get; }

        /// <summary>Whether this boss's position is decided by its itinerary rather than by moving.</summary>
        public bool HasItinerary => Stations.Count > 0;

        /// <summary>How long the boss takes to leave, still visible and already invulnerable.</summary>
        public float VanishSeconds { get; }

        /// <summary>How long it stays out of sight. The move to the next station happens as this begins.</summary>
        public float HiddenSeconds { get; }

        /// <summary>How long it is arriving — visible again, and invulnerable until this window ends.</summary>
        public float AppearSeconds { get; }

        /// <summary>The whole relocation, end to end.</summary>
        public float RelocationSeconds => VanishSeconds + HiddenSeconds + AppearSeconds;

        /// <summary>
        /// How long the boss spends announcing itself once the fight is triggered, before any of it starts running.
        /// </summary>
        /// <remarks>
        /// The length of the roar. A boss that is triggered does not begin fighting in the same instant: it makes a
        /// noise, and the room is given that long to notice. Boss tuning rather than encounter tuning, because it is
        /// the boss's own gesture that is being waited on — everything a peer plays over that window (the roar, the
        /// room opening around the players, the music) is timed to it.
        /// <para>Zero means a boss that starts fighting the moment it is told to, which is what a boss with no
        /// authored opening gets.</para>
        /// </remarks>
        public float OpeningSeconds { get; }

        /// <summary>
        /// Whether the boss runs an attack cycle of its own — idling, telegraphing, committing, recovering.
        /// </summary>
        /// <remarks>
        /// <para>A boss that does not is not a boss that does nothing: what threatens the players may be entirely
        /// the room's, summoned and thrown and supplied on the boss's behalf. Turning it off leaves the boss idle
        /// between relocations, which is the honest state for a creature whose fight is fought by everything
        /// around it.</para>
        /// <para><b>The weak point goes with it.</b> The exposed window <i>is</i> the post-attack recovery, so a
        /// boss with no attacks of its own has none — the damage a player deals it is never amplified, which is a
        /// tuning consequence to remember rather than an oversight.</para>
        /// <para>The attack timings and radii stay meaningful for a boss that does attack; they are simply never
        /// read by one that does not.</para>
        /// </remarks>
        public bool AttacksOnItsOwn { get; }

        /// <summary>
        /// An itinerary must start at full health and descend strictly: each station is entered at a lower health
        /// fraction than the one before, so "the next station" is always unambiguous and one big hit can be
        /// resolved by walking forward through the list. A boss with an itinerary must not also walk — two things
        /// deciding where it stands is exactly the double-authority this codebase refuses.
        /// </summary>
        private static IReadOnlyList<BossStation> ValidateItinerary(
            IReadOnlyList<BossStation>? stations, float moveSpeed)
        {
            if (stations is null || stations.Count == 0)
            {
                return Array.Empty<BossStation>();
            }

            if (moveSpeed > 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(moveSpeed),
                    moveSpeed,
                    "A boss with an itinerary stands where its stations put it, so it must not also move: give it "
                    + "a move speed of zero, or no stations.");
            }

            if (stations[0].EnterAtHealthFraction < 1f)
            {
                throw new ArgumentException(
                    "The first station is where the boss starts, so it is entered at full health (fraction 1).",
                    nameof(stations));
            }

            for (var i = 1; i < stations.Count; i++)
            {
                if (stations[i].EnterAtHealthFraction >= stations[i - 1].EnterAtHealthFraction)
                {
                    throw new ArgumentException(
                        $"Station {i} is entered at {stations[i].EnterAtHealthFraction:P0} health, which is not "
                        + $"below station {i - 1}'s {stations[i - 1].EnterAtHealthFraction:P0}; an itinerary must "
                        + "descend strictly.",
                        nameof(stations));
                }
            }

            var copy = new BossStation[stations.Count];
            for (var i = 0; i < stations.Count; i++)
            {
                copy[i] = stations[i];
            }

            return copy;
        }

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
