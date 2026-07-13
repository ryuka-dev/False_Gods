using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using SULFURTogether.Api;

// FalseGods.Protocol defines its own Vector3/Quaternion authoring types; alias only the handful this probe reads
// by name so nothing clashes with UnityEngine's (used here for coroutine timing).
using ArenaContentArtifact = FalseGods.Protocol.Arena.ArenaContentArtifact;

namespace FalseGods.Probe
{
    /// <summary>How the CLIENT instance answers the host's EnterArena — the four P9 scenarios, one per run.</summary>
    internal enum P9ClientMode
    {
        /// <summary>Send the real (schema, ContentHash): host should PASS and seal.</summary>
        Normal,
        /// <summary>Flip a byte of the hash: host should abort ContentMismatch.</summary>
        ForceHashMismatch,
        /// <summary>Bump the schema version: host should abort ContentHashSchemaMismatch WITHOUT comparing hashes.</summary>
        ForceSchemaMismatch,
        /// <summary>Send nothing: host's gate should time out and abort.</summary>
        StaySilent,
    }

    /// <summary>
    /// PoC step P9 — the host+client arena-parity proof, run over the real SULFUR Together transport through the
    /// public bridge (<see cref="SULFURTogether.Api.NetExternalChannel"/> / <see cref="NetSessionInfo"/>), with no
    /// reflection into ST internals. Both instances load this same probe; behaviour is decided by
    /// <see cref="NetSessionInfo.Role"/>.
    ///
    /// The sequence mirrors the loading contract (Docs/MultiplayerLoadingContract.md §5.3): the host broadcasts
    /// <c>EnterArena</c>; each peer computes its OWN canonical <c>ContentHash</c> from its deployed artifact
    /// (through the production FalseGods.Protocol, exactly as P8) and reports <c>ArenaReady</c>; the host validates
    /// every peer's <c>(ContentHashSchemaVersion, ContentHash)</c> against its own and the gate blocks the seal
    /// until all match. The seal here is FG-owned and notional — this probe never drives ST's ArenaLockdownManager
    /// (that, and remote-NPC activation, are the deferred Phase-B asks; see Docs/ADRs/ADR-004).
    ///
    /// Fail-closed is the point: a byte-flipped hash aborts ContentMismatch, a bumped schema aborts
    /// ContentHashSchemaMismatch (hashes never compared), and a silent peer times out — none seal or start
    /// (§5.3.1). The client's <see cref="P9ClientMode"/> selects which of those four this run exercises.
    ///
    /// Throwaway, like every P0-P8 probe: it registers one opaque channel, reads its own artifact, and mutates no
    /// authoritative game or ST state. The channel handler runs on the Unity main thread (the bridge guarantees
    /// it), so the host coroutine polls the collected responses without locking.
    /// </summary>
    internal sealed class P9ParityProbe
    {
        public const string ChannelId = "false_gods.p9.arena";
        private const string ArtifactFileName = "arena-content-PocRoom.artifact";
        private const byte KindEnterArena = 1;
        private const byte KindArenaReady = 2;
        private const int MaxHashBytes = 1024; // a well-formed ContentHash is 32; guard the reader anyway.

        // Plain mirror of SULFURTogether.Api.SessionRole, so the coroutines branch on an int and never hold the ST
        // enum as an iterator local (which would become an unloadable field when ST is absent).
        private const int RoleOffline = 0;
        private const int RoleHost = 1;
        private const int RoleClient = 2;

        private readonly ManualLogSource _log;

        // Held as IDisposable, NOT the ST type IExternalChannelRegistration (which extends IDisposable), on purpose:
        // this type is constructed in ProbePlugin.Awake, so the CLR lays it out at plugin load. A field of an ST type
        // would force the SULFUR Together assembly to load THERE — a TypeLoadException that kills the whole probe when
        // ST is absent, which is exactly the case B0 must run in (single-player, no ST). Keeping every ST type inside
        // method bodies (JIT'd only on the guarded P9 path) lets P0-P8 and B0 load with no ST installed.
        private IDisposable _registration;

