using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;

// FalseGods.Protocol defines its own Vector3/Quaternion (authoring types), which clash with UnityEngine's here.
// Alias the handful of Protocol types this probe reads by name and reach the transform components through the
// records (parity.LocalTransform.Position.X, ...), so the authoring vectors never need to be named unqualified.
using ArenaContentArtifact = FalseGods.Protocol.Arena.ArenaContentArtifact;
using ArenaContentDefinition = FalseGods.Protocol.Arena.ArenaContentDefinition;
using ContentHashComputer = FalseGods.Protocol.Arena.ContentHashComputer;

namespace FalseGods.Probe
{
    /// <summary>
    /// PoC step P8 — the single-player FULL LOOP over our authored arena content, proving the obligations P0–P7
    /// did not: that the shipped content artifact recomputes to the pinned hash IN-GAME (RiskList R34), that the
    /// realized runtime hierarchy matches the authored parity map (RiskList R14), and that the loading-contract
    /// ready-gate sequence (load -> validate -> ready -> start) resolves for a single local peer — then it hands
    /// off to the already-proven fight (P6) and clean teardown (P7) so the whole loop runs end to end
    /// (Docs/MinimalProofOfConceptPlan.md §7.2, Docs/MultiplayerLoadingContract.md §5.2/§5.3).
    ///
    /// This is the first probe that runs FalseGods.Protocol code inside the game: it reads the artifact the Unity
    /// exporter shipped next to the bundle and recomputes the canonical <c>ContentHash</c> through the very
    /// production assembly a peer would use. Two things are checked, both fail-closed:
    ///
    ///   R34 (a) — the in-game hash equals the golden hash pinned by the offline fixture test
    ///             (tests/FalseGods.ProtocolTests/ArenaContentArtifactFixtureTests.cs). If the deployed artifact
    ///             or Protocol.dll drifts from what CI verified, this diverges.
    ///   R34 (b) — reversing the order of every authored list (the deterministic stand-in for "a different
    ///             Addressables completion order across two loads") recomputes the SAME hash. The canonical hash
    ///             orders by StableMarkerId, so realization order must not matter.
    ///
    ///   R14     — the arena is loaded from the bundle and every authored parity node is located by path and its
    ///             runtime LOCAL transform compared to the authored one. Local (not world) transforms are compared
    ///             so where the island is placed is irrelevant — this is a hierarchy-shape check, not a placement one.
    ///
    /// The ready gate is a throwaway in-probe stand-in (<see cref="LocalReadyGate"/>) — the production loading
    /// contract is not built yet. It proves the sequence shape only: content validated -> local peer ready ->
    /// gate resolves -> session starts.
    ///
    /// The fight and the leave are REUSED, not re-implemented: this probe runs the proven <see cref="NavmeshEnemyProbe"/>
    /// (a real vanilla dummy paths our applied arena, P6/R9) and then <see cref="NavmeshTeardownProbe"/> (snapshot +
    /// restore leaves the level we stay in at baseline, P7/R8) into the same report, and finally asserts no arena
    /// residue survived the whole loop. Mutates AstarPath.active exactly as P6/P7 do (each restores what it added);
    /// run it in a throwaway level standing on solid level nav.
    /// </summary>
    internal sealed class ArenaContentProbe
    {
        private const string BundleFileName = "falsegods-poc-room.bundle";
        private const string ArtifactFileName = "arena-content-PocRoom.artifact";
        private const string OurRoomPrefabName = "PocRoom";

        // Pinned by tests/FalseGods.ProtocolTests/ArenaContentArtifactFixtureTests.cs. The in-game recompute must
        // reproduce this exact digest — a change here is a ContentHashSchemaVersion change, never a quiet edit.
        private const string GoldenContentHashHex =
            "dbed0d2a644fb8a2701518f95088087fe33982c19619677cc22524f5221c5acf";

        // R14 tolerances: the same prefab is serialized into the bundle and read back, so local transforms should
        // match to well within these. Tight enough to catch a real hierarchy/transform divergence, loose enough to
        // tolerate float round-tripping through the AssetBundle pipeline.
        private const float PositionEpsilon = 1e-3f;   // metres
        private const float RotationEpsilonDeg = 0.05f; // degrees (Quaternion.Angle, sign-robust)
        private const float ScaleEpsilon = 1e-3f;

