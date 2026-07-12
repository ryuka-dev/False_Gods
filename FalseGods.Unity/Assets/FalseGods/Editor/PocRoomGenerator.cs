using System;
using UnityEditor;
using UnityEngine;

namespace FalseGods.EditorTools
{
    /// <summary>
    /// Deterministically (re)generates the PoC test-room prefab described in
    /// Docs/MinimalProofOfConceptPlan.md §7.1: a flat ~20×20 m floor, four boundary walls, one central
    /// pillar, and the PlayerSpawn/EnemySpawn markers. This is the payload for PoC step P2 (RiskList R2:
    /// does a bundle built in the game's exact Unity version load under BepInEx?).
    ///
    /// Layer assignment follows the values measured in-game by the P0 probe
    /// (Docs/CollisionAndNavigationProposal.md §4.2):
    ///
    ///   - The recast graph rasterizes MESHES (not colliders) on
    ///     {Geometry(3), StaticDoodad(12), InvisibleGeometry(18), ProjectileTrigger(30)}.
    ///   - Physics / AI line-of-sight uses GameManager.geometryLayer
    ///     {Geometry(3), StaticDoodad(12), InvisibleGeometry(18), GeometryNoNavMesh(22), LevelGenBlock(26)}.
    ///
    /// Hence: floor and pillar carry a visible mesh on Geometry(3) (walkable / rasterized as obstacle) plus
    /// a collider (physics-solid); boundary walls are colliders only, on GeometryNoNavMesh(22) — solid to
    /// physics, invisible to the nav rasterization, exactly as §4.2 recommends for boundary walls.
    ///
    /// Everything is generated in code — meshes, materials, prefab — so the room is reproducible and the
    /// bundle never depends on hand-authored binary assets. Existing assets are updated in place to keep
    /// their GUIDs (and therefore prefab references) stable across regenerations.
    /// </summary>
    public static class PocRoomGenerator
    {
        // Layer indices measured in-game (P0 probe) — see Docs/CollisionAndNavigationProposal.md §4.2.
        private const int GeometryLayer = 3;
        private const int GeometryNoNavMeshLayer = 22;

        // Room dimensions per Docs/MinimalProofOfConceptPlan.md §7.1 (metres).
        private const float RoomSize = 20f;
        private const float FloorThickness = 0.5f;
        private const float WallHeight = 4f;
        private const float WallThickness = 1f;
        private const float PillarSize = 2f;
        private const float PillarHeight = 4f;

        // LightingRoot — two realtime lights per Docs/MinimalProofOfConceptPlan.md §7.1. No baked lightmaps:
        // SULFUR generates levels at runtime, so the arena must light itself (Docs/MaterialCompatibilityReport
        // §3.3). These travel in the bundle as real Light components; ambient/fog are scene RenderSettings and
        // cannot ride a prefab, so the arena loader (for the PoC, the probe's P3 section) applies those.
        private const float KeyLightIntensity = 1.1f;   // directional key, global
        private const float FillLightIntensity = 3f;    // point fill, range-limited
        private const float FillLightRange = 30f;
        private const float FillLightHeight = 6f;

        private const string ArenaFolder = "Assets/FalseGods/Arenas/PocRoom";
        private const string MaterialsFolder = "Assets/FalseGods/Materials";

        public const string PrefabPath = ArenaFolder + "/PocRoom.prefab";

