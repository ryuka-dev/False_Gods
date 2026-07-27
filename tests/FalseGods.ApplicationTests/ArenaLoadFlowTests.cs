using System;
using System.Collections.Generic;
using System.Linq;
using FalseGods.Application.Arena;
using FalseGods.Protocol.Arena;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Arena;
using Xunit;
using ProtoQuaternion = FalseGods.Protocol.Arena.Quaternion;
using ProtoVector3 = FalseGods.Protocol.Arena.Vector3;

namespace FalseGods.ApplicationTests
{
    /// <summary>
    /// The canonical local load sequence (Docs/MultiplayerLoadingContract.md §5.3 steps 2–4): ordering, R14
    /// parity, marker resolution, manifest production, and the fail-closed teardown of every failure path.
    /// </summary>
    public sealed class ArenaLoadFlowTests
    {
        private static StableMarkerId Marker(int seed) =>
            new StableMarkerId(new Guid($"{seed:00000000}-0000-0000-0000-000000000000"));

        private static AuthoredTransform Authored(float x, float y, float z) => new AuthoredTransform(
            new ProtoVector3(x, y, z), ProtoQuaternion.Identity, new ProtoVector3(1f, 1f, 1f));

        /// <summary>A minimal but hash-valid artifact: a root node, the two spawns, and a parity map covering
        /// the marker ancestor chain.</summary>
        private static ArenaContentArtifact Artifact() => new ArenaContentArtifact(
            new ArenaContentDefinition(
                "false_gods.arena.test",
                ArenaVersion: 2,
                ArenaContentId: "assets/falsegods/test.prefab",
                Nodes: new[] { new AuthoredNode(Marker(1), "ArenaRoot", null, Authored(0f, 0f, 0f)) },
                VanillaProxies: Array.Empty<VanillaProxyDefinition>(),
                Colliders: Array.Empty<ColliderDefinition>(),
                NavDefinitions: Array.Empty<NavDefinition>(),
                Spawns: new[]
                {
                    new SpawnDefinition(Marker(2), "Player", "false_gods.spawn.player", Authored(-7f, 0f, -7f)),
                    new SpawnDefinition(Marker(3), "Enemy", "false_gods.spawn.dummy", Authored(7f, 0f, 7f)),
                },
                Mechanisms: Array.Empty<MechanismDefinition>(),
                MaterialBorrows: Array.Empty<MaterialBorrowDefinition>()),
            new ContentHashSchemaVersion(1),
            ProtocolVersion: 1, // deliberately stale: the reported manifest must stamp the runtime's version
            BundleVersion: "test.bundle.1",
            Parity: new[]
            {
                new ArenaParityNode("GameplayRoot", "GameplayRoot", Authored(0f, 1f, 0f)),
                new ArenaParityNode("GameplayRoot/PlayerSpawn", "Player", Authored(-7f, 0f, -7f)),
                new ArenaParityNode("GameplayRoot/EnemySpawn", "Enemy", Authored(7f, 0f, 7f)),
            },
            MaterialBorrowPlacements: Array.Empty<MaterialBorrowPlacement>());

        /// <summary>The base artifact plus one material borrow (targeting the root node) with its runtime target
        /// path placement — a hash-valid artifact that exercises the borrow request-building path.</summary>
        private static ArenaContentArtifact ArtifactWithBorrow()
        {
            var baseArtifact = Artifact();
            var definition = baseArtifact.Definition with
            {
                MaterialBorrows = new[]
                {
                    new MaterialBorrowDefinition(Marker(9), Marker(1), 0, "carrier-guid", "CaveFloor"),
                },
            };
            return baseArtifact with
            {
                Definition = definition,
                MaterialBorrowPlacements = new[] { new MaterialBorrowPlacement(Marker(9), "VisualRoot/Floor") },
            };
        }

        // ---------------------------------------------------------------- fakes

        private sealed class FakeAssets : IArenaAssetProvider
        {
            private readonly List<string> _journal;

            public FakeAssets(List<string> journal, string? artifactText)
            {
                _journal = journal;
                ArtifactText = artifactText;
            }

            public string? ArtifactText { get; set; }

