using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FalseGods.EditorTools
{
    /// <summary>
    /// Builds the PoC room AssetBundle for PoC step P2 (RiskList R2). The bundle is built for
    /// StandaloneWindows64 with this project's editor (which ProjectVersion.txt pins to the game's exact
    /// Unity version, 6000.3.6f1) and lands in Build/ at the Unity project root — gitignored, because the
    /// bundle is a build artefact the generator + builder can always reproduce.
    ///
    /// Explicit AssetBundleBuild entries are used instead of AssetImporter bundle tags: what goes into the
    /// bundle should be readable here, in code, not spread over importer metadata.
    /// </summary>
    public static class PocBundleBuilder
    {
        /// <summary>Must match what the runtime probe looks for (tools/FalseGods.Probe/BundleProbe.cs).</summary>
        public const string BundleFileName = "falsegods-poc-room.bundle";

        private const string OutputDirectory = "Build";

        [MenuItem("False Gods/Build PoC AssetBundle")]
        public static void Build()
        {
            var path = BuildInternal();
            Debug.Log($"[FalseGods] PoC bundle written to {path}.");
        }

        /// <summary>
        /// Headless entry point:
        ///   Unity.exe -batchmode -nographics -projectPath FalseGods.Unity
        ///     -executeMethod FalseGods.EditorTools.PocBundleBuilder.BuildFromBatchMode -logFile …
        /// Exits the editor with an explicit process exit code (0 success / 1 failure) — do not pass -quit,
        /// and trust the exit code rather than scraping the log.
        /// </summary>
        public static void BuildFromBatchMode()
        {
            try
            {
                var path = BuildInternal();
                Debug.Log($"[FalseGods] Batch build OK: {path}");
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[FalseGods] Batch build FAILED: {exception}");
                EditorApplication.Exit(1);
            }
        }

        private static string BuildInternal()
        {
            // The arena prefab is hand-authored (floor/ceiling/collision + the sculpted CaveShell wall + rocks),
            // so the build packs whatever is currently saved in the prefab — it does NOT regenerate. Save the
            // prefab before building; the artifact is exported from the same saved prefab below, so the two cannot
            // diverge (R14). PocRoomGenerator is now only a one-off scaffold for a fresh box, not the build source.
            Directory.CreateDirectory(OutputDirectory);

            var builds = new[]
            {
                new AssetBundleBuild
                {
                    assetBundleName = BundleFileName,
                    assetNames = new[] { PocRoomGenerator.PrefabPath },
                },
            };

            var manifest = BuildPipeline.BuildAssetBundles(
                OutputDirectory,
                builds,
                BuildAssetBundleOptions.ChunkBasedCompression, // LZ4: cheap random-access loads at runtime
                BuildTarget.StandaloneWindows64);

            if (manifest == null)
                throw new InvalidOperationException("BuildPipeline.BuildAssetBundles returned null.");

            var bundlePath = Path.Combine(OutputDirectory, BundleFileName);
            if (!File.Exists(bundlePath))
                throw new InvalidOperationException($"Build reported success but '{bundlePath}' does not exist.");

            // Ship the authored content artifact alongside the bundle (P8.1): the runtime reads it to recompute
            // the ContentHash (R34) and to check hierarchy parity (R14). The room was just generated above, so
            // write from the current prefab rather than regenerating a second time.
            var artifactPath = PocArenaContentExporter.WriteArtifactForCurrentPrefab();
            Debug.Log($"[FalseGods] Arena content artifact written to {artifactPath}.");

            return Path.GetFullPath(bundlePath);
        }
    }
}