        [MenuItem("False Gods/Generate PoC Room Prefab")]
        public static void Generate()
        {
            AssertGameLayerNamesPresent();
            EnsureFolder(ArenaFolder);
            EnsureFolder(MaterialsFolder);

            var floorMesh = SaveMesh(BuildBoxMesh(new Vector3(RoomSize, FloorThickness, RoomSize)),
                ArenaFolder + "/FG_PocFloor.asset");
            var pillarMesh = SaveMesh(BuildBoxMesh(new Vector3(PillarSize, PillarHeight, PillarSize)),
                ArenaFolder + "/FG_PocPillar.asset");

            var groundMaterial = SaveUrpLitMaterial(MaterialsFolder + "/PocRoom_Ground.mat",
                new Color(0.42f, 0.40f, 0.38f));
            var pillarMaterial = SaveUrpLitMaterial(MaterialsFolder + "/PocRoom_Pillar.mat",
                new Color(0.55f, 0.33f, 0.22f));

            var root = new GameObject("PocRoom");
            try
            {
                BuildHierarchy(root, floorMesh, pillarMesh, groundMaterial, pillarMaterial);

                var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out var success);
                if (!success || prefab == null)
                    throw new InvalidOperationException($"SaveAsPrefabAsset failed for {PrefabPath}.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[FalseGods] PoC room prefab written to {PrefabPath}.");
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

        private static void BuildHierarchy(GameObject root, Mesh floorMesh, Mesh pillarMesh,
            Material groundMaterial, Material pillarMaterial)
        {
            var visualRoot = Child(root, "VisualRoot");
            var collisionRoot = Child(root, "CollisionRoot");
            BuildLighting(Child(root, "LightingRoot")); // P3 — realtime lights, no baked lightmaps
            Child(root, "NavigationRoot"); // P5 — NavmeshPrefab / rescan comes later
            var gameplayRoot = Child(root, "GameplayRoot");

            // Visuals: floor top surface at y = 0; meshes on Geometry(3) so the recast scan sees them.
            AddMeshChild(visualRoot, "Floor", floorMesh, groundMaterial, GeometryLayer,
                new Vector3(0f, -FloorThickness / 2f, 0f));
            AddMeshChild(visualRoot, "Pillar", pillarMesh, pillarMaterial, GeometryLayer,
                new Vector3(0f, PillarHeight / 2f, 0f));

            // Physics: colliders make the same shapes solid. Floor/pillar on Geometry(3).
            AddBoxCollider(collisionRoot, "FloorCollider", GeometryLayer,
                new Vector3(0f, -FloorThickness / 2f, 0f), new Vector3(RoomSize, FloorThickness, RoomSize));
            AddBoxCollider(collisionRoot, "PillarCollider", GeometryLayer,
                new Vector3(0f, PillarHeight / 2f, 0f), new Vector3(PillarSize, PillarHeight, PillarSize));

            // Boundary walls: collider-only boxes on GeometryNoNavMesh(22) — solid, excluded from nav.
            var wallY = WallHeight / 2f;
            var wallOffset = (RoomSize + WallThickness) / 2f;
            var wallLength = RoomSize + 2f * WallThickness; // overlap the corners
            AddBoxCollider(collisionRoot, "WallNorth", GeometryNoNavMeshLayer,
                new Vector3(0f, wallY, wallOffset), new Vector3(wallLength, WallHeight, WallThickness));
            AddBoxCollider(collisionRoot, "WallSouth", GeometryNoNavMeshLayer,
                new Vector3(0f, wallY, -wallOffset), new Vector3(wallLength, WallHeight, WallThickness));
            AddBoxCollider(collisionRoot, "WallEast", GeometryNoNavMeshLayer,
                new Vector3(wallOffset, wallY, 0f), new Vector3(WallThickness, WallHeight, wallLength));
            AddBoxCollider(collisionRoot, "WallWest", GeometryNoNavMeshLayer,
                new Vector3(-wallOffset, wallY, 0f), new Vector3(WallThickness, WallHeight, wallLength));

            // Markers only — spawn logic is not the bundle's business.
            Child(gameplayRoot, "PlayerSpawn").transform.localPosition = new Vector3(-7f, 0f, -7f);
            Child(gameplayRoot, "EnemySpawn").transform.localPosition = new Vector3(7f, 0f, 7f);
        }

        /// <summary>
        /// Two realtime lights under the LightingRoot (§7.1): a directional "key" that lights the arena — and
        /// any runtime-instantiated vanilla prefab placed in it (P3) — regardless of position, plus a local
        /// point "fill" over the room centre. Both are explicitly Realtime: the bundle must never depend on a
        /// source scene's baked lightmaps (Docs/MaterialCompatibilityReport §3.3).
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
        /// Built in code so the "our own ground mesh" half of P2 owes nothing to Unity's built-in
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

                // metre-scaled UVs: one UV unit per metre keeps any test texture density uniform
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