            public ArenaAssetLoadResult Load()
            {
                _journal.Add("assets.Load");
                return ArtifactText is null
                    ? ArenaAssetLoadResult.Failed("bundle missing")
                    : ArenaAssetLoadResult.Loaded(ArtifactText);
            }

            public void Release() => _journal.Add("assets.Release");
        }

        private sealed class FakeRealization : IArenaRealization
        {
            private readonly List<string> _journal;
            private readonly ArenaContentArtifact _authored;

            public FakeRealization(List<string> journal, ArenaContentArtifact authored)
            {
                _journal = journal;
                _authored = authored;
            }

            public bool FailRealize { get; set; }

            public string? OmitParityPath { get; set; }

            public string? PerturbParityPath { get; set; }

            public bool OmitMarkers { get; set; }

            public ArenaWorldPoint? CapturedOrigin { get; private set; }

            /// <summary>Members this fake reports for each requested marker group, keyed by group path.</summary>
            public Dictionary<string, IReadOnlyList<ArenaWorldPoint>> GroupMembers { get; } =
                new Dictionary<string, IReadOnlyList<ArenaWorldPoint>>(StringComparer.Ordinal);

            /// <summary>The local scale this fake reports for a named marker, keyed by path; markers not listed
            /// report unit scale.</summary>
            public Dictionary<string, ArenaWorldPoint> MarkerScales { get; } =
                new Dictionary<string, ArenaWorldPoint>(StringComparer.Ordinal);

            public ArenaRealizationResult Realize(
                ArenaWorldPoint origin,
                IReadOnlyList<string> parityPaths,
                IReadOnlyList<string> markerPaths,
                IReadOnlyList<string> markerGroupPaths)
            {
                _journal.Add("realize");
                CapturedOrigin = origin;
                if (FailRealize)
                {
                    return ArenaRealizationResult.Failed("instantiate blew up");
                }

                // Echo the authored transforms back, as a faithful realization would.
                var byPath = _authored.Parity.ToDictionary(p => p.Path, p => p.LocalTransform, StringComparer.Ordinal);
                var nodes = new List<RealizedParityNode>();
                foreach (var path in parityPaths)
                {
                    if (string.Equals(path, OmitParityPath, StringComparison.Ordinal) || !byPath.TryGetValue(path, out var t))
                    {
                        continue;
                    }

                    var nudge = string.Equals(path, PerturbParityPath, StringComparison.Ordinal) ? 0.01f : 0f;
                    nodes.Add(new RealizedParityNode(
                        path,
                        new ArenaWorldPoint(t.Position.X + nudge, t.Position.Y, t.Position.Z),
                        new ArenaRotation(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W),
                        new ArenaWorldPoint(t.Scale.X, t.Scale.Y, t.Scale.Z)));
                }

                var markers = new List<RealizedMarker>();
                if (!OmitMarkers)
                {
                    for (var i = 0; i < markerPaths.Count; i++)
                    {
                        var path = markerPaths[i];
                        var scale = MarkerScales.TryGetValue(path, out var authored)
                            ? authored
                            : new ArenaWorldPoint(1f, 1f, 1f);
                        markers.Add(new RealizedMarker(path, new ArenaWorldPoint(100f + i, 5f, 200f + i), scale));
                    }
                }

                foreach (var groupPath in markerGroupPaths)
                {
                    if (!GroupMembers.TryGetValue(groupPath, out var members))
                    {
                        continue; // an absent group reports nothing, as the real one does
                    }

                    for (var i = 0; i < members.Count; i++)
                    {
                        markers.Add(new RealizedMarker(
                            groupPath + "/Member_" + i, members[i], new ArenaWorldPoint(1f, 1f, 1f)));
                    }
                }

                return new ArenaRealizationResult(true, null, nodes, markers);
            }

            public void Teardown() => _journal.Add("realize.Teardown");
        }

        private sealed class FakeNavigation : INavigationPort
        {
            private readonly List<string> _journal;

            public FakeNavigation(List<string> journal) => _journal = journal;

            public bool FailApply { get; set; }

            public NavigationApplyResult Apply()
            {
                _journal.Add("nav.Apply");
                return FailApply ? NavigationApplyResult.Failed("bake produced 0 triangles") : NavigationApplyResult.Applied(16);
            }

            public void Remove() => _journal.Add("nav.Remove");
        }