        // Host-side collected client responses. Written by OnReceive and read by RunHost — both on the Unity main
        // thread (the bridge dispatches on it), so no lock is needed.
        private readonly Dictionary<string, Manifest> _responses = new Dictionary<string, Manifest>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _malformed = new Dictionary<string, string>(StringComparer.Ordinal);

        public P9ParityProbe(ManualLogSource log) => _log = log;

        /// <summary>Set by the plugin from config before each run; only the CLIENT consults it.</summary>
        public P9ClientMode ClientMode { get; set; } = P9ClientMode.Normal;

        private readonly struct Manifest
        {
            public readonly int Schema;
            public readonly byte[] Hash;
            public readonly string ArenaId;
            public readonly int ArenaVersion;
            public readonly int ProtocolVersion;
            public readonly string BundleVersion;

            public Manifest(int schema, byte[] hash, string arenaId, int arenaVersion, int protocolVersion, string bundleVersion)
            {
                Schema = schema;
                Hash = hash;
                ArenaId = arenaId;
                ArenaVersion = arenaVersion;
                ProtocolVersion = protocolVersion;
                BundleVersion = bundleVersion;
            }
        }

        // ── registration (the only ST touch besides RunOrArm; guarded at the call site for a missing bridge) ──

        /// <summary>Register the opaque channel once. Idempotent; returns false only if the bridge rejects it.</summary>
        public bool EnsureRegistered(ProbeReport report)
        {
            if (_registration != null)
            {
                report.Value("channel", $"{ChannelId} (already registered)");
                return true;
            }

            _registration = NetExternalChannel.Register(ChannelId, OnReceive);
            report.Value("channel registered", $"{ChannelId} (ST bridge API v{NetExternalChannel.ApiVersion})");
            return true;
        }

        public void Dispose()
        {
            try { _registration?.Dispose(); }
            catch (Exception ex) { _log.LogWarning($"[P9] channel dispose failed: {ex.Message}"); }
            _registration = null;
        }

        // ── entry: host drives the exchange; client arms and answers asynchronously ──

        public IEnumerator RunOrArm(ProbeReport report, float timeoutSeconds)
        {
            report.Section("P9 — host+client arena parity over the ST bridge");

            // Read all ST session state in ONE regular (non-iterator) method and keep only plain types in this
            // coroutine. An iterator hoists its locals into state-machine fields, and a field of an ST type makes the
            // whole probe assembly fail Assembly.GetTypes() when ST is absent — which breaks every unrelated
            // GetTypes() scanner in the process (XNode, EasySettings, ...). So no ST type may live in an iterator
            // local or a lambda anywhere in this file; only regular-method bodies/signatures may name one.
            var session = ReadSession();
            report.Value("role", session.RoleName);
            report.Value("session active", session.Active);
            report.Value("local peer id", session.LocalPeerId);
            report.Value("session peers", session.PeersSummary);

            if (session.RoleCode == RoleHost)
            {
                yield return RunHost(report, session.RemotePeerIds, session.LocalPeerId, timeoutSeconds);
            }
            else if (session.RoleCode == RoleClient)
            {
                report.Line($"  Armed as CLIENT (channel handler registered, mode={ClientMode}). Waiting for the host's");
                report.Line("  EnterArena; the response is logged to this console when it arrives. Set the mode you want");
                report.Line("  to test in Probe/P9ClientMode, then press the P9 key on the HOST to drive the exchange.");
            }
            else
            {
                report.Line("  No session running. Create a host on one instance and join it from a client, then press P9");
                report.Line("  on the host. (This probe needs the bridge-enabled SULFUR Together on BOTH instances.)");
            }
        }

        // ── host ──

