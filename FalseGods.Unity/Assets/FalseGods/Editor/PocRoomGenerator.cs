using System;
using UnityEditor;
using UnityEngine;

namespace FalseGods.EditorTools
{
    /// <summary>
    /// Deterministically (re)generates the boss-arena prefab: a large flat cave floor, four boundary walls, a
    /// ceiling, scattered decorative rocks, and the PlayerSpawn/EnemySpawn markers. This is the payload for
    /// roadmap P2 (the hand-authored cave), authored entirely in code so it stays reproducible and the bundle
    /// never depends on a hand-placed binary asset.
    ///
    /// Direction B (arena content plan §2.1): the floor/walls/ceiling/rocks are OUR OWN meshes; at load the
    /// runtime BORROWS vanilla cave materials onto them by name from a pinned donor carrier (CaveNormal3New),
    /// so nothing vanilla is shipped. The placeholder URP/Lit materials assigned here only fill the renderer's
    /// sub-material slot before the borrow overwrites it. The material-borrow rows are authored by
    /// <see cref="PocArenaContentExporter"/>, which reads this prefab.
    ///
    /// Layer assignment follows the values measured in-game by the P0 probe
    /// (Docs/CollisionAndNavigationProposal.md §4.2):
    ///
    ///   - The recast graph rasterizes MESHES (not colliders) on
    ///     {Geometry(3), StaticDoodad(12), InvisibleGeometry(18), ProjectileTrigger(30)}.
    ///   - Physics / AI line-of-sight uses GameManager.geometryLayer
    ///     {Geometry(3), StaticDoodad(12), InvisibleGeometry(18), GeometryNoNavMesh(22), LevelGenBlock(26)}.
    ///
    /// Hence only the FLOOR mesh sits on Geometry(3) (walkable, rasterized). The walls/ceiling/rocks are pure
    /// VISUAL décor on Default(0): they are never rasterized and never collide, so they cannot disturb the
    /// proven walkable-floor nav (the minimal recast surface the PoC already validated). Physical containment
    /// is provided separately by collider-only boundary walls on GeometryNoNavMesh(22) — solid to physics,
    /// invisible to nav — exactly as §4.2 recommends for boundary walls.
    ///
    /// Existing assets are updated in place to keep their GUIDs (and therefore prefab references) stable across
    /// regenerations.
    /// </summary>
    public static class PocRoomGenerator
    {
        // Layer indices measured in-game (P0 probe) — see Docs/CollisionAndNavigationProposal.md §4.2.
        private const int GeometryLayer = 3;
        private const int GeometryNoNavMeshLayer = 22;
        private const int DefaultLayer = 0; // pure-visual décor: not rasterized, not collided

        // Cave dimensions (metres). A ~60x60 arena — roughly 2x the largest vanilla cave room (~33x30,
        // measured off CaveNormal3New), which direction B lets us exceed since we author our own space.
        private const float RoomSize = 60f;
        private const float FloorThickness = 0.5f;
        private const float WallHeight = 30f;   // tall cavern; ceiling caps at y = WallHeight
        private const float WallThickness = 1f;
        private const float CeilingThickness = 0.5f;

        // LightingRoot — two realtime lights. No baked lightmaps: SULFUR generates levels at runtime, so the
        // arena must light itself (Docs/MaterialCompatibilityReport §3.3). Ambient/fog are scene RenderSettings
        // and cannot ride a prefab, so the arena loader applies those.
        private const float KeyLightIntensity = 1.1f;   // directional key, global
        private const float FillLightIntensity = 4f;    // point fill, range-limited
        private const float FillLightRange = 70f;       // covers the larger footprint
        private const float FillLightHeight = 16f;

        private const string ArenaFolder = "Assets/FalseGods/Arenas/PocRoom";
        private const string MaterialsFolder = "Assets/FalseGods/Materials";

        public const string PrefabPath = ArenaFolder + "/PocRoom.prefab";