        private sealed class FakeVanillaAssets : IVanillaAssetProvider
        {
            private readonly List<string> _journal;

            public FakeVanillaAssets(List<string> journal) => _journal = journal;

            public bool FailResolve { get; set; }

            public IReadOnlyList<MaterialBorrowRequest>? CapturedRequests { get; private set; }

            public MaterialConventionPaint? CapturedPaint { get; private set; }

            public MaterialBorrowResult Resolve(IReadOnlyList<MaterialBorrowRequest> requests)
            {
                _journal.Add("vanilla.Resolve");
                CapturedRequests = requests;
                return FailResolve
                    ? MaterialBorrowResult.Failed("carrier did not load")
                    : MaterialBorrowResult.Resolved(requests.Count);
            }

            public MaterialBorrowResult PaintByConvention(MaterialConventionPaint paint)
            {
                CapturedPaint = paint;
                return MaterialBorrowResult.Resolved(0);
            }

            public SubmeshBorrow? CapturedSubmeshBorrow { get; private set; }

            public MaterialBorrowResult PaintSubmeshes(SubmeshBorrow borrow)
            {
                CapturedSubmeshBorrow = borrow;
                return MaterialBorrowResult.Resolved(0);
            }

            public VanillaPropClone? CapturedPropClone { get; private set; }

            public bool FailPropClone { get; set; }

            public VanillaPropResult CloneProps(VanillaPropClone request)
            {
                _journal.Add("vanilla.CloneProps");
                CapturedPropClone = request;
                return FailPropClone
                    ? VanillaPropResult.Failed("donor room did not load")
                    : VanillaPropResult.Placed(0);
            }

            public void Release() => _journal.Add("vanilla.Release");
        }

        private sealed class Rig
        {
            public readonly List<string> Journal = new List<string>();
            public readonly ArenaContentArtifact Authored = Artifact();
            public readonly FakeAssets Assets;
            public readonly FakeRealization Realization;
            public readonly FakeNavigation Navigation;
            public readonly FakeVanillaAssets Vanilla;
            public readonly ArenaLoadFlow Flow;

            public Rig(string? artifactText = "unset")
            {
                Assets = new FakeAssets(Journal, artifactText == "unset" ? Authored.Serialize() : artifactText);
                Realization = new FakeRealization(Journal, Authored);
                Navigation = new FakeNavigation(Journal);
                Vanilla = new FakeVanillaAssets(Journal);
                Flow = new ArenaLoadFlow(Assets, Realization, Navigation, Vanilla);
            }
        }

        private static readonly ArenaWorldPoint Origin = new ArenaWorldPoint(10f, 2f, 20f);

        // ---------------------------------------------------------------- happy path

        [Fact]
        public void Canonical_order_load_realize_navigation()
        {
            var rig = new Rig();

            Assert.True(rig.Flow.Prepare().Success);
            Assert.True(rig.Flow.Realize(Origin).Success);

            Assert.Equal(new[] { "assets.Load", "realize", "vanilla.Resolve", "vanilla.CloneProps", "nav.Apply" }, rig.Journal);
            Assert.Equal(ArenaLoadStage.Realized, rig.Flow.Stage);
            Assert.Equal(Origin, rig.Realization.CapturedOrigin);
        }

        [Fact]
        public void Manifest_carries_recomputed_hash_and_runtime_protocol_version()
        {
            var rig = new Rig();
            rig.Flow.Prepare();
            var result = rig.Flow.Realize(Origin);

            var manifest = Assert.IsType<ArenaManifest>(result.Manifest);
            Assert.Equal("false_gods.arena.test", manifest.ArenaId);
            Assert.Equal(2, manifest.ArenaVersion);
            Assert.Equal(new ContentHashSchemaVersion(1), manifest.ContentHashSchemaVersion);
            Assert.Equal(rig.Authored.ComputeContentHash(), manifest.ContentHash);
            Assert.Equal("test.bundle.1", manifest.BundleVersion);
            // The artifact was stamped with protocol 1; the peers must agree on what they RUN.
            Assert.Equal(ProtocolVersion.Current.Value, manifest.ProtocolVersion);
        }

