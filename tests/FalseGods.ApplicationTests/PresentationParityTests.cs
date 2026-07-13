using FalseGods.Application.Presentation;
using FalseGods.Application.Wire;
using FalseGods.Core.Bosses;
using FalseGods.Core.Bosses.Events;
using FalseGods.Core.Simulation;
using FalseGods.Protocol.Wire;
using Xunit;

namespace FalseGods.ApplicationTests
{
    /// <summary>
    /// The "one presentation entry point" invariant (Docs/Architecture.md §7): what a multiplayer client sees is
    /// what the host/single-player sees. Concretely, for the same domain fact, the domain→presentation path and
    /// the domain→wire→client-presentation path must produce identical presentation contracts.
    /// </summary>
    public sealed class PresentationParityTests
    {
        private static readonly BossInstanceId Boss = new BossInstanceId(7);
        private static readonly SimVector2 Aim = new SimVector2(3f, -1f);
        private static readonly Sequence Seq = new Sequence(0);
        private static readonly SimulationTick Tick = new SimulationTick(0);

        public static object[][] DomainEvents() => new[]
        {
            new object[] { new BossSpawned(Boss, BossPhase.Two, 80) },
            new object[] { new AttackTelegraphed(Boss, new AttackInstanceId(3), BossAttackKind.AimedProjectile, Aim, 1.25f) },
            new object[] { new AttackCommitted(Boss, new AttackInstanceId(3), BossAttackKind.AreaTelegraph, Aim) },
            new object[] { new BossPhaseChanged(Boss, BossPhase.Two) },
            new object[] { new WeakPointExposed(Boss, true) },
            new object[] { new WeakPointExposed(Boss, false) },
            new object[] { new BossDamaged(Boss, 30, 70, true) },
            new object[] { new BossDied(Boss) },
        };

        [Theory]
        [MemberData(nameof(DomainEvents))]
        public void An_event_maps_the_same_through_the_domain_and_the_wire_paths(IBossDomainEvent domainEvent)
        {
            var viaDomain = BossPresentationMapping.ToEvent(domainEvent);

            var wireEvent = BossWireMapping.ToWireEvent(domainEvent, Seq, Tick);
            var viaWire = WirePresentationMapping.ToEvent(domainEvent.Boss, wireEvent);

            Assert.Equal(viaDomain, viaWire);
        }

        [Theory]
        [InlineData(0)] // fresh spawn: idle, phase one, full health
        [InlineData(1)] // recovering: weak point exposed
        [InlineData(2)] // dead
        public void State_maps_the_same_through_the_domain_and_the_wire_paths(int scenario)
        {
            var f = new BossFixture();
            f.Boss.Spawn(SimVector2.Zero);
            switch (scenario)
            {
                case 1:
                    f.Step(BossFixture.Definition.IdleSeconds);
                    f.Step(BossFixture.Definition.TelegraphSeconds);
                    f.Step(BossFixture.Definition.CommitSeconds); // -> recovering
                    break;
                case 2:
                    f.Boss.ApplyDamage(1000);
                    break;
            }

            var viaDomain = BossPresentationMapping.ToState(f.Boss);

            var snapshot = BossWireMapping.ToSnapshot(
                f.Boss, new EncounterId(1), new DefinitionId(1), Tick, Seq);
            var viaWire = WirePresentationMapping.ToState(snapshot);

            Assert.Equal(viaDomain, viaWire);
        }
    }
}