        // Decorative rocks stuck to the walls and ceiling (like vanilla caves). Pure visual, borrow Rocks_Caves.
        // Fixed transforms so the prefab — and therefore the content hash — is deterministic. Positions sit just
        // inside the wall inner faces (±(RoomSize/2)) or just under the ceiling, at varied scale/rotation.
        private static readonly (Vector3 pos, Vector3 euler, Vector3 scale)[] Rocks = new[]
        {
            // On the four walls (inner face at ±30), high up.
            (new Vector3(-14f, 20f,  29f), new Vector3(20f,  10f, 0f),   new Vector3(4f, 4f, 3f)),
            (new Vector3( 10f, 24f,  29f), new Vector3(-15f, -20f, 25f),  new Vector3(3f, 3f, 2.5f)),
            (new Vector3( 29f, 18f,  -6f), new Vector3(10f,  90f, -15f),  new Vector3(4.5f, 5f, 3f)),
            (new Vector3( 29f, 26f,  14f), new Vector3(-20f, 90f, 10f),   new Vector3(3f, 3.5f, 2.5f)),
            (new Vector3(-29f, 21f,   8f), new Vector3(15f, -90f, 20f),   new Vector3(4f, 4f, 3f)),
            (new Vector3(-29f, 27f, -12f), new Vector3(-10f, -90f, -20f), new Vector3(2.5f, 3f, 2f)),
            (new Vector3(  6f, 22f, -29f), new Vector3(25f, 180f, 5f),    new Vector3(4f, 4.5f, 3f)),
            (new Vector3(-18f, 25f, -29f), new Vector3(-20f, 180f, -15f), new Vector3(3f, 3f, 2.5f)),
            // On the ceiling (just below y = WallHeight), hanging.
            (new Vector3(-8f, 28.5f,  4f), new Vector3(160f, 30f, 10f),   new Vector3(5f, 4f, 5f)),
            (new Vector3(12f, 28.5f, -8f), new Vector3(150f, -40f, -20f), new Vector3(4f, 3.5f, 4f)),
        };

        [MenuItem("False Gods/Generate PoC Room Prefab")]
        public static void Generate()
        {
            AssertGameLayerNamesPresent();
            EnsureFolder(ArenaFolder);
            EnsureFolder(MaterialsFolder);

            var floorMesh = SaveMesh(BuildBoxMesh(new Vector3(RoomSize, FloorThickness, RoomSize)),
                ArenaFolder + "/FG_PocFloor.asset");
            var ceilingMesh = SaveMesh(BuildBoxMesh(new Vector3(RoomSize, CeilingThickness, RoomSize)),
                ArenaFolder + "/FG_PocCeiling.asset");
            var wallXMesh = SaveMesh(BuildBoxMesh(new Vector3(RoomSize, WallHeight, WallThickness)),
                ArenaFolder + "/FG_PocWallX.asset"); // runs along X (north/south walls)
            var wallZMesh = SaveMesh(BuildBoxMesh(new Vector3(WallThickness, WallHeight, RoomSize)),
                ArenaFolder + "/FG_PocWallZ.asset"); // runs along Z (east/west walls)
            var rockMesh = SaveMesh(BuildBoxMesh(Vector3.one),
                ArenaFolder + "/FG_PocRock.asset"); // unit cube, scaled per instance

            var groundMaterial = SaveUrpLitMaterial(MaterialsFolder + "/PocRoom_Ground.mat",
                new Color(0.42f, 0.40f, 0.38f));
            var wallMaterial = SaveUrpLitMaterial(MaterialsFolder + "/PocRoom_Wall.mat",
                new Color(0.35f, 0.34f, 0.33f));
            var ceilingMaterial = SaveUrpLitMaterial(MaterialsFolder + "/PocRoom_Ceiling.mat",
                new Color(0.25f, 0.24f, 0.24f));
            var rockMaterial = SaveUrpLitMaterial(MaterialsFolder + "/PocRoom_Rock.mat",
                new Color(0.45f, 0.43f, 0.40f));

            var meshes = new CaveMeshes(floorMesh, ceilingMesh, wallXMesh, wallZMesh, rockMesh);
            var materials = new CaveMaterials(groundMaterial, wallMaterial, ceilingMaterial, rockMaterial);

            var root = new GameObject("PocRoom");
            try
            {
                BuildHierarchy(root, meshes, materials);

                var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out var success);
                if (!success || prefab == null)
                    throw new InvalidOperationException($"SaveAsPrefabAsset failed for {PrefabPath}.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[FalseGods] Cave arena prefab written to {PrefabPath}.");
        }

