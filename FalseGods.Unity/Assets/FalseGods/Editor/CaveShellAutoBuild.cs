using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace FalseGods.EditorTools
{
    /// <summary>
    /// Blender→game auto-pipeline for the hand-sculpted cave wall. When <c>CaveShell.fbx</c> is (re)imported —
    /// e.g. exported from Blender straight into the project, overwriting in place — this rebuilds the arena
    /// AssetBundle and copies the bundle + content artifact into each local game deploy target. The only manual
    /// steps left are "export from Blender" and "reload the level in-game".
    ///
    /// It touches only the bundle + artifact (which carry the mesh), never the plugin DLLs, so it never fights the
    /// game's DLL locks while the game is running. Deploy targets are read from a git-ignored
    /// <c>LocalDeployTargets.txt</c> at the Unity project root (one absolute plugin-folder path per line, '#'
    /// comments allowed), so no machine-specific path is committed.
    /// </summary>
    public sealed class CaveShellAutoBuild : AssetPostprocessor
    {
        private const string WatchedFbx = "Assets/FalseGods/Arenas/PocRoom/CaveShell.fbx";
        private const string PrefabPath = "Assets/FalseGods/Arenas/PocRoom/PocRoom.prefab";
        private const string ArenaFolder = "Assets/FalseGods/Arenas/PocRoom";
        private const string ShellPath = "VisualRoot/CaveShell";
        private const string WalkablePath = "VisualRoot/CaveWalkable";
        private const string MaterialsFolder = "Assets/FalseGods/Materials";

        private const string WalkableSlot = "Floor";

        // Measured in-game (F10 probe): the recast graph rasterizes MESHES on {3 Geometry, 12 StaticDoodad,
        // 18 InvisibleGeometry, 30 ProjectileTrigger}, and GameManager.geometryLayer (physics + line of sight) is
        // {3, 12, 18, 22 GeometryNoNavMesh, 26 LevelGenBlock}.
        private const int WalkableLayer = 3;   // rasterized, solid, and NOT in the thrown-crate wall mask: a floor
        private const int SolidLayer = 22;     // solid and crate-breaking, but invisible to the navigation scan

        /// <summary>
        /// The authored material slots, by the meaningful part of their Blender name (anything after the last
        /// underscore, so the "01_"/"02_" ordering prefixes may be renumbered freely), and the placeholder material
        /// each one gets in the prefab. The placeholder is what the runtime borrow matches on, which is why keeping
        /// it aligned with the imported sub-meshes matters — see <see cref="SyncShellMaterials"/>.
        /// </summary>
        private static readonly (string Slot, string Placeholder)[] SlotPlaceholders =
        {
            ("WallBot", "FG_WallBot"),
            ("WallMid", "FG_WallMid"),
            ("WallTop", "FG_WallTop"),
            ("Floor", "FG_Floor"),
            ("Ceiling", "FG_Ceiling"),
        };

        private static void OnPostprocessAllAssets(
            string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            var touched = false;
            foreach (var path in importedAssets)
            {
                if (string.Equals(path, WatchedFbx, StringComparison.OrdinalIgnoreCase))
                {
                    touched = true;
                    break;
                }
            }

            // Building an AssetBundle mid-import is unsafe — defer past the current import pipeline.
            if (touched)
                EditorApplication.delayCall += RebuildAndDeploy;
        }

        private static void RebuildAndDeploy()
        {
            try
            {
                SyncShellMaterials(); // before the build: the bundle must carry the corrected placeholder order
                PocBundleBuilder.Build(); // rebuilds the bundle + writes the artifact into <root>/Build
            }
            catch (Exception exception)
            {
                Debug.LogError($"[FalseGods] auto-build: bundle build FAILED: {exception.Message}");
                return;
            }

            var root = Directory.GetParent(Application.dataPath).FullName;
            var bundle = Path.Combine(root, "Build", PocBundleBuilder.BundleFileName);
            var artifact = Path.Combine(root, "Build", PocArenaContentExporter.ArtifactFileName);
            var targetsFile = Path.Combine(root, "LocalDeployTargets.txt");

            if (!File.Exists(targetsFile))
            {
                Debug.Log("[FalseGods] auto-build: bundle rebuilt. No LocalDeployTargets.txt at the project root, " +
                    "so it was not deployed. Add one absolute plugin-folder path per line to auto-deploy.");
                return;
            }

            var deployed = 0;
            foreach (var raw in File.ReadAllLines(targetsFile))
            {
                var dir = raw.Trim();
                if (dir.Length == 0 || dir.StartsWith("#", StringComparison.Ordinal))
                    continue;

                try
                {
                    Directory.CreateDirectory(dir);
                    File.Copy(bundle, Path.Combine(dir, Path.GetFileName(bundle)), overwrite: true);
                    File.Copy(artifact, Path.Combine(dir, Path.GetFileName(artifact)), overwrite: true);
                    deployed++;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"[FalseGods] auto-build: deploy to '{dir}' failed ({exception.Message}). " +
                        "If the game is running it may hold the bundle open — reload the level or close the game.");
                }
            }

            Debug.Log($"[FalseGods] auto-build: CaveShell.fbx -> bundle rebuilt + deployed to {deployed} target(s). " +
                "Reload the level in-game to see it.");
        }

        /// <summary>
        /// Re-align the shell's placeholder materials in the prefab with the sub-mesh order Unity actually
        /// produced for the freshly imported FBX.
        /// <para><b>Why this exists:</b> Unity orders an imported mesh's sub-meshes by the order faces first use
        /// each material, NOT by the authoring tool's slot list — so re-sculpting can permute the sub-mesh indices
        /// while the Blender slot order stays put. The runtime borrow matches each slot by the placeholder material
        /// it wears, so as long as this runs after every import, the paint lands on the right surfaces no matter
        /// how the indices moved. Slots are recognised by the meaningful part of the authored name (after the last
        /// underscore), so the numeric ordering prefixes may be renumbered freely.</para>
        /// </summary>
        [MenuItem("False Gods/Sync Cave Shell Materials")]
        public static void SyncShellMaterials()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(WatchedFbx);
            if (model == null)
            {
                Debug.LogWarning($"[FalseGods] material sync: no model at {WatchedFbx}; skipped.");
                return;
            }

            var modelRenderer = model.GetComponentInChildren<MeshRenderer>();
            if (modelRenderer == null)
            {
                Debug.LogWarning("[FalseGods] material sync: the imported model has no MeshRenderer; skipped.");
                return;
            }

            var imported = modelRenderer.sharedMaterials;
            var placeholders = new Material[imported.Length];
            var unknown = new List<string>();
            for (var i = 0; i < imported.Length; i++)
            {
                var slotName = imported[i] == null ? string.Empty : imported[i].name;
                var placeholder = PlaceholderFor(slotName);
                if (placeholder == null)
                {
                    unknown.Add($"{i}:'{slotName}'");
                    continue;
                }

                placeholders[i] = LoadOrCreatePlaceholder(placeholder);
            }

            if (unknown.Count > 0)
            {
                Debug.LogWarning($"[FalseGods] material sync: {unknown.Count} FBX slot(s) match no known surface " +
                    $"({string.Join(", ", unknown)}). Name them *_WallBot/_WallMid/_WallTop/_Floor/_Ceiling in " +
                    "Blender, or those sub-meshes will keep their placeholder in-game.");
            }

            var walkablePlaceholder = PlaceholderNameFor(WalkableSlot);
            var walkableIndex = Array.FindIndex(placeholders,
                p => p != null && string.Equals(p.name, walkablePlaceholder, StringComparison.Ordinal));

            var modelFilter = model.GetComponentInChildren<MeshFilter>();
            if (walkableIndex < 0 || modelFilter == null || modelFilter.sharedMesh == null)
            {
                Debug.LogWarning($"[FalseGods] material sync: no '{WalkableSlot}' slot in the model, so the walkable " +
                    "surface cannot be separated; the whole shell keeps whatever layer it has. Assign the cave's " +
                    "walkable faces to a *_Floor material slot in Blender.");
                ApplyToPrefab(placeholders);
                return;
            }

            var source = modelFilter.sharedMesh;
            var solidSubmeshes = new List<int>();
            var solidPlaceholders = new List<Material>();
            for (var i = 0; i < source.subMeshCount; i++)
            {
                if (i == walkableIndex)
                    continue;

                solidSubmeshes.Add(i);
                solidPlaceholders.Add(i < placeholders.Length ? placeholders[i] : null);
            }

            var solid = SaveMesh(BuildSubset(source, solidSubmeshes, "CaveShell_Solid"), ArenaFolder + "/CaveShell_Solid.asset");
            var walkable = SaveMesh(BuildSubset(source, new List<int> { walkableIndex }, "CaveShell_Walkable"),
                ArenaFolder + "/CaveShell_Walkable.asset");

            Debug.Log($"[FalseGods] material sync: split sub-mesh {walkableIndex} ('{WalkableSlot}') off as the " +
                $"walkable surface — {walkable.triangles.Length / 3} tris on layer {WalkableLayer}, " +
                $"{solid.triangles.Length / 3} tris of solid shell on layer {SolidLayer}.");

            ApplyToPrefab(solidPlaceholders.ToArray(), solid, walkable, placeholders[walkableIndex]);
        }

        /// <summary>
        /// A mesh carrying only the listed sub-meshes of <paramref name="source"/>, in the order given. Vertices are
        /// copied wholesale rather than re-indexed: the unreferenced ones cost a few hundred KB in a bundle that is
        /// already built per-import, and re-indexing is a chance to get the UVs wrong for no benefit anyone can see.
        /// </summary>
        private static Mesh BuildSubset(Mesh source, List<int> submeshes, string name)
        {
            var mesh = new Mesh { name = name, indexFormat = source.indexFormat };
            mesh.vertices = source.vertices;
            mesh.normals = source.normals;
            mesh.tangents = source.tangents;
            mesh.uv = source.uv;
            mesh.uv2 = source.uv2;
            mesh.colors = source.colors;
            mesh.subMeshCount = submeshes.Count;
            for (var i = 0; i < submeshes.Count; i++)
                mesh.SetTriangles(source.GetTriangles(submeshes[i]), i, calculateBounds: false);

            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Write a generated mesh to <paramref name="path"/>, overwriting the existing asset IN PLACE when
        /// there is one so its GUID survives — the prefab references these by GUID, and replacing the asset would
        /// leave the shell with a missing mesh on every re-import.</summary>
        private static Mesh SaveMesh(Mesh mesh, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(mesh, path);
                return mesh;
            }

            EditorUtility.CopySerialized(mesh, existing);
            EditorUtility.SetDirty(existing);
            UnityEngine.Object.DestroyImmediate(mesh); // the temporary, never the asset
            return existing;
        }

        private static string PlaceholderNameFor(string slot)
        {
            foreach (var (candidate, placeholder) in SlotPlaceholders)
            {
                if (string.Equals(candidate, slot, StringComparison.OrdinalIgnoreCase))
                    return placeholder;
            }

            return null;
        }

        /// <summary>The placeholder for an authored slot name, matched on the part after the last underscore so the
        /// numeric ordering prefix is free to change; null when the name matches no known surface.</summary>
        private static string PlaceholderFor(string slotName)
        {
            if (string.IsNullOrEmpty(slotName))
                return null;

            var separator = slotName.LastIndexOf('_');
            var meaningful = separator >= 0 && separator < slotName.Length - 1
                ? slotName.Substring(separator + 1)
                : slotName;

            foreach (var (slot, placeholder) in SlotPlaceholders)
            {
                if (string.Equals(slot, meaningful, StringComparison.OrdinalIgnoreCase))
                    return placeholder;
            }

            return null;
        }

        private static void ApplyToPrefab(Material[] placeholders)
            => EditPrefab(root => AssignShellMaterials(root, placeholders));

        private static void ApplyToPrefab(Material[] solidPlaceholders, Mesh solid, Mesh walkable, Material walkablePlaceholder)
            => EditPrefab(root => AssignShellSplit(root, solidPlaceholders, solid, walkable, walkablePlaceholder));

        /// <summary>Run an edit against the arena prefab and save it. Writes through an open prefab stage when there
        /// is one: the editor holds that copy in memory and would otherwise save it back over an asset-level
        /// edit.</summary>
        private static void EditPrefab(Func<GameObject, bool> edit)
        {
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && string.Equals(stage.assetPath, PrefabPath, StringComparison.Ordinal))
            {
                if (!edit(stage.prefabContentsRoot))
                    return;

                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(stage.scene);
                PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, stage.assetPath);
                AssetDatabase.SaveAssets();
                return;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogWarning($"[FalseGods] material sync: no prefab at {PrefabPath}; skipped.");
                return;
            }

            var instance = (GameObject)UnityEngine.Object.Instantiate(prefab);
            try
            {
                if (!edit(instance))
                    return;

                PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// Put the sculpt's walkable faces on their own object and layer, and the rest of it on a layer the
        /// navigation scan never looks at.
        /// <para>Which faces are walkable is an AUTHORED decision — the ones assigned to the Floor material slot —
        /// rather than whatever the recast scan decides is flat enough. Two things follow. The walls stop
        /// contributing their 0.5 m character-radius erosion along every surface they touch, which is what was
        /// eating the narrow terraces; and the outside of the shell stops generating navigation nobody can reach
        /// (measured: over half the graph's nodes sat on the cave's exterior roof).</para>
        /// </summary>
        private static bool AssignShellSplit(
            GameObject root, Material[] solidPlaceholders, Mesh solid, Mesh walkable, Material walkablePlaceholder)
        {
            var shell = root.transform.Find(ShellPath);
            if (shell == null)
            {
                Debug.LogWarning($"[FalseGods] material sync: the prefab has no {ShellPath}; skipped.");
                return false;
            }

            Dress(shell, solid, solidPlaceholders, SolidLayer);

            var walkableTransform = shell.parent.Find("CaveWalkable");
            if (walkableTransform == null)
            {
                var created = new GameObject("CaveWalkable");
                created.transform.SetParent(shell.parent, worldPositionStays: false);
                walkableTransform = created.transform;
            }

            // The two halves are one sculpt cut in two: they must sit on exactly the same transform.
            walkableTransform.localPosition = shell.localPosition;
            walkableTransform.localRotation = shell.localRotation;
            walkableTransform.localScale = shell.localScale;
            Dress(walkableTransform, walkable, new[] { walkablePlaceholder }, WalkableLayer);

            Debug.Log($"[FalseGods] material sync: {ShellPath} -> layer {SolidLayer} with " +
                string.Join(", ", solidPlaceholders.Select((m, i) => $"{i}:{(m == null ? "NULL" : m.name)}")) +
                $"; {WalkablePath} -> layer {WalkableLayer} with " +
                (walkablePlaceholder == null ? "NULL" : walkablePlaceholder.name) + ".");
            return true;
        }

        private static void Dress(Transform target, Mesh mesh, Material[] materials, int layer)
        {
            target.gameObject.layer = layer;

            // Not '??': a missing Unity component compares equal to null through the overloaded operator while
            // still being a real object reference, so the null-coalescing operator would hand back the missing one.
            var filter = target.GetComponent<MeshFilter>();
            if (filter == null)
                filter = target.gameObject.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;

            var renderer = target.GetComponent<MeshRenderer>();
            if (renderer == null)
                renderer = target.gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;

            var collider = target.GetComponent<MeshCollider>();
            if (collider == null)
                collider = target.gameObject.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
            collider.convex = false;
        }

        private static bool AssignShellMaterials(GameObject root, Material[] placeholders)
        {
            var shell = root.transform.Find(ShellPath);
            if (shell == null)
            {
                Debug.LogWarning($"[FalseGods] material sync: the prefab has no {ShellPath}; skipped.");
                return false;
            }

            var renderer = shell.GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                Debug.LogWarning($"[FalseGods] material sync: {ShellPath} has no MeshRenderer; skipped.");
                return false;
            }

            renderer.sharedMaterials = placeholders;
            Debug.Log("[FalseGods] material sync: shell placeholders now " +
                string.Join(", ", placeholders.Select((m, i) => $"{i}:{(m == null ? "NULL" : m.name)}")) + ".");
            return true;
        }

        /// <summary>Our own URP/Lit placeholder, created on first use. Its look does not matter — the runtime
        /// repaints every matched slot with the borrowed vanilla material — only its NAME does, since that is what
        /// the borrow matches on.</summary>
        private static Material LoadOrCreatePlaceholder(string name)
        {
            var path = $"{MaterialsFolder}/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
                return existing;

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogWarning("[FalseGods] material sync: URP/Lit not found; cannot create " + path);
                return null;
            }

            var material = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(material, path);
            Debug.Log($"[FalseGods] material sync: created placeholder {path}.");
            return material;
        }
    }
}
