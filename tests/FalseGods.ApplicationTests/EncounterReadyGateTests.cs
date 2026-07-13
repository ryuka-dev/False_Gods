using FalseGods.Application.ReadyGate;
using FalseGods.Protocol.Arena;
using FalseGods.RuntimeContracts.Transport;
using Xunit;

namespace FalseGods.ApplicationTests
{
    public sealed class EncounterReadyGateTests
    {
        private static ContentHash Hash(byte seed)
        {
            var bytes = new byte[ContentHash.Sha256Length];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)(seed + i);
            }

            return new ContentHash(bytes);
        }

        private static ArenaManifest Manifest(
            string arenaId = "false_gods.arena.poc",
            int arenaVersion = 1,
            int schema = 1,
            byte hashSeed = 10,
            int protocol = 1,
            string bundle = "poc-1")
            => new ArenaManifest(arenaId, arenaVersion, new ContentHashSchemaVersion(schema), Hash(hashSeed), protocol, bundle);

        private static EncounterReadyGate Gate(FakeRoster roster) => new EncounterReadyGate(Manifest(), roster);

        [Fact]
        public void Single_player_resolves_the_instant_the_one_local_peer_readies()
        {
            var gate = Gate(new FakeRoster(0));

            var status = gate.SubmitReady(new SessionPeerId(0), Manifest());

            Assert.Equal(GateStatus.Resolved, status);
        }

        [Fact]
        public void A_two_member_gate_waits_for_both()
        {
            var gate = Gate(new FakeRoster(0, 1));

            Assert.Equal(GateStatus.Waiting, gate.SubmitReady(new SessionPeerId(0), Manifest()));
            Assert.Equal(GateStatus.Resolved, gate.SubmitReady(new SessionPeerId(1), Manifest()));
        }

        [Fact]
        public void An_empty_required_set_never_resolves()
        {
            var gate = Gate(new FakeRoster());
            Assert.Equal(GateStatus.Waiting, gate.Status);
        }

        [Fact]
        public void A_ready_from_a_non_member_is_rejected_not_admitted()
        {
            var gate = Gate(new FakeRoster(0));

            var status = gate.SubmitReady(new SessionPeerId(9), Manifest());

            Assert.Equal(GateStatus.Waiting, status);
            Assert.Contains(new SessionPeerId(0), gate.Outstanding);
        }

        [Fact]
        public void A_schema_mismatch_aborts_without_comparing_the_hashes()
        {
            var gate = Gate(new FakeRoster(0));

            // Different schema AND a different hash: the reason must be the schema, proving the hash was never compared.
            var status = gate.SubmitReady(new SessionPeerId(0), Manifest(schema: 2, hashSeed: 99));

            Assert.Equal(GateStatus.Aborted, status);
            Assert.Equal(GateAbortReason.ContentHashSchemaMismatch, gate.AbortReason);
        }

        [Fact]
        public void A_version_mismatch_aborts()
        {
            var gate = Gate(new FakeRoster(0));
            var status = gate.SubmitReady(new SessionPeerId(0), Manifest(arenaVersion: 2));
            Assert.Equal(GateAbortReason.VersionMismatch, gate.AbortReason);
            Assert.Equal(GateStatus.Aborted, status);
        }

        [Fact]
        public void A_content_hash_mismatch_on_a_matching_schema_aborts()
        {
            var gate = Gate(new FakeRoster(0));
            gate.SubmitReady(new SessionPeerId(0), Manifest(hashSeed: 200));
            Assert.Equal(GateAbortReason.ContentMismatch, gate.AbortReason);
        }

        [Fact]
        public void A_load_failure_aborts()
        {
            var gate = Gate(new FakeRoster(0, 1));
            gate.SubmitLoadFailed(new SessionPeerId(1));
            Assert.Equal(GateAbortReason.LoadFailed, gate.AbortReason);
        }

        [Fact]
        public void A_timeout_while_waiting_aborts()
        {
            var gate = Gate(new FakeRoster(0, 1));
            gate.SubmitReady(new SessionPeerId(0), Manifest());
            gate.OnTimeout();
            Assert.Equal(GateAbortReason.Timeout, gate.AbortReason);
        }

        [Fact]
        public void A_timeout_after_resolving_does_not_abort()
        {
            var gate = Gate(new FakeRoster(0));
            gate.SubmitReady(new SessionPeerId(0), Manifest());
            Assert.Equal(GateStatus.Resolved, gate.OnTimeout());
            Assert.Equal(GateAbortReason.None, gate.AbortReason);
        }

        [Fact]
        public void An_abort_is_sticky()
        {
            var gate = Gate(new FakeRoster(0, 1));
            gate.SubmitReady(new SessionPeerId(0), Manifest(hashSeed: 200)); // ContentMismatch
            Assert.Equal(GateStatus.Aborted, gate.SubmitReady(new SessionPeerId(1), Manifest())); // even a valid ready
            Assert.Equal(GateAbortReason.ContentMismatch, gate.AbortReason);
        }
    }
}
