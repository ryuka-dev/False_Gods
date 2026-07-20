using FalseGods.Core.Bosses;
using FalseGods.Core.Simulation;
using Xunit;

namespace FalseGods.CoreTests
{
    /// <summary>
    /// The outbound half of combat (Docs/MultiplayerLoadingContract.md §5.6): when an attack commits, the boss
    /// resolves who is caught within the attack's hit radius of its aim point and emits one
    /// <see cref="Bosses.Combat.DamageRequest"/> each. Leaving the danger zone during the telegraph misses.
    /// </summary>
    public sealed class BossAttackHitResolutionTests
    {
        // A still boss (moveSpeed 0) so the aim point equals the target's telegraph-time position and the hit check
        // is a clean radius test around it. attackDamage 25, aimed radius 2 (tight), area radius 5 (wide).
        private static BossDefinition Def() => new BossDefinition(
            maxHealth: 100, phaseTwoHealthFraction: 0.5f, moveSpeed: 0f, idleSeconds: 1f,
            telegraphSeconds: 1f, commitSeconds: 0.5f, recoverSeconds: 1f, weakPointDamageMultiplier: 3,
            attackDamage: 25, aimedHitRadius: 2f, areaHitRadius: 5f);

        private static (BossTestHarness h, BossSimulation boss) Spawned(int random, params (int id, float x, float z)[] participants)
        {
            var h = new BossTestHarness().WithRandom(random);
            foreach (var p in participants)
            {
                h.WithParticipantAt(p.id, p.x, p.z);
            }

            var boss = h.Build(Def());
            boss.Spawn(SimVector2.Zero);
            boss.DrainEvents();
            return (h, boss);
        }

        [Fact]
        public void A_target_that_stays_in_the_aim_is_damaged_when_the_attack_commits()
        {
            var (h, boss) = Spawned(random: 0, (1, 5f, 0f));

            h.Step(1f); // idle -> telegraph, aim fixed at (5,0)
            h.Step(1f); // telegraph -> commit
            var request = Assert.Single(boss.DrainDamageRequests());

            Assert.Equal(new ParticipantId(1), request.Target);
            Assert.Equal(25, request.Amount);
            Assert.Equal(new AttackInstanceId(1), request.Attack);
        }

        [Fact]
        public void A_target_that_leaves_the_danger_zone_during_the_telegraph_is_missed()
        {
            var (h, boss) = Spawned(random: 0, (1, 5f, 0f));

            h.Step(1f); // telegraph, aim locked at (5,0)
            h.Participants.Set(new ParticipantId(1), new SimVector2(50f, 0f)); // dodge out
            h.Step(1f); // commit

            Assert.Empty(boss.DrainDamageRequests());
        }

        [Fact]
        public void An_area_attack_catches_every_participant_within_its_wide_radius()
        {
            // Random 1 selects the area attack; aim = nearest = (5,0); both participants are within area radius 5.
            var (h, boss) = Spawned(random: 1, (1, 5f, 0f), (2, 7f, 0f));

            h.Step(1f);
            h.Step(1f);
            var requests = boss.DrainDamageRequests();

            Assert.Equal(2, requests.Count);
            Assert.Contains(requests, r => r.Target == new ParticipantId(1));
            Assert.Contains(requests, r => r.Target == new ParticipantId(2));
        }

        [Fact]
        public void An_aimed_attacks_tight_radius_spares_a_bystander_an_area_attack_would_catch()
        {
            // Random 0 selects the aimed attack; aim = (5,0); the bystander at (8,0) is 3 m away > aimed radius 2.
            var (h, boss) = Spawned(random: 0, (1, 5f, 0f), (2, 8f, 0f));

            h.Step(1f);
            h.Step(1f);
            var request = Assert.Single(boss.DrainDamageRequests());

            Assert.Equal(new ParticipantId(1), request.Target);
        }

        [Fact]
        public void Draining_damage_requests_clears_them()
        {
            var (h, boss) = Spawned(random: 0, (1, 5f, 0f));
            h.Step(1f);
            h.Step(1f);

            Assert.Single(boss.DrainDamageRequests());
            Assert.Empty(boss.DrainDamageRequests());
        }
    }
}