        private IEnumerator RunHost(ProbeReport report, List<string> required, string localPeerId, float timeoutSeconds)
        {
            if (required.Count == 0)
            {
                report.Line("  No remote client connected. Join a second instance as a client, then press P9 here again.");
                yield break;
            }

            var host = LoadLocalManifest(report);
            if (host == null)
            {
                report.Line("  Host could not load its own artifact — build the Unity bundle and redeploy the probe.");
                yield break;
            }
            var hm = host.Value;
            report.Value("host manifest", DescribeManifest(hm));

            // ENTER: announce, and open a fresh collection window.
            _responses.Clear();
            _malformed.Clear();
            var enter = Encode(KindEnterArena, hm);
            bool announced = SendEnterArena(enter);
            report.Value("EnterArena -> all clients", announced ? "sent" : "NOT sent (no live session?)");
            report.Value("awaiting ArenaReady from", string.Join(", ", required));

            // COLLECT: poll until every required peer has answered (well-formed or not), or the deadline passes.
            float deadline = Time.realtimeSinceStartup + Mathf.Max(1f, timeoutSeconds);
            while (Time.realtimeSinceStartup < deadline)
            {
                if (required.All(p => _responses.ContainsKey(p) || _malformed.ContainsKey(p)))
                    break;
                yield return null;
            }

            // VALIDATE: schema first (never compare hashes across schemas), then versions, then the full hash.
            report.Section("P9 — validation (host compares each peer to its own manifest)");
            var gate = new LocalReadyGate(new[] { localPeerId }.Concat(required).ToList());
            gate.MarkReady(localPeerId); // the host validated its own content locally

            string abortReason = null;
            var outstanding = new List<string>();
            foreach (var peerId in required)
            {
                if (_malformed.TryGetValue(peerId, out var mal))
                {
                    report.Value($"peer {peerId}", $"MALFORMED ArenaReady ({mal})");
                    abortReason ??= $"MalformedArenaReady ({peerId})";
                    continue;
                }
                if (!_responses.TryGetValue(peerId, out var m))
                {
                    outstanding.Add(peerId);
                    report.Value($"peer {peerId}", "NO RESPONSE (timed out)");
                    continue;
                }
                if (m.Schema != hm.Schema)
                {
                    report.Value($"peer {peerId}", $"SCHEMA MISMATCH (peer={m.Schema}, host={hm.Schema}) — hashes NOT compared");
                    abortReason ??= $"ContentHashSchemaMismatch ({peerId})";
                    continue;
                }
                if (m.ArenaId != hm.ArenaId || m.ArenaVersion != hm.ArenaVersion ||
                    m.ProtocolVersion != hm.ProtocolVersion || m.BundleVersion != hm.BundleVersion)
                {
                    report.Value($"peer {peerId}", $"VERSION MISMATCH ({m.ArenaId} v{m.ArenaVersion} proto{m.ProtocolVersion} '{m.BundleVersion}')");
                    abortReason ??= $"VersionMismatch ({peerId})";
                    continue;
                }
                if (!BytesEqual(m.Hash, hm.Hash))
                {
                    report.Value($"peer {peerId}", $"CONTENT MISMATCH hash={Hex(m.Hash)}");
                    abortReason ??= $"ContentMismatch ({peerId})";
                    continue;
                }
                report.Value($"peer {peerId}", $"MATCH hash={Hex(m.Hash)}");
                gate.MarkReady(peerId);
            }

            if (abortReason == null && outstanding.Count > 0)
                abortReason = $"Timeout — no ArenaReady from: {string.Join(", ", outstanding)}";

            // VERDICT.
            report.Section("P9 — verdict");
            report.Value("gate", gate.Describe());
            bool pass = abortReason == null && gate.IsResolved;
            report.Value("gate resolved (all peers matched)", pass);
            if (pass)
            {
                report.Value("P9 verdict", "PARITY OK — every peer reported a byte-identical (schema, ContentHash) and the "
                    + "gate resolved. SEAL + TELEPORT would fire now (FG-owned test seal). Nothing sealed before the gate "
                    + "passed. NOTE: remote-NPC activation (R10) is NOT covered here — it needs the deferred activation "
                    + "surface (ADR-004); this probe proves the channel + session + hash-parity + abort paths the bridge enables.");
            }
            else
            {
                report.Value("abort reason", abortReason);
                report.Value("P9 verdict", $"ABORT (fail-closed) — {abortReason}. Nothing sealed, teleported, or started. "
                    + "This is the CORRECT outcome for a mismatch / schema / timeout scenario; it is only a failure if you "
                    + "ran the client in Normal mode and expected a pass.");
            }
        }

