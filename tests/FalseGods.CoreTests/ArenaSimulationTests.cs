using System.Linq;
using FalseGods.Core.Arena;
using FalseGods.Core.Arena.Events;
using Xunit;

namespace FalseGods.CoreTests
{
    public sealed class ArenaSimulationTests
    {
        private static readonly MechanismGroupId PhaseTwo = new MechanismGroupId("phase_2");
        private static readonly MechanismGroupId Hazards = new MechanismGroupId("hazards");

        [Fact]
        public void Activating_a_group_the_first_time_makes_it_active_and_emits_one_event()
        {
            var arena = new ArenaSimulation();

            arena.ActivateMechanismGroup(PhaseTwo);

            Assert.True(arena.IsMechanismGroupActive(PhaseTwo));
            var activated = Assert.IsType<MechanismGroupActivated>(Assert.Single(arena.DrainEvents()));
            Assert.Equal(PhaseTwo, activated.Group);
        }

        [Fact]
        public void Re_activating_a_group_is_idempotent_and_emits_nothing()
        {
            var arena = new ArenaSimulation();
            arena.ActivateMechanismGroup(PhaseTwo);
            arena.DrainEvents();

            arena.ActivateMechanismGroup(PhaseTwo);

            Assert.True(arena.IsMechanismGroupActive(PhaseTwo));
            Assert.Empty(arena.DrainEvents());
        }

        [Fact]
        public void Different_groups_are_tracked_independently()
        {
            var arena = new ArenaSimulation();

            arena.ActivateMechanismGroup(PhaseTwo);
            arena.ActivateMechanismGroup(Hazards);

            Assert.True(arena.IsMechanismGroupActive(PhaseTwo));
            Assert.True(arena.IsMechanismGroupActive(Hazards));
            Assert.Equal(2, arena.ActiveMechanismGroups.Count);
            Assert.Equal(2, arena.DrainEvents().OfType<MechanismGroupActivated>().Count());
        }

        [Fact]
        public void Unlocking_the_exit_the_first_time_sets_the_flag_and_emits_one_event()
        {
            var arena = new ArenaSimulation();

            arena.UnlockExit();

            Assert.True(arena.IsExitUnlocked);
            Assert.IsType<ArenaExitUnlocked>(Assert.Single(arena.DrainEvents()));
        }

        [Fact]
        public void Unlocking_the_exit_again_is_idempotent_and_emits_nothing()
        {
            var arena = new ArenaSimulation();
            arena.UnlockExit();
            arena.DrainEvents();

            arena.UnlockExit();

            Assert.True(arena.IsExitUnlocked);
            Assert.Empty(arena.DrainEvents());
        }

        [Fact]
        public void A_fresh_arena_has_no_active_groups_a_locked_exit_and_no_events()
        {
            var arena = new ArenaSimulation();

            Assert.False(arena.IsExitUnlocked);
            Assert.Empty(arena.ActiveMechanismGroups);
            Assert.Empty(arena.DrainEvents());
        }
    }
}
