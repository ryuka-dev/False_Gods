using System;
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

namespace FalseGods.EditorTools
{
    /// <summary>
    /// Rebuild the arena bundle and put it in every game profile that asked for it — the loop for dressing the
    /// room while the game is running.
    /// </summary>
    /// <remarks>
    /// <para>Content, unlike code, can be replaced under a running game: the plugin DLLs are held open, the bundle
    /// is not (except while a level built from it is actually loaded). So the iteration is: move a prop here,
    /// deploy, reload the level in game, look. Seconds rather than a restart.</para>
    /// <para><b>Deliberately not automatic.</b> Importing a sculpt is a discrete event worth reacting to; nudging
    /// a marker is not — an editor that rebuilt on every change would spend the session rebuilding. So this is a
    /// thing you ask for: double-click <c>Deploy Arena to Game</c> in the Project window, or use the menu.</para>
    /// <para>Targets come from <c>LocalDeployTargets.txt</c> at the Unity project root — one absolute plugin
    /// folder per line, git-ignored because they are one machine's paths. Both ends of a two-instance test are
    /// just two lines.</para>
    /// </remarks>
    public static class ArenaDeploy
    {
        /// <summary>The asset that runs this when double-clicked. Its contents say so, for whoever finds it
        /// first.</summary>
        public const string TriggerAssetPath = "Assets/FalseGods/Deploy Arena to Game.txt";

        private const string TargetsFileName = "LocalDeployTargets.txt";

        [MenuItem("False Gods/Deploy Arena to Game %#d")]
        public static void BuildAndDeploy()
        {
            string bundle;
            try
            {
                PocBundleBuilder.Build(); // rebuilds the bundle and writes the artifact beside it
                bundle = Path.Combine(ProjectRoot, "Build", PocBundleBuilder.BundleFileName);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[FalseGods] deploy: the bundle did not build, so nothing was copied: {exception.Message}");
                return;
            }

            Deploy(bundle, Path.Combine(ProjectRoot, "Build", PocArenaContentExporter.ArtifactFileName), "deploy");
        }

        /// <summary>Copy an already-built bundle and artifact to every configured profile. Reports what happened
        /// rather than failing silently — a deploy nobody noticed failing is a session spent testing the last
        /// build.</summary>
        public static void Deploy(string bundle, string artifact, string label)
        {
            var targetsFile = Path.Combine(ProjectRoot, TargetsFileName);
            if (!File.Exists(targetsFile))
            {
                Debug.Log($"[FalseGods] {label}: bundle rebuilt, but there is no {TargetsFileName} at the project "
                    + "root, so it went nowhere. Put one absolute plugin-folder path per line in it.");
                return;
            }

            var deployed = 0;
            var failed = 0;
            foreach (var raw in File.ReadAllLines(targetsFile))
            {
                var directory = raw.Trim();
                if (directory.Length == 0 || directory.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(directory);
                    File.Copy(bundle, Path.Combine(directory, Path.GetFileName(bundle)), overwrite: true);
                    File.Copy(artifact, Path.Combine(directory, Path.GetFileName(artifact)), overwrite: true);
                    deployed++;
                }
                catch (Exception exception)
                {
                    failed++;
                    Debug.LogWarning($"[FalseGods] {label}: '{directory}' did not take it ({exception.Message}). "
                        + "A game standing in the arena holds the bundle open — leave the level and try again.");
                }
            }

            var message = $"[FalseGods] {label}: arena deployed to {deployed} profile(s)"
                + (failed > 0 ? $", {failed} refused it" : string.Empty)
                + ". Reload the level in game to see it.";
            if (failed > 0)
            {
                Debug.LogWarning(message);
            }
            else
            {
                Debug.Log(message);
            }
        }

        /// <summary>Double-clicking the trigger asset deploys instead of opening it in a text editor.</summary>
        [OnOpenAsset(0)]
        private static bool OnOpenTriggerAsset(int instanceId, int line)
        {
            var opened = EditorUtility.InstanceIDToObject(instanceId);
            var path = opened == null ? null : AssetDatabase.GetAssetPath(opened);
            if (!string.Equals(path, TriggerAssetPath, StringComparison.OrdinalIgnoreCase))
            {
                return false; // not ours; let Unity open it normally
            }

            BuildAndDeploy();
            return true;
        }

        private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
    }
}