        [Fact]
        public void Realized_arena_reports_marker_world_positions_and_nav_count()
        {
            var rig = new Rig();
            rig.Flow.Prepare();
            var arena = rig.Flow.Realize(Origin).Arena;

            Assert.NotNull(arena);
            Assert.Equal(new ArenaWorldPoint(100f, 5f, 200f), arena!.PlayerSpawn);
            Assert.Equal(new ArenaWorldPoint(101f, 5f, 201f), arena.BossSpawn);
            Assert.Equal(16, arena.NavWalkableNodes);
            Assert.Equal(Origin, arena.Origin);
        }

        // ---------------------------------------------------------------- authored boss content

        [Fact]
        public void Boss_anchors_come_from_the_authored_group_in_authored_order()
        {
            var rig = new Rig();
            rig.Realization.GroupMembers[BossRoomContent.AnchorGroupPath] = new[]
            {
                new ArenaWorldPoint(-8.18f, 8.1f, 25.97f),
                new ArenaWorldPoint(0.24f, 0.1f, -7.69f),
            };
            rig.Flow.Prepare();

            var arena = rig.Flow.Realize(Origin).Arena;

            Assert.NotNull(arena);
            Assert.Equal(2, arena!.BossAnchors.Count);
            Assert.Equal(new ArenaWorldPoint(-8.18f, 8.1f, 25.97f), arena.BossAnchors[0]);
            Assert.Equal(new ArenaWorldPoint(0.24f, 0.1f, -7.69f), arena.BossAnchors[1]);
        }

        [Fact]
        public void A_room_that_authored_no_anchors_loads_with_none()
        {
            var rig = new Rig();
            rig.Flow.Prepare();

            var arena = rig.Flow.Realize(Origin).Arena;

            Assert.NotNull(arena);
            Assert.Empty(arena!.BossAnchors);
        }

        [Fact]
        public void Boss_size_is_the_body_markers_uniform_scale()
        {
            var rig = new Rig();
            rig.Realization.MarkerScales[BossRoomContent.BodyPath] = new ArenaWorldPoint(2.5f, 2.5f, 2.5f);
            rig.Flow.Prepare();

            var arena = rig.Flow.Realize(Origin).Arena;

            Assert.NotNull(arena);
            Assert.Equal(2.5f, arena!.BossSize);
        }

        [Theory]
        [InlineData(2f, 1f, 2f)] // non-uniform: not a size
        [InlineData(0f, 0f, 0f)] // degenerate: would collapse the boss to a point
        [InlineData(-2f, -2f, -2f)] // negative: would mirror the rig
        public void An_unusable_authored_scale_reads_as_no_size_rather_than_a_broken_boss(float x, float y, float z)
        {
            var rig = new Rig();
            rig.Realization.MarkerScales[BossRoomContent.BodyPath] = new ArenaWorldPoint(x, y, z);
            rig.Flow.Prepare();

            var arena = rig.Flow.Realize(Origin).Arena;

            Assert.NotNull(arena);
            Assert.Equal(0f, arena!.BossSize);
        }

        // ---------------------------------------------------------------- fail-closed paths

        [Fact]
        public void Missing_content_fails_prepare_and_releases()
        {
            var rig = new Rig(artifactText: null);

            var result = rig.Flow.Prepare();

            Assert.False(result.Success);
            Assert.Contains("unavailable", result.FailureReason);
            Assert.Equal(new[] { "assets.Load", "assets.Release" }, rig.Journal);
            Assert.Equal(ArenaLoadStage.NotLoaded, rig.Flow.Stage);
        }

        [Fact]
        public void Unparseable_artifact_fails_prepare_and_releases()
        {
            var rig = new Rig(artifactText: "not an artifact");

            var result = rig.Flow.Prepare();

            Assert.False(result.Success);
            Assert.Contains("invalid", result.FailureReason);
            Assert.Equal(new[] { "assets.Load", "assets.Release" }, rig.Journal);
        }

        [Fact]
        public void Realization_failure_tears_down_in_reverse_order()
        {
            var rig = new Rig();
            rig.Realization.FailRealize = true;
            rig.Flow.Prepare();

            var result = rig.Flow.Realize(Origin);

            Assert.False(result.Success);
            Assert.Contains("realization failed", result.FailureReason);
            Assert.Equal(
                new[] { "assets.Load", "realize", "nav.Remove", "realize.Teardown", "vanilla.Release", "assets.Release" },
                rig.Journal);
            Assert.Equal(ArenaLoadStage.NotLoaded, rig.Flow.Stage);
        }

