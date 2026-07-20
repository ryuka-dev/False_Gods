using System.Collections.Generic;
using FalseGods.Application.Combat;
using FalseGods.Application.Replication;
using FalseGods.Core.Simulation;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;
using Xunit;

namespace FalseGods.ApplicationTests
{
    /// <summary>
    /// The client → host hit path (Docs/OriginalBossNetworkingArchitecture.md §5.6): the client reports intent, the
    /// host validates membership and the live encounter, clamps the untrusted candidate, and applies its own rules.
    /// </summary>
    public sealed class ClientHitPathTests
    {
        private static readonly EncounterId Encounter = new EncounterId(5);
        private const float MaxPerHit = 200f;

        // ---- host intake -----------------------------------------------------------------------------------

        private static (FakeChannel channel, HostHitIntake intake, List<float> applied) Intake(
            float maxPerHit = MaxPerHit, params int[] members)
        {
            var channel = new FakeChannel();
            var applied = new List<float>();
            var intake = new HostHitIntake(
                channel, new FakeRoster(members), Encounter, maxPerHit, applied.Add);
            return (channel, intake, applied);
        }

        private static EncodedPayload Hit(float candidate, EncounterId? encounter = null) =>
            EncounterCodec.Encode(new ClientHitRequest(encounter ?? Encounter, 1, candidate, null));

        [Fact]
        public void Applies_a_member_hit_for_this_encounter()
        {
            var (channel, _, applied) = Intake(members: new[] { 0, 1 });

            channel.Deliver(new SessionPeerId(1), Hit(50f));

            Assert.Equal(new[] { 50f }, applied);
        }

        [Fact]
        public void Clamps_an_oversized_candidate_to_the_ceiling()
        {
            var (channel, _, applied) = Intake(members: new[] { 0, 1 });

            channel.Deliver(new SessionPeerId(1), Hit(1_000_000f));

            Assert.Equal(new[] { MaxPerHit }, applied);
        }

        [Fact]
        public void Drops_a_hit_from_a_non_member()
        {
            var (channel, _, applied) = Intake(members: new[] { 0, 1 });

            channel.Deliver(new SessionPeerId(2), Hit(50f)); // 2 is not in the roster

            Assert.Empty(applied);
        }

        [Fact]
        public void Ignores_a_hit_for_a_different_encounter()
        {
            var (channel, _, applied) = Intake(members: new[] { 0, 1 });

            channel.Deliver(new SessionPeerId(1), Hit(50f, new EncounterId(999)));

            Assert.Empty(applied);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-10f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void Drops_a_non_finite_or_non_positive_candidate(float candidate)
        {
            var (channel, _, applied) = Intake(members: new[] { 0, 1 });

            channel.Deliver(new SessionPeerId(1), Hit(candidate));

            Assert.Empty(applied);
        }

        [Fact]
        public void Ignores_other_message_kinds_and_undecodable_traffic()
        {
            var (channel, _, applied) = Intake(members: new[] { 0, 1 });

            channel.Deliver(new SessionPeerId(1), EncounterCodec.Encode(new EncounterEnded(Encounter, new SimulationTick(1))));
            channel.Deliver(new SessionPeerId(1), new EncodedPayload(new byte[] { 0xFF, 0x01 }));

            Assert.Empty(applied);
        }

        [Fact]
        public void Stops_applying_after_dispose()
        {
            var (channel, intake, applied) = Intake(members: new[] { 0, 1 });

            intake.Dispose();
            channel.Deliver(new SessionPeerId(1), Hit(50f));

            Assert.Empty(applied);
        }

        // ---- client reporter -------------------------------------------------------------------------------

        [Fact]
        public void Reporter_sends_a_reliable_hit_request_to_the_host()
        {
            var channel = new FakeChannel();
            var reporter = new ClientHitReporter(channel, new FakeSession(SessionRole.Client, localPeer: 1, hostPeer: 0));

            reporter.ReportHit(Encounter, 42f);

            var sent = Assert.Single(channel.Sent);
            Assert.Equal(MessageDelivery.ReliableOrdered, sent.Delivery);
            Assert.Equal(MessageTargetKind.Host, sent.Target.Kind);
            var decoded = Assert.IsType<ClientHitRequest>(EncounterCodec.Decode(sent.Payload).Value);
            Assert.Equal(Encounter, decoded.Encounter);
            Assert.Equal(42f, decoded.DamageCandidate);
        }

        [Fact]
        public void Reporter_stamps_a_monotonic_sequence()
        {
            var channel = new FakeChannel();
            var reporter = new ClientHitReporter(channel, new FakeSession(SessionRole.Client, localPeer: 1));

            reporter.ReportHit(Encounter, 1f);
            reporter.ReportHit(Encounter, 1f);

            var first = (ClientHitRequest)EncounterCodec.Decode(channel.Sent[0].Payload).Value;
            var second = (ClientHitRequest)EncounterCodec.Decode(channel.Sent[1].Payload).Value;
            Assert.True(second.RequestSequence > first.RequestSequence);
        }
    }
}