        private readonly struct CaveMeshes
        {
            public readonly Mesh Floor, Ceiling, WallX, WallZ, Rock;
            public CaveMeshes(Mesh floor, Mesh ceiling, Mesh wallX, Mesh wallZ, Mesh rock)
            { Floor = floor; Ceiling = ceiling; WallX = wallX; WallZ = wallZ; Rock = rock; }
        }

        private readonly struct CaveMaterials
        {
            public readonly Material Ground, Wall, Ceiling, Rock;
            public CaveMaterials(Material ground, Material wall, Material ceiling, Material rock)
            { Ground = ground; Wall = wall; Ceiling = ceiling; Rock = rock; }
        }

        /// <summary>
        /// The prefab serializes layer *indices*, but authoring against wrong names would silently produce a
        /// room the game physics/nav cannot see. If ProjectSettings/TagManager.asset was not applied, stop.
        /// </summary>
        private static void AssertGameLayerNamesPresent()
        {
            AssertLayerName(GeometryLayer, "Geometry");
            AssertLayerName(GeometryNoNavMeshLayer, "GeometryNoNavMesh");
        }

        private static void AssertLayerName(int index, string expected)
        {
            var actual = LayerMask.LayerToName(index);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Layer {index} is named '{actual}', expected '{expected}'. " +
                    "ProjectSettings/TagManager.asset must define the game's measured layer names " +
                    "(Docs/CollisionAndNavigationProposal.md §4.2) before generating the room.");
        }

        private static void BuildHierarchy(GameObject root, CaveMeshes meshes, CaveMaterials materials)
        {
            var visualRoot = Child(root, "VisualRoot");
            var collisionRoot = Child(root, "CollisionRoot");
            BuildLighting(Child(root, "LightingRoot")); // realtime lights, no baked lightmaps
            Child(root, "NavigationRoot"); // NavmeshPrefab / rescan handled at load
            var gameplayRoot = Child(root, "GameplayRoot");

            // ── Visuals ────────────────────────────────────────────────────────────────────────────────────
            // Floor: top surface at y = 0, on Geometry(3) so the recast scan sees it (the one walkable mesh).
            AddMeshChild(visualRoot, "Floor", meshes.Floor, materials.Ground, GeometryLayer,
                new Vector3(0f, -FloorThickness / 2f, 0f));

            // Walls: pure-visual boxes on Default(0), inner face flush with the floor edge (±RoomSize/2).
            var wallOffset = (RoomSize + WallThickness) / 2f;
            var wallY = WallHeight / 2f;
            AddMeshChild(visualRoot, "Wall_N", meshes.WallX, materials.Wall, DefaultLayer,
                new Vector3(0f, wallY, wallOffset));
            AddMeshChild(visualRoot, "Wall_S", meshes.WallX, materials.Wall, DefaultLayer,
                new Vector3(0f, wallY, -wallOffset));
            AddMeshChild(visualRoot, "Wall_E", meshes.WallZ, materials.Wall, DefaultLayer,
                new Vector3(wallOffset, wallY, 0f));
            AddMeshChild(visualRoot, "Wall_W", meshes.WallZ, materials.Wall, DefaultLayer,
                new Vector3(-wallOffset, wallY, 0f));

            // Ceiling: caps the cavern, bottom face at y = WallHeight.
            AddMeshChild(visualRoot, "Ceiling", meshes.Ceiling, materials.Ceiling, DefaultLayer,
                new Vector3(0f, WallHeight + CeilingThickness / 2f, 0f));

            // Decorative rocks on the walls/ceiling (pure visual).
            for (var i = 0; i < Rocks.Length; i++)
            {
                var (pos, euler, scale) = Rocks[i];
                AddDecorMeshChild(visualRoot, $"Rock_{i + 1}", meshes.Rock, materials.Rock, DefaultLayer,
                    pos, Quaternion.Euler(euler), scale);
            }

            // ── Physics ────────────────────────────────────────────────────────────────────────────────────
            // Floor collider on Geometry(3) makes the ground solid.
            AddBoxCollider(collisionRoot, "FloorCollider", GeometryLayer,
                new Vector3(0f, -FloorThickness / 2f, 0f), new Vector3(RoomSize, FloorThickness, RoomSize));

            // Boundary walls: collider-only boxes on GeometryNoNavMesh(22) — solid, excluded from nav.
            var boundaryLength = RoomSize + 2f * WallThickness; // overlap the corners
            AddBoxCollider(collisionRoot, "WallNorth", GeometryNoNavMeshLayer,
                new Vector3(0f, wallY, wallOffset), new Vector3(boundaryLength, WallHeight, WallThickness));
            AddBoxCollider(collisionRoot, "WallSouth", GeometryNoNavMeshLayer,
                new Vector3(0f, wallY, -wallOffset), new Vector3(boundaryLength, WallHeight, WallThickness));
            AddBoxCollider(collisionRoot, "WallEast", GeometryNoNavMeshLayer,
                new Vector3(wallOffset, wallY, 0f), new Vector3(WallThickness, WallHeight, boundaryLength));
            AddBoxCollider(collisionRoot, "WallWest", GeometryNoNavMeshLayer,
                new Vector3(-wallOffset, wallY, 0f), new Vector3(WallThickness, WallHeight, boundaryLength));

            // ── Markers ────────────────────────────────────────────────────────────────────────────────────
            // Spawn logic is not the bundle's business — the runtime reads these positions from the artifact.
            // Player enters near the south wall; the boss stands across the arena, both on the floor (y = 0).
            Child(gameplayRoot, "PlayerSpawn").transform.localPosition = new Vector3(0f, 0f, -22f);
            Child(gameplayRoot, "EnemySpawn").transform.localPosition = new Vector3(0f, 0f, 14f);
        }