        // ── client (invoked from OnReceive on the main thread) ──

        private void RespondAsClient(string hostPeerId)
        {
            if (ClientMode == P9ClientMode.StaySilent)
            {
                _log.LogMessage("[P9] client mode=StaySilent — sending nothing; the host's gate should time out and abort.");
                return;
            }

            var local = LoadLocalManifest(null);
            if (local == null)
            {
                _log.LogWarning("[P9] client could not load its artifact — sending nothing (host will time out).");
                return;
            }
            var m = local.Value;

            int schema = m.Schema;
            byte[] hash = (byte[])m.Hash.Clone();
            if (ClientMode == P9ClientMode.ForceSchemaMismatch) schema += 7;
            if (ClientMode == P9ClientMode.ForceHashMismatch && hash.Length > 0) hash[0] ^= 0xFF;

            var reply = Encode(KindArenaReady, new Manifest(schema, hash, m.ArenaId, m.ArenaVersion, m.ProtocolVersion, m.BundleVersion));
            bool sent = NetExternalChannel.Send(ChannelId, reply, ExternalDelivery.ReliableOrdered, ExternalTarget.Host);
            _log.LogMessage($"[P9] client -> host ArenaReady (mode={ClientMode}, schema={schema}, hash={Hex(hash)}) sent={sent}");
        }

        // ── receive (both roles) ──

