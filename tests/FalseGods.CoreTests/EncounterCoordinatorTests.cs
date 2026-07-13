using System;
using System.Linq;
using FalseGods.Core.Arena;
using FalseGods.Core.Arena.Events;
using FalseGods.Core.Bosses;
using FalseGods.Core.Bosses.Events;
using FalseGods.Core.Encounters;
using FalseGods.Core.Simulation;
using Xunit;

namespace FalseGods.CoreTests
{
    public sealed class EncounterCoordinatorTests
    {
        private static readonly MechanismGroupId PhaseTwo = new MechanismGroupId("phase_2");
        private static readonly BossInstanceId Boss = new BossInstanceId(1);

        private static (EncounterCoordinator coordinator, ArenaSimulation arena) Build()
        {
            var arena = new ArenaSimulation();
            var coordinator = new EncounterCoordinator(new EncounterId(42), arena, PhaseTwo);
            return (coordinator, arena);
        }

        [Fact]
        public void A_new_coordinator_starts_pre_fight()
        {
            var (coordinator, _) = Build();
            Assert.Equal(EncounterPhase.PreFight, coordinator.Phase);
            Assert.Equal(new EncounterId(42), coordinator.Id);
        }

        [Fact]
        public void Begin_moves_pre_fight_to_fighting()
        {
            var (coordinator, _) = Build();
            coordinator.Begin();
            Assert.Equal(EncounterPhase.Fighting, coordinator.Phase);
        }

        [Fact]
        public void Boss_reaching_phase_two_activates_the_configured_mechanism_group()
        {
            var (coordinator, arena) = Build();

            coordinator.Process(new IBossDomainEvent[] { new BossPhaseChanged(Boss, BossPhase.Two) });

            Assert.True(arena.IsMechanismGroupActive(PhaseTwo));
            Assert.IsType<MechanismGroupActivated>(Assert.Single(arena.DrainEvents()));
        }

        [Fact]
        public void A_phase_change_that_is_not_phase_two_activates_nothing()
        {
            var (coordinator, arena) = Build();

            coordinator.Process(new IBossDomainEvent[] { new BossPhaseChanged(Boss, BossPhase.One) });

            Assert.False(arena.IsMechanismGroupActive(PhaseTwo));
            Assert.Empty(arena.DrainEvents());
        }

        [Fact]
        public void Boss_death_unlocks_the_exit_and_marks_the_encounter_defeated()
        {
            var (coordinator, arena) = Build();
            coordinator.Begin();

            coordinator.Process(new IBossDomainEvent[] { new BossDied(Boss) });

            Assert.True(arena.IsExitUnlocked);
            Assert.Equal(EncounterPhase.Defeated, coordinator.Phase);
            Assert.IsType<ArenaExitUnlocked>(Assert.Single(arena.DrainEvents()));
        }

        [Fact]
        public void Processing_boss_death_twice_unlocks_the_exit_exactly_once()
        {
            var (coordinator, arena) = Build();

            coordinator.Process(new IBossDomainEvent[] { new BossDied(Boss) });
            arena.DrainEvents();
            coordinator.Process(new IBossDomainEvent[] { new BossDied(Boss) });

            Assert.True(arena.IsExitUnlocked);
            Assert.Equal(EncounterPhase.Defeated, coordinator.Phase);
            Assert.Empty(arena.DrainEvents());
        }

        [Fact]
        public void Process_ignores_unrelated_events_and_an_empty_batch()
        {
            var (coordinator, arena) = Build();

            coordinator.Process(new IBossDomainEvent[] { new BossDamaged(Boss, 10, 90, false), new BossSpawned(Boss, BossPhase.One, 100) });
            coordinator.Process(Array.Empty<IBossDomainEvent>());

            Assert.False(arena.IsExitUnlocked);
            Assert.Empty(arena.ActiveMechanismGroups);
            Assert.Empty(arena.DrainEvents());
        }

        [Fact]
        public void Process_rejects_null()
        {
            var (coordinator, _) = Build();
            Assert.Throws<ArgumentNullException>(() => coordinator.Process(null!));
        }

        [Fact]
        public void End_to_end_a_real_boss_driving_the_arena_through_phase_two_and_death()
        {
            var h = new BossTestHarness().WithRandom(0).WithParticipantAt(1, 5f, 0f);
            var boss = h.Build();
            var (coordinator, arena) = Build();
            boss.Spawn(SimVector2.Zero);
            coordinator.Begin();
            coordinator.Process(boss.DrainEvents()); // spawn

            boss.ApplyDamage(60); // 100 -> 40, crosses the 50 phase-two threshold
            coordinator.Process(boss.DrainEvents());
            Assert.True(arena.IsMechanismGroupActive(PhaseTwo));

            boss.ApplyDamage(1000); // lethal
            coordinator.Process(boss.DrainEvents());
            Assert.True(arena.IsExitUnlocked);
            Assert.Equal(EncounterPhase.Defeated, coordinator.Phase);
        }
    }
}