        [Fact]
        public void Parity_mismatch_fails_closed_and_names_the_node()
        {
            var rig = new Rig();
            rig.Realization.PerturbParityPath = "GameplayRoot/PlayerSpawn";
            rig.Flow.Prepare();

            var result = rig.Flow.Realize(Origin);

            Assert.False(result.Success);
            Assert.Contains("GameplayRoot/PlayerSpawn", result.FailureReason);
            Assert.Contains("realize.Teardown", rig.Journal);
            Assert.Equal(ArenaLoadStage.NotLoaded, rig.Flow.Stage);
        }

        [Fact]
        public void Parity_node_missing_at_runtime_fails_closed()
        {
            var rig = new Rig();
            rig.Realization.OmitParityPath = "GameplayRoot/EnemySpawn";
            rig.Flow.Prepare();

            var result = rig.Flow.Realize(Origin);

            Assert.False(result.Success);
            Assert.Contains("missing at runtime", result.FailureReason);
        }

        [Fact]
        public void Missing_marker_fails_closed()
        {
            var rig = new Rig();
            rig.Realization.OmitMarkers = true;
            rig.Flow.Prepare();

            var result = rig.Flow.Realize(Origin);

            Assert.False(result.Success);
            Assert.Contains("missing marker", result.FailureReason);
        }

        [Fact]
        public void Navigation_failure_tears_down_everything()
        {
            var rig = new Rig();
            rig.Navigation.FailApply = true;
            rig.Flow.Prepare();

            var result = rig.Flow.Realize(Origin);

            Assert.False(result.Success);
            Assert.Contains("navigation failed", result.FailureReason);
            Assert.Equal(
                new[] { "assets.Load", "realize", "vanilla.Resolve", "vanilla.CloneProps", "nav.Apply", "nav.Remove", "realize.Teardown", "vanilla.Release", "assets.Release" },
                rig.Journal);
            Assert.Equal(ArenaLoadStage.NotLoaded, rig.Flow.Stage);
        }

        [Fact]
        public void Material_borrow_failure_tears_down_everything()
        {
            var rig = new Rig();
            rig.Vanilla.FailResolve = true;
            rig.Flow.Prepare();

            var result = rig.Flow.Realize(Origin);

            Assert.False(result.Success);
            Assert.Contains("material borrow failed", result.FailureReason);
            // Borrow is resolved after realization and before navigation, so nav.Apply never runs.
            Assert.Equal(
                new[] { "assets.Load", "realize", "vanilla.Resolve", "nav.Remove", "realize.Teardown", "vanilla.Release", "assets.Release" },
                rig.Journal);
            Assert.Equal(ArenaLoadStage.NotLoaded, rig.Flow.Stage);
        }

        [Fact]
        public void Material_borrow_request_carries_the_placement_target_path()
        {
            var rig = new Rig(ArtifactWithBorrow().Serialize());

            rig.Flow.Prepare();
            var result = rig.Flow.Realize(Origin);

            Assert.True(result.Success);
            var request = Assert.Single(rig.Vanilla.CapturedRequests!);
            Assert.Equal("VisualRoot/Floor", request.TargetPath);   // from the non-hashed placement
            Assert.Equal("CaveFloor", request.MaterialName);        // from the hashed definition
            Assert.Equal("carrier-guid", request.CarrierGuid);
            Assert.Equal(0, request.TargetSubMaterialIndex);
        }

        [Fact]
        public void Vanilla_prop_failure_tears_down_everything()
        {
            var rig = new Rig();
            rig.Vanilla.FailPropClone = true;
            rig.Flow.Prepare();

            var result = rig.Flow.Realize(Origin);

            Assert.False(result.Success);
            Assert.Contains("vanilla scenery failed", result.FailureReason);
            // Scenery is cloned after the paints and before navigation, so nav.Apply never runs.
            Assert.Equal(
                new[] { "assets.Load", "realize", "vanilla.Resolve", "vanilla.CloneProps", "nav.Remove", "realize.Teardown", "vanilla.Release", "assets.Release" },
                rig.Journal);
            Assert.Equal(ArenaLoadStage.NotLoaded, rig.Flow.Stage);
        }

