using System.Linq;
using FalseGods.Application.Presentation;
using FalseGods.Application.Wire;
using FalseGods.Core.Arena;
using FalseGods.Core.Arena.Events;
using FalseGods.Core.Simulation;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Presentation;
using Xunit;

namespace FalseGods.ApplicationTests
{
    /// <summary>
    /// The arena presentation mappers: the domain half (host/single-player) and the wire half (client) must
    /// produce identical cues for the same transition — the arena counterpart of the boss presentation-parity
    /// tests (Architecture §7) — and a baseline's arena snapshot replays as idempotent cues (late join).
    /// </summary>
    public sealed class ArenaPresentationMappingTests
    {
        private static readonly MechanismGroupId Group = new MechanismGroupId("phase_2");

        [Fact]
        public void Domain_and_wire_mappers_agree_on_mechanism_activation()
        {
            var domainCue = ArenaPresentationMapping.ToEvent(new MechanismGroupActivated(Group));
            var wireCue = WirePresentationMapping.ToEvent(
                ArenaWireMapping.ToWireEvent(new MechanismGroupActivated(Group), new Sequence(0), new SimulationTick(1)));

            Assert.Equal(new MechanismGroupEngaged(Group), domainCue);
            Assert.Equal(domainCue, wireCue);
        }

        [Fact]
        public void Domain_and_wire_mappers_agree_on_exit_unlock()
        {
            var domainCue = ArenaPresentationMapping.ToEvent(new ArenaExitUnlocked());
            var wireCue = WirePresentationMapping.ToEvent(
                ArenaWireMapping.ToWireEvent(new ArenaExitUnlocked(), new Sequence(0), new SimulationTick(1)));

            Assert.Equal(new ExitOpened(), domainCue);
            Assert.Equal(domainCue, wireCue);
        }

        [Fact]
        public void A_snapshot_replays_its_state_as_cues_for_a_late_joiner()
        {
            var snapshot = new ArenaSnapshot(
                new EncounterId(3), "a", 1, ProtocolVersion.Current, new SimulationTick(10),
                new[] { Group, new MechanismGroupId("hazards") }, ExitUnlocked: true, new Sequence(4));

            var cues = WirePresentationMapping.ToEvents(snapshot);

            Assert.Equal(3, cues.Count);
            Assert.Contains(new MechanismGroupEngaged(Group), cues);
            Assert.Contains(new MechanismGroupEngaged(new MechanismGroupId("hazards")), cues);
            Assert.Contains(new ExitOpened(), cues.OfType<IPresentationEvent>());
        }

        [Fact]
        public void A_locked_exit_replays_no_exit_cue()
        {
            var snapshot = new ArenaSnapshot(
                new EncounterId(3), "a", 1, ProtocolVersion.Current, new SimulationTick(10),
                new[] { Group }, ExitUnlocked: false, new Sequence(0));

            var cues = WirePresentationMapping.ToEvents(snapshot);

            Assert.Equal(new IPresentationEvent[] { new MechanismGroupEngaged(Group) }, cues);
        }
    }
}