        /// <summary>
        /// Two realtime lights under the LightingRoot: a directional "key" that lights the arena regardless of
        /// position, plus a local point "fill" over the room centre sized to the larger footprint. Both are
        /// explicitly Realtime: the bundle must never depend on a source scene's baked lightmaps
        /// (Docs/MaterialCompatibilityReport §3.3).
        /// </summary>
        private static void BuildLighting(GameObject lightingRoot)
        {
            var key = Child(lightingRoot, "KeyLight");
            key.transform.localRotation = Quaternion.Euler(50f, -30f, 0f);
            var keyLight = key.AddComponent<Light>();
            keyLight.type = LightType.Directional;
            keyLight.lightmapBakeType = LightmapBakeType.Realtime;
            keyLight.intensity = KeyLightIntensity;
            keyLight.color = new Color(1f, 0.96f, 0.9f); // faintly warm
            keyLight.shadows = LightShadows.Soft;

            var fill = Child(lightingRoot, "FillLight");
            fill.transform.localPosition = new Vector3(0f, FillLightHeight, 0f);
            var fillLight = fill.AddComponent<Light>();
            fillLight.type = LightType.Point;
            fillLight.lightmapBakeType = LightmapBakeType.Realtime;
            fillLight.intensity = FillLightIntensity;
            fillLight.range = FillLightRange;
            fillLight.color = new Color(0.85f, 0.9f, 1f); // faintly cool
            fillLight.shadows = LightShadows.None;
        }

        private static GameObject Child(GameObject parent, string name)
        {
            var child = new GameObject(name);
            child.transform.SetParent(parent.transform, worldPositionStays: false);
            return child;
        }

