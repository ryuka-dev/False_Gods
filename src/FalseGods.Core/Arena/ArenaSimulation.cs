using System;
using System.Collections.Generic;
using FalseGods.Core.Arena.Events;

namespace FalseGods.Core.Arena
{
    /// <summary>
    /// The authoritative arena gameplay state for the PoC test encounter: which mechanism groups are active, and
    /// whether the exit is unlocked.
    /// </summary>
    /// <remarks>
    /// The arena half of the Boss/Arena/Encounter split (Docs/Architecture.md §5,
    /// Docs/OriginalBossNetworkingArchitecture.md §9.2). It is host-authoritative and owns <b>only</b> arena state —
    /// it never inspects the boss, and the boss never inspects it; the <see cref="Encounters.EncounterCoordinator"/>
    /// bridges the two. It is kept to exactly what the test boss needs (a phase-2 mechanism group and an exit
    /// unlock on death, Docs/MinimalProofOfConceptPlan.md B7/B9); hazards, gates, and destructibles are not
    /// modelled until a boss needs them (Docs/RiskList.md R31).
    ///
    /// <para>
    /// State changes are idempotent and emit an <see cref="IArenaDomainEvent"/> only on the first transition, so a
    /// repeated command never produces a duplicate effect. Events are drained by the caller, exactly like
    /// <c>BossSimulation</c>.
    /// </para>
    /// </remarks>
    public sealed class ArenaSimulation
    {
        private readonly HashSet<MechanismGroupId> _activeGroups = new HashSet<MechanismGroupId>();
        private readonly List<IArenaDomainEvent> _events = new List<IArenaDomainEvent>();

        /// <summary>Whether the arena exit has been unlocked.</summary>
        public bool IsExitUnlocked { get; private set; }

        /// <summary>The mechanism groups currently active. Read for the arena snapshot.</summary>
        public IReadOnlyCollection<MechanismGroupId> ActiveMechanismGroups => _activeGroups;

        /// <summary>Whether <paramref name="group"/> is currently active.</summary>
        public bool IsMechanismGroupActive(MechanismGroupId group) => _activeGroups.Contains(group);

        /// <summary>
        /// Switch on <paramref name="group"/>. The first activation emits <see cref="MechanismGroupActivated"/>; a
        /// repeat is a no-op with no event, so a duplicated command cannot re-fire the mechanism.
        /// </summary>
        public void ActivateMechanismGroup(MechanismGroupId group)
        {
            if (_activeGroups.Add(group))
            {
                _events.Add(new MechanismGroupActivated(group));
            }
        }

        /// <summary>
        /// Unlock the exit. The first call emits <see cref="ArenaExitUnlocked"/>; a repeat is a no-op with no event.
        /// </summary>
        public void UnlockExit()
        {
            if (!IsExitUnlocked)
            {
                IsExitUnlocked = true;
                _events.Add(new ArenaExitUnlocked());
            }
        }

        /// <summary>
        /// Take the events accumulated since the last drain, clearing the internal buffer. The caller owns the
        /// returned events.
        /// </summary>
        public IReadOnlyList<IArenaDomainEvent> DrainEvents()
        {
            if (_events.Count == 0)
            {
                return Array.Empty<IArenaDomainEvent>();
            }

            var drained = _events.ToArray();
            _events.Clear();
            return drained;
        }
    }
}