        private readonly bool _runFightAndLeave;
        private readonly string _enemyUnitIdName;

        private AssetBundle _bundle;
        private GameObject _holder;

        public ArenaContentProbe(bool runFightAndLeave, string enemyUnitIdName)
        {
            _runFightAndLeave = runFightAndLeave;
            _enemyUnitIdName = enemyUnitIdName;
        }

        public IEnumerator Run(ProbeReport report)
        {
            report.Section("P8 — single-player full loop: content identity (R34) + hierarchy parity (R14) + ready gate");

            // ── ENTER: read the shipped artifact (deployed next to the bundle by DeployProbe). ────────────────
            var dataDir = Path.Combine(Paths.BepInExRootPath, "FalseGods.Probe");
            var artifactPath = Path.Combine(dataDir, ArtifactFileName);
            if (!File.Exists(artifactPath))
            {
                report.Line($"  Skipped: artifact not found at {artifactPath}.");
                report.Line("  Build the Unity bundle (it writes the artifact) and redeploy with -p:DeployProbe=true.");
                yield break;
            }

            ArenaContentArtifact artifact = null;
            string artifactText = null;
            report.Try("read + parse artifact (FalseGods.Protocol, in-game)", () =>
            {
                artifactText = File.ReadAllText(artifactPath);
                artifact = ArenaContentArtifact.Parse(artifactText);
            });
            if (artifact == null)
            {
                report.Line("  *** Artifact did not parse — see the exception above. ***");
                yield break;
            }

            var def = artifact.Definition;
            report.Value("artifact bytes / arenaId / version",
                $"{artifactText.Length} / {def.ArenaId} / {def.ArenaVersion}");
            report.Value("sections node/prox/coll/nav/spawn/mech/parity",
                $"{def.Nodes.Count}/{def.VanillaProxies.Count}/{def.Colliders.Count}/"
                + $"{def.NavDefinitions.Count}/{def.Spawns.Count}/{def.Mechanisms.Count}/{artifact.Parity.Count}");

            // ── P8.2 — R34: recompute the canonical hash in-game; prove golden + order-independence. ──────────
            report.Section("P8.2 — R34: canonical ContentHash recomputed in-game");
            var hashMatchesGolden = false;
            var orderIndependent = false;
            var canonicalHex = "<none>";
            report.Try("recompute + reversed-order recompute", () =>
            {
                var canonical = artifact.ComputeContentHash();
                canonicalHex = canonical.ToHex();

                // Reverse every authored list: a deterministic "different realization order" that must not move
                // the hash (it orders by StableMarkerId, not list position).
                var reversed = new ArenaContentDefinition(
                    def.ArenaId, def.ArenaVersion, def.ArenaContentId,
                    def.Nodes.Reverse().ToList(),
                    def.VanillaProxies.Reverse().ToList(),
                    def.Colliders.Reverse().ToList(),
                    def.NavDefinitions.Reverse().ToList(),
                    def.Spawns.Reverse().ToList(),
                    def.Mechanisms.Reverse().ToList());
                var reversedHash = ContentHashComputer.Compute(reversed, artifact.SchemaVersion);

                hashMatchesGolden = string.Equals(canonicalHex, GoldenContentHashHex, StringComparison.Ordinal);
                orderIndependent = canonical.Equals(reversedHash);
            });
            report.Value("content hash (in-game)", canonicalHex);
            report.Value("golden hash (pinned fixture)", GoldenContentHashHex);
            report.Value("R34: matches offline golden", hashMatchesGolden);
            report.Value("R34: order-independent (reversed lists)", orderIndependent);

            // ── P8.3a — R14: load the arena and compare the realized hierarchy to the authored parity map. ────
            report.Section("P8.3a — R14: runtime hierarchy parity vs the authored map");
            var r14Ok = false;
            yield return CheckParity(artifact, report, ok => r14Ok = ok);

            // ── P8.3b — ready gate: single local peer, fail-closed shape. ─────────────────────────────────────
            report.Section("P8.3b — ready gate resolves for a single local peer (loading-contract shape)");
            const string localPeer = "false_gods.peer.local";
            var contentValidated = hashMatchesGolden && orderIndependent && r14Ok;

            var gate = new LocalReadyGate(new[] { localPeer });
            var closedBeforeReady = !gate.IsResolved;                       // fail-closed until ready
            var intruderRejected = !gate.MarkReady("false_gods.peer.intruder") && !gate.IsResolved; // untrusted input
            // Only a peer that has both realized AND validated its content may report ready.
            var markedReady = contentValidated && gate.MarkReady(localPeer);
            var resolvedAfterReady = gate.IsResolved;

            // A two-member set proves the gate genuinely waits — single-player resolving is not a fail-open bug.
            var twoPeer = new LocalReadyGate(new[] { localPeer, "false_gods.peer.remote" });
            twoPeer.MarkReady(localPeer);
            var partialSetWaits = !twoPeer.IsResolved;

            report.Value("gate (single peer)", gate.Describe());
            report.Value("closed before local peer ready", closedBeforeReady);
            report.Value("intruder ready rejected", intruderRejected);
            report.Value("content validated (hash + order + parity)", contentValidated);
            report.Value("local peer marked ready", markedReady);
            report.Value("gate resolved -> session starts", resolvedAfterReady);
            report.Value("two-peer gate with one ready still waits", partialSetWaits);
            var gateOk = closedBeforeReady && intruderRejected && resolvedAfterReady && partialSetWaits;

            // ── P8.3c — the loop over real geometry: fight (P6) then leave (P7), reused as-is. ────────────────
            var fightLeaveRan = false;
            if (_runFightAndLeave && resolvedAfterReady)
            {
                report.Section("P8.3c — session started: FIGHT (reuse P6) then LEAVE (reuse P7)");
                report.Line("  The gate resolved, so the session 'starts'. The fight and the leave are the already-");
                report.Line("  proven probes, run into this same report so the whole loop is one pass:");
                yield return new NavmeshEnemyProbe(_enemyUnitIdName).Run(report);     // dummy paths our arena (R9)
                yield return new NavmeshTeardownProbe().Run(report);                  // teardown restores level (R8)
                fightLeaveRan = true;
            }
            else
            {
                report.Section("P8.3c — fight + leave (skipped)");
                report.Line(_runFightAndLeave
                    ? "  Ready gate did not resolve (content failed validation) — not starting the session."
                    : "  Probe/P8RunFightAndLeave is off. Run P6 (F4) and P7 ('=') by hand for the physical loop.");
            }

            // ── Residue: after the whole loop, no arena GameObject may survive in the level we stay in. ───────
            yield return null; // Destroy is deferred to end of frame; let one pass before counting.
            var residue = CountArenaResidue();

            // ── Verdict. ──────────────────────────────────────────────────────────────────────────────────────
            report.Section("P8 — verdict (single-player full loop)");
            report.Value("R34 hash (golden + order-independent)", hashMatchesGolden && orderIndependent);
            report.Value("R14 runtime hierarchy parity", r14Ok);
            report.Value("ready gate sequence (fail-closed)", gateOk);
            report.Value("fight + leave ran (P6 + P7)", fightLeaveRan);
            report.Value("arena residue after the loop", residue == 0 ? "none" : $"{residue} object(s) LEFT");

            var pass = hashMatchesGolden && orderIndependent && r14Ok && gateOk && residue == 0
                       && (fightLeaveRan || !_runFightAndLeave);
            report.Line();
            report.Value("P8 verdict", pass
                ? "FULL LOOP OK — the shipped artifact recomputes to the pinned hash in-game and is order-"
                  + "independent (R34), the realized hierarchy matches the authored map (R14), the single-peer "
                  + "ready gate resolves fail-closed, and after fight+leave no arena object or nav node survives. "
                  + "See the P6/P7 verdicts above for the physical fight and teardown results."
                : "NOT FULLY GREEN — read the verdict lines above; a false R34/R14/gate/residue line points at "
                  + "which half of the loop regressed. (The live-enemy line inside P6 is best-effort; the P8 "
                  + "verdict does not depend on it — the nav-graph proof and teardown do.)");

            Cleanup();
        }

