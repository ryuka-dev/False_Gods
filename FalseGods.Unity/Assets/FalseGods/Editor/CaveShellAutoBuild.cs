using System;
using System.IO;
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
    }
}
