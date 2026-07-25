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
        private const string ShellPath = "VisualRoot/CaveShell";
        private const string MaterialsFolder = "Assets/FalseGods/Materials";

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

            ApplyToPrefab(placeholders);
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

        /// <summary>Write the placeholder array onto the prefab's shell renderer. Writes through an open prefab
        /// stage when there is one: the editor holds that copy in memory and would otherwise save it back over an
        /// asset-level edit.</summary>
        private static void ApplyToPrefab(Material[] placeholders)
        {
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && string.Equals(stage.assetPath, PrefabPath, StringComparison.Ordinal))
            {
                if (!AssignShellMaterials(stage.prefabContentsRoot, placeholders))
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
                if (!AssignShellMaterials(instance, placeholders))
                    return;

                PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
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