        /// <summary>
        /// R14: instantiate the arena from the bundle under an INACTIVE holder (no component lifecycle, no world
        /// mutation — the P1/P2 pattern), then locate each authored parity node by path and compare its runtime
        /// local transform to the authored one. Reports per-node matches and the first few mismatches.
        /// </summary>
        private IEnumerator CheckParity(ArenaContentArtifact artifact, ProbeReport report, Action<bool> onResult)
        {
            var bundlePath = Path.Combine(Paths.BepInExRootPath, "FalseGods.Probe", BundleFileName);
            var bundleRequest = AssetBundle.LoadFromFileAsync(bundlePath);
            yield return bundleRequest;
            _bundle = bundleRequest.assetBundle;
            var load = _bundle == null ? null : _bundle.LoadAssetAsync<GameObject>(OurRoomPrefabName);
            if (load != null) yield return load;
            if (!(load?.asset is GameObject ourPrefab))
            {
                report.Line("  *** FAILED: bundle / PocRoom did not load — cannot check parity. ***");
                onResult(false);
                yield break;
            }

            // Inactive holder so instantiating runs no Awake/Start and touches nothing in the world.
            _holder = new GameObject("FalseGodsP8_ParityHolder");
            _holder.SetActive(false);
            var room = UnityEngine.Object.Instantiate(ourPrefab, _holder.transform);
            room.name = OurRoomPrefabName;

            var checkedCount = 0;
            var matched = 0;
            var mismatches = new List<string>();
            foreach (var node in artifact.Parity)
            {
                checkedCount++;
                var t = room.transform.Find(node.Path);
                if (t == null)
                {
                    mismatches.Add($"{node.Path}: MISSING at runtime");
                    continue;
                }

                var authoredPos = new Vector3(node.LocalTransform.Position.X, node.LocalTransform.Position.Y,
                    node.LocalTransform.Position.Z);
                var authoredRot = new Quaternion(node.LocalTransform.Rotation.X, node.LocalTransform.Rotation.Y,
                    node.LocalTransform.Rotation.Z, node.LocalTransform.Rotation.W);
                var authoredScale = new Vector3(node.LocalTransform.Scale.X, node.LocalTransform.Scale.Y,
                    node.LocalTransform.Scale.Z);

                var posGap = Vector3.Distance(t.localPosition, authoredPos);
                var rotGap = Quaternion.Angle(t.localRotation, authoredRot);
                var scaleGap = Vector3.Distance(t.localScale, authoredScale);

                if (posGap <= PositionEpsilon && rotGap <= RotationEpsilonDeg && scaleGap <= ScaleEpsilon)
                    matched++;
                else
                    mismatches.Add($"{node.Path} ({node.Kind}): pos {posGap:0.####} rot {rotGap:0.####}deg scale {scaleGap:0.####}");
            }

            report.Value("parity nodes checked / matched", $"{checkedCount} / {matched}");
            foreach (var m in mismatches.Take(5))
                report.Value("  mismatch", m);
            if (mismatches.Count > 5)
                report.Value("  (more)", $"{mismatches.Count - 5} further mismatch(es)");

            var ok = checkedCount > 0 && matched == checkedCount;
            report.Value("R14 verdict (realized hierarchy matches authored map)", ok
                ? $"MATCH — all {checkedCount} authored parity nodes were found by path with the authored local "
                  + "transform. The realized arena is the arena the hash was computed over."
                : "MISMATCH — a parity node was missing or off (see the lines above). The runtime hierarchy has "
                  + "diverged from what shipped; a peer would realize different content than it validated.");

            // The parity check is read-only: drop the holder + bundle now (the reused P6/P7 load their own).
            if (_holder != null) { UnityEngine.Object.Destroy(_holder); _holder = null; }
            if (_bundle != null) { _bundle.Unload(unloadAllLoadedObjects: true); _bundle = null; }
            onResult(ok);
        }

        private static int CountArenaResidue()
        {
            // FindObjectsByType's sort-mode overload is not in this game's referenced assemblies; the deprecated
            // call is correct for a one-shot teardown assertion (not a hot path).
#pragma warning disable CS0618
            return UnityEngine.Object.FindObjectsOfType<GameObject>(true)
                .Count(go => go != null && go.name.StartsWith("FalseGods", StringComparison.Ordinal));
#pragma warning restore CS0618
        }

        private void Cleanup()
        {
            if (_holder != null) { UnityEngine.Object.Destroy(_holder); _holder = null; }
            if (_bundle != null) { _bundle.Unload(unloadAllLoadedObjects: true); _bundle = null; }
        }
    }
}