        [Fact]
        public void Vanilla_prop_recipe_keeps_the_pool_off_the_navigation_layers_and_strips_its_gameplay()
        {
            var rig = new Rig();

            rig.Flow.Prepare();
            var result = rig.Flow.Realize(Origin);

            Assert.True(result.Success);
            var request = rig.Vanilla.CapturedPropClone!;
            Assert.Equal("VisualRoot/VanillaProps", request.ParentPath);
            Assert.Equal("Prop_MudPool", request.MarkerNamePrefix);
            Assert.Equal("Enemies/CousinSludgePool", request.PropPath);

            // The donor room is named by the key the running game answers to, not by an exported GUID.
            Assert.EndsWith("CaveCousinNew.prefab", request.RoomKey);

            // The prop's own layer is rasterized by the navigation scan; ours must not be, or a decorative
            // basin silently becomes terrain.
            Assert.Equal("GeometryNoNavMesh", request.LayerName);

            // The donor boss's anchor, its damage volume and its pool controller stay in the donor room.
            Assert.Contains("CousinPosition", request.StripChildNames);
            Assert.Contains("PoolBlocker", request.StripChildNames);
            Assert.Contains("CousinPool", request.StripComponentNames);
        }

        // ---------------------------------------------------------------- teardown & guards

        [Fact]
        public void Teardown_runs_reverse_order_and_resets()
        {
            var rig = new Rig();
            rig.Flow.Prepare();
            rig.Flow.Realize(Origin);
            rig.Journal.Clear();

            rig.Flow.Teardown();

            Assert.Equal(new[] { "nav.Remove", "realize.Teardown", "vanilla.Release", "assets.Release" }, rig.Journal);
            Assert.Equal(ArenaLoadStage.NotLoaded, rig.Flow.Stage);
            Assert.Null(rig.Flow.Manifest);
            Assert.Null(rig.Flow.Arena);
            Assert.Null(rig.Flow.Artifact);
        }

        [Fact]
        public void Stage_guards_reject_out_of_order_calls()
        {
            var rig = new Rig();

            Assert.Throws<InvalidOperationException>(() => rig.Flow.Realize(Origin));
            rig.Flow.Prepare();
            Assert.Throws<InvalidOperationException>(() => rig.Flow.Prepare());
            rig.Flow.Realize(Origin);
            Assert.Throws<InvalidOperationException>(() => rig.Flow.Prepare());
        }

        // ---------------------------------------------------------------- placement

        [Fact]
        public void Origin_puts_the_authored_player_spawn_at_the_player_foot()
        {
            // Marker chain: GameplayRoot (0,1,0) + PlayerSpawn (-7,0,-7) → offset (-7,1,-7).
            var origin = ArenaPlacement.OriginForPlayerFoot(Artifact(), new ArenaWorldPoint(10f, 5f, 20f));

            Assert.Equal(new ArenaWorldPoint(17f, 4f, 27f), origin);
        }

        [Fact]
        public void Placement_refuses_a_rotated_ancestor_rather_than_misplacing()
        {
            var artifact = Artifact();
            var rotated = artifact with
            {
                Parity = new[]
                {
                    new ArenaParityNode("GameplayRoot", "GameplayRoot", new AuthoredTransform(
                        new ProtoVector3(0f, 1f, 0f), new ProtoQuaternion(0f, 0.7071f, 0f, 0.7071f), new ProtoVector3(1f, 1f, 1f))),
                    artifact.Parity[1],
                    artifact.Parity[2],
                },
            };

            Assert.Throws<InvalidOperationException>(
                () => ArenaPlacement.OriginForPlayerFoot(rotated, new ArenaWorldPoint(0f, 0f, 0f)));
        }

        [Fact]
        public void Placement_refuses_an_artifact_without_a_player_marker()
        {
            var artifact = Artifact();
            var withoutPlayer = artifact with { Parity = new[] { artifact.Parity[0], artifact.Parity[2] } };

            Assert.Throws<InvalidOperationException>(
                () => ArenaPlacement.OriginForPlayerFoot(withoutPlayer, new ArenaWorldPoint(0f, 0f, 0f)));
        }
    }
}