        private static void AddMeshChild(GameObject parent, string name, Mesh mesh, Material material,
            int layer, Vector3 localPosition)
        {
            var child = Child(parent, name);
            child.layer = layer;
            child.transform.localPosition = localPosition;
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            child.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        private static void AddDecorMeshChild(GameObject parent, string name, Mesh mesh, Material material,
            int layer, Vector3 localPosition, Quaternion localRotation, Vector3 localScale)
        {
            var child = Child(parent, name);
            child.layer = layer;
            child.transform.localPosition = localPosition;
            child.transform.localRotation = localRotation;
            child.transform.localScale = localScale;
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            child.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        private static void AddBoxCollider(GameObject parent, string name, int layer,
            Vector3 localPosition, Vector3 size)
        {
            var child = Child(parent, name);
            child.layer = layer;
            child.transform.localPosition = localPosition;
            child.AddComponent<BoxCollider>().size = size;
        }

        /// <summary>
        /// An axis-aligned box with per-face normals and metre-scaled UVs, centred on the origin.
        /// Built in code so the "our own geometry" half of direction B owes nothing to Unity's built-in
        /// primitives (which live in the player's built-in resources, not in our bundle).
        /// </summary>
        private static Mesh BuildBoxMesh(Vector3 size)
        {
            var extents = size / 2f;

            var faces = new (Vector3 normal, Vector3 up, Vector2 faceSize)[]
            {
                (Vector3.up, Vector3.forward, new Vector2(size.x, size.z)),
                (Vector3.down, Vector3.back, new Vector2(size.x, size.z)),
                (Vector3.forward, Vector3.up, new Vector2(size.x, size.y)),
                (Vector3.back, Vector3.up, new Vector2(size.x, size.y)),
                (Vector3.right, Vector3.up, new Vector2(size.z, size.y)),
                (Vector3.left, Vector3.up, new Vector2(size.z, size.y)),
            };

            var vertices = new Vector3[24];
            var normals = new Vector3[24];
            var uv = new Vector2[24];
            var triangles = new int[36];

            for (var face = 0; face < 6; face++)
            {
                var (normal, up, faceSize) = faces[face];
                var right = Vector3.Cross(up, normal);
                var centre = Vector3.Scale(normal, extents);
                var halfRight = Vector3.Scale(right, extents);
                var halfUp = Vector3.Scale(up, extents);

                var v = face * 4;
                vertices[v + 0] = centre - halfRight - halfUp;
                vertices[v + 1] = centre - halfRight + halfUp;
                vertices[v + 2] = centre + halfRight + halfUp;
                vertices[v + 3] = centre + halfRight - halfUp;

                for (var i = 0; i < 4; i++)
                    normals[v + i] = normal;

                // metre-scaled UVs: one UV unit per metre keeps any borrowed cave texture density uniform
                // across floor/wall/ceiling regardless of face size — the projection-friendly mapping F11
                // proved renders CaveFloor/CaveWall correctly on our flat meshes.
                uv[v + 0] = new Vector2(0f, 0f);
                uv[v + 1] = new Vector2(0f, faceSize.y);
                uv[v + 2] = new Vector2(faceSize.x, faceSize.y);
                uv[v + 3] = new Vector2(faceSize.x, 0f);

                // Wind each face so its GEOMETRIC normal (cross(v1-v0, v2-v0)) points along `normal` — i.e.
                // outward. A* recast decides walkability from this triangle winding, NOT the supplied vertex
                // normals, so the floor's top face must wind to face UP; the earlier order wound it the other
                // way, so recast read the floor top as a ceiling and rasterized zero walkable nodes (measured
                // in-game, probe P5c, RiskList R4). Outward winding also renders correctly single-sided.
                var t = face * 6;
                triangles[t + 0] = v + 0;
                triangles[t + 1] = v + 2;
                triangles[t + 2] = v + 1;
                triangles[t + 3] = v + 0;
                triangles[t + 4] = v + 3;
                triangles[t + 5] = v + 2;
            }

            var mesh = new Mesh
            {
                vertices = vertices,
                normals = normals,
                uv = uv,
                triangles = triangles,
            };
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        /// <summary>Creates the asset, or copies into the existing one so its GUID stays stable.</summary>
        private static Mesh SaveMesh(Mesh built, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
            {
                built.name = System.IO.Path.GetFileNameWithoutExtension(path);
                AssetDatabase.CreateAsset(built, path);
                return built;
            }

            existing.Clear();
            existing.vertices = built.vertices;
            existing.normals = built.normals;
            existing.uv = built.uv;
            existing.triangles = built.triangles;
            existing.tangents = built.tangents;
            existing.RecalculateBounds();
            EditorUtility.SetDirty(existing);
            UnityEngine.Object.DestroyImmediate(built);
            return existing;
        }

        private static Material SaveUrpLitMaterial(string path, Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                throw new InvalidOperationException(
                    "Shader 'Universal Render Pipeline/Lit' not found — is the URP package installed?");

            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing == null)
            {
                var material = new Material(shader) { color = color };
                material.name = System.IO.Path.GetFileNameWithoutExtension(path);
                AssetDatabase.CreateAsset(material, path);
                return material;
            }

            existing.shader = shader;
            existing.color = color;
            EditorUtility.SetDirty(existing);
            return existing;
        }

        /// <summary>Creates every missing segment of an Assets/… folder path.</summary>
        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;

            var parent = System.IO.Path.GetDirectoryName(folder)?.Replace('\\', '/');
            var leaf = System.IO.Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf))
                throw new InvalidOperationException($"Cannot create asset folder '{folder}'.");

            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