        private void OnReceive(string senderPeerId, byte[] payload)
        {
            try
            {
                using var ms = new MemoryStream(payload ?? Array.Empty<byte>());
                using var r = new BinaryReader(ms);
                byte kind = r.ReadByte();
                if (kind == KindEnterArena)
                {
                    _log.LogMessage($"[P9] EnterArena from host peer '{senderPeerId}'; responding (mode={ClientMode}).");
                    RespondAsClient(senderPeerId);
                }
                else if (kind == KindArenaReady)
                {
                    if (TryReadManifest(r, out var m))
                    {
                        _responses[senderPeerId] = m;
                        _log.LogMessage($"[P9] ArenaReady from '{senderPeerId}': schema={m.Schema} hash={Hex(m.Hash)} {m.ArenaId} v{m.ArenaVersion}");
                    }
                    else
                    {
                        _malformed[senderPeerId] = "unreadable ArenaReady body";
                        _log.LogWarning($"[P9] malformed ArenaReady from '{senderPeerId}'");
                    }
                }
                else
                {
                    _log.LogWarning($"[P9] unknown message kind {kind} from '{senderPeerId}'");
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[P9] OnReceive error from '{senderPeerId}': {ex.Message}");
            }
        }

        // ── artifact -> local manifest (the production FalseGods.Protocol path, same as P8) ──

        private Manifest? LoadLocalManifest(ProbeReport report)
        {
            var artifactPath = Path.Combine(Paths.BepInExRootPath, "FalseGods.Probe", ArtifactFileName);
            if (!File.Exists(artifactPath))
            {
                report?.Line($"  Artifact not found at {artifactPath}.");
                return null;
            }
            try
            {
                var artifact = ArenaContentArtifact.Parse(File.ReadAllText(artifactPath));
                var hash = artifact.ComputeContentHash().ToArray();
                var def = artifact.Definition;
                return new Manifest(artifact.SchemaVersion.Value, hash, def.ArenaId, def.ArenaVersion,
                    artifact.ProtocolVersion, artifact.BundleVersion);
            }
            catch (Exception ex)
            {
                report?.Failure("load local manifest (FalseGods.Protocol)", ex);
                return null;
            }
        }

        // ── wire codec (throwaway, length-prefixed via BinaryWriter) ──

        private static byte[] Encode(byte kind, Manifest m)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms))
            {
                w.Write(kind);
                w.Write(m.Schema);
                w.Write(m.Hash.Length);
                w.Write(m.Hash);
                w.Write(m.ArenaId ?? "");
                w.Write(m.ArenaVersion);
                w.Write(m.ProtocolVersion);
                w.Write(m.BundleVersion ?? "");
            }
            return ms.ToArray();
        }

        private static bool TryReadManifest(BinaryReader r, out Manifest m)
        {
            m = default;
            try
            {
                int schema = r.ReadInt32();
                int hlen = r.ReadInt32();
                if (hlen < 0 || hlen > MaxHashBytes) return false;
                var hash = r.ReadBytes(hlen);
                if (hash.Length != hlen) return false;
                var arenaId = r.ReadString();
                int arenaVersion = r.ReadInt32();
                int protocolVersion = r.ReadInt32();
                var bundleVersion = r.ReadString();
                m = new Manifest(schema, hash, arenaId, arenaVersion, protocolVersion, bundleVersion);
                return true;
            }
            catch { return false; }
        }

        // ── helpers ──

        // ── ST boundary (regular methods only — every ST type is named here, never in an iterator local/lambda) ──

        /// <summary>
        /// Read the whole ST session into plain types in one regular method, so the coroutines can branch and address
        /// peers without ever holding a <see cref="SessionRole"/> or <see cref="ExternalPeer"/> in a hoisted iterator
        /// field. The foreach enumerator and the <c>ExternalPeer</c> loop variable are ordinary stack locals here.
        /// </summary>
        private SessionSnapshot ReadSession()
        {
            var snapshot = new SessionSnapshot
            {
                RoleName = NetSessionInfo.Role.ToString(),
                Active = NetSessionInfo.IsSessionActive,
                LocalPeerId = NetSessionInfo.LocalPeerId,
                RemotePeerIds = new List<string>(),
            };

            switch (NetSessionInfo.Role)
            {
                case SessionRole.Host: snapshot.RoleCode = RoleHost; break;
                case SessionRole.Client: snapshot.RoleCode = RoleClient; break;
                default: snapshot.RoleCode = RoleOffline; break;
            }

            var parts = new List<string>();
            foreach (var peer in NetSessionInfo.Peers)
            {
                parts.Add($"{peer.PeerId}{(peer.IsHost ? "(host)" : "")}{(peer.IsLocal ? "(local)" : "")}");
                if (!peer.IsLocal)
                    snapshot.RemotePeerIds.Add(peer.PeerId);
            }

            snapshot.PeersSummary = parts.Count == 0 ? "<none>" : string.Join(", ", parts);
            return snapshot;
        }

        /// <summary>Broadcast EnterArena to all clients. Wrapped so the ST enums stay out of the RunHost iterator.</summary>
        private static bool SendEnterArena(byte[] payload) =>
            NetExternalChannel.Send(ChannelId, payload, ExternalDelivery.ReliableOrdered, ExternalTarget.AllClients);

        /// <summary>Plain, ST-free projection of the session, safe to hold across a coroutine yield.</summary>
        private sealed class SessionSnapshot
        {
            public int RoleCode;
            public string RoleName;
            public bool Active;
            public string LocalPeerId;
            public string PeersSummary;
            public List<string> RemotePeerIds;
        }

        private static string DescribeManifest(Manifest m) =>
            $"schema={m.Schema} hash={Hex(m.Hash)} arena={m.ArenaId} v{m.ArenaVersion} proto={m.ProtocolVersion} bundle={m.BundleVersion}";

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static string Hex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "<none>";
            // Short-form: first 8 bytes are enough to eyeball a match/mismatch in the log; the full compare is byte-exact.
            var take = Math.Min(bytes.Length, 8);
            var sb = new System.Text.StringBuilder(take * 2 + 1);
            for (int i = 0; i < take; i++) sb.Append(bytes[i].ToString("x2"));
            if (bytes.Length > take) sb.Append("...");
            return sb.ToString();
        }
    }
}
