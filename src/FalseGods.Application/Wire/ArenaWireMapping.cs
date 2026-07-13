using System;
using System.Collections.Generic;
using FalseGods.Core.Arena;
using FalseGods.Core.Arena.Events;
using FalseGods.Core.Simulation;
using FalseGods.Protocol.Wire;

namespace FalseGods.Application.Wire
{
    /// <summary>
    /// The host-side mapper from the arena <b>domain</b> to the <b>wire</b>: <see cref="ArenaSimulation"/> state and
    /// <see cref="IArenaDomainEvent"/>s into <see cref="ArenaSnapshot"/> / <see cref="IArenaWireEvent"/>.
    /// </summary>
    /// <remarks>
    /// The arena counterpart of <see cref="BossWireMapping"/>, on its own separate wire stream (ADR-005). Pure and
    /// static.
    /// </remarks>
    public static class ArenaWireMapping
    {
        public static ArenaSnapshot ToSnapshot(
            ArenaSimulation arena,
            EncounterId encounter,
            string arenaId,
            int arenaVersion,
            SimulationTick tick,
            Sequence lastProcessedArenaEventSequence)
        {
            if (arena is null)
            {
                throw new ArgumentNullException(nameof(arena));
            }

            var groups = new List<MechanismGroupId>(arena.ActiveMechanismGroups);
            return new ArenaSnapshot(
                encounter,
                arenaId,
                arenaVersion,
                ProtocolVersion.Current,
                tick,
                groups,
                arena.IsExitUnlocked,
                lastProcessedArenaEventSequence);
        }

        public static IArenaWireEvent ToWireEvent(IArenaDomainEvent domainEvent, Sequence sequence, SimulationTick tick)
        {
            switch (domainEvent)
            {
                case MechanismGroupActivated e:
                    return new ArenaMechanismGroupActivatedEvent(sequence, tick, e.Group);
                case ArenaExitUnlocked _:
                    return new ArenaExitUnlockedEvent(sequence, tick);
                case null:
                    throw new ArgumentNullException(nameof(domainEvent));
                default:
                    throw new ArgumentOutOfRangeException(nameof(domainEvent), domainEvent.GetType().Name, "No wire mapping for this arena domain event.");
            }
        }
    }
}
