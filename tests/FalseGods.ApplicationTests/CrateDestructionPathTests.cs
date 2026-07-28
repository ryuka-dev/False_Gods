using System.Collections.Generic;
using FalseGods.Application.Combat;
using FalseGods.Application.Replication;
using FalseGods.Core.Bosses.Combat;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;
using Xunit;

namespace FalseGods.ApplicationTests
{
    /// <summary>
    /// Which destructible deaths travel, and in which direction. The crates themselves are rebuilt on every peer
    /// from the commands that made them, so the only deaths worth sending are the ones no peer can work out for
    /// itself: the ones a player caused.
    /// </summary>
    public sealed class CrateDestructionPathTests
    {
        private static (FakeChannel channel, CrateCommandFlow flow, List<(int Id, CrateDeath Death)> settled,
            List<(int Id, CrateDeath Death)> asked) Rig(SessionRole role, int localPeer = 0)
        {
            var channel = new FakeChannel();
            var settled = new List<(int, CrateDeath)>();
            var asked = new List<(int, CrateDeath)>();
            var flow = new CrateCommandFlow(channel, new FakeSession(role, localPeer))
            {
                OnDestroyed = (id, death) => settled.Add((id, death)),
                OnDestroyRequested = (id, death) => asked.Add((id, death)),
            };
            return (channel, flow, settled, asked);
        }

        [Fact]
        public void A_host_settles_a_death_for_everyone()
        {
            var (channel, flow, _, _) = Rig(SessionRole.Host);

            flow.ReportDestroyed(crateId: 7, CrateDeath.Shot);

            var sent = Assert.Single(channel.Sent);
            Assert.Equal(MessageTargetKind.AllClients, sent.Target.Kind);
            var message = Assert.IsType<CrateDestroyed>(EncounterCodec.Decode(sent.Payload).Value);
            Assert.Equal(7, message.CrateId);
            Assert.Equal((int)CrateDeath.Shot, message.Death);
        }

        [Fact]
        public void A_client_asks_the_host_rather_than_settling_it()
        {
            var (channel, flow, _, _) = Rig(SessionRole.Client, localPeer: 1);

            flow.ReportDestroyed(crateId: 7, CrateDeath.Struck);

            var sent = Assert.Single(channel.Sent);
            Assert.Equal(MessageTargetKind.Host, sent.Target.Kind);
            var message = Assert.IsType<CrateDestroyRequested>(EncounterCodec.Decode(sent.Payload).Value);
            Assert.Equal(7, message.CrateId);
            Assert.Equal((int)CrateDeath.Struck, message.Death);
        }

        [Fact]
        public void A_client_carries_out_the_hosts_word()
        {
            var (channel, _, settled, _) = Rig(SessionRole.Client, localPeer: 1);

            channel.Deliver(new SessionPeerId(0), EncounterCodec.Encode(new CrateDestroyed(9, (int)CrateDeath.Struck)));

            Assert.Equal(new[] { (9, CrateDeath.Struck) }, settled);
        }

        [Fact]
        public void A_host_reads_a_clients_request_and_nobody_elses()
        {
            var (channel, _, _, asked) = Rig(SessionRole.Host);

            channel.Deliver(new SessionPeerId(1), EncounterCodec.Encode(new CrateDestroyRequested(9, (int)CrateDeath.Shot)));
            // Its own broadcast coming back: already done, and settling it again would be a loop.
            channel.Deliver(new SessionPeerId(0), EncounterCodec.Encode(new CrateDestroyRequested(11, (int)CrateDeath.Shot)));

            Assert.Equal(new[] { (9, CrateDeath.Shot) }, asked);
        }

        [Fact]
        public void A_client_ignores_another_clients_request()
        {
            var (channel, _, settled, asked) = Rig(SessionRole.Client, localPeer: 1);

            channel.Deliver(new SessionPeerId(2), EncounterCodec.Encode(new CrateDestroyRequested(9, (int)CrateDeath.Shot)));

            Assert.Empty(asked);
            Assert.Empty(settled);
        }

        [Fact]
        public void A_death_this_build_does_not_know_is_dropped_rather_than_guessed_at()
        {
            var (channel, _, settled, _) = Rig(SessionRole.Client, localPeer: 1);

            channel.Deliver(new SessionPeerId(0), EncounterCodec.Encode(new CrateDestroyed(9, Death: 42)));

            Assert.Empty(settled);
        }

        [Fact]
        public void A_destruction_claimed_by_a_client_is_not_settled_by_another_client()
        {
            var (channel, _, settled, _) = Rig(SessionRole.Client, localPeer: 1);

            channel.Deliver(new SessionPeerId(2), EncounterCodec.Encode(new CrateDestroyed(9, (int)CrateDeath.Shot)));

            Assert.Empty(settled);
        }
    }
}
