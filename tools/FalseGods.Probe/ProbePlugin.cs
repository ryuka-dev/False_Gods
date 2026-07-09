using System;
using System.Collections;
using BepInEx;
using BepInEx.Configuration;
using Pathfinding;
using PerfectRandom.Sulfur.Core;
using UnityEngine.InputSystem;

namespace FalseGods.Probe
{
    /// <summary>
    /// Read-only diagnostic plugin for PoC steps P0 and P1. Applies no Harmony patches and mutates no game
    /// state; it reads values and writes a report.
    ///
    /// It runs automatically once per <c>AstarPath</c> instance. That is not a timing heuristic dressed up
    /// as an event — a fresh <c>AstarPath</c> IS the level's graph: <c>GameManager</c> destroys the old one
    /// and instantiates <c>astarPathPrefab</c> on every level change (GameManager.cs:1097 / :1137). So one
    /// run per instance is exactly one run per level, which is what we want to capture.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class ProbePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "ryuka_labs.falsegods.probe";
        public const string PluginName = "False Gods Probe";
        public const string PluginVersion = "0.1.0";

        private ConfigEntry<bool> _runOnEachLevel;
        private ConfigEntry<Key> _hotkey;

        /// <summary>Identifies the graph we last reported on, so a new level triggers a new report.</summary>
        private int _reportedAstarInstanceId;

        private bool _running;

        private void Awake()
        {
            _runOnEachLevel = Config.Bind("Probe", "RunOnEachLevel", true,
                "Run the probe automatically once per level, when the level's AstarPath graph appears.");

            _hotkey = Config.Bind("Probe", "Hotkey", Key.F10,
                "Re-run the probe on demand. The game uses the new Input System; legacy UnityEngine.Input would throw.");

            Logger.LogMessage($"{PluginName} {PluginVersion} loaded. Read-only. " +
                              $"Auto-run per level: {_runOnEachLevel.Value}. Hotkey: {_hotkey.Value}.");
        }

        private void Update()
        {
            if (_running)
                return;

            if (HotkeyPressed())
            {
                StartCoroutine(RunProbe("hotkey"));
                return;
            }

            if (!_runOnEachLevel.Value)
                return;

            var astar = AstarPath.active;
            if (astar == null || GameManager.Instance == null)
                return;

            var instanceId = astar.GetInstanceID();
            if (instanceId == _reportedAstarInstanceId)
                return;

            _reportedAstarInstanceId = instanceId;
            StartCoroutine(RunProbe("new AstarPath instance (level loaded)"));
        }

        private bool HotkeyPressed()
        {
            try
            {
                var keyboard = Keyboard.current;
                return keyboard != null && keyboard[_hotkey.Value].wasPressedThisFrame;
            }
            catch (Exception)
            {
                // No keyboard device, or an unmapped key. Auto-run still works.
                return false;
            }
        }

        private IEnumerator RunProbe(string trigger)
        {
            _running = true;

            var report = new ProbeReport(Logger);

            report.Line("False Gods — PoC probe P0/P1");
            report.Line($"trigger: {trigger}");
            report.Line($"utc:     {DateTime.UtcNow:O}");
            report.Line(new string('═', 78));

            // P0 is synchronous: everything it reads is already in memory.
            try
            {
                NavigationProbe.Run(report);
            }
            catch (Exception exception)
            {
                report.Failure("NavigationProbe (P0)", exception);
            }

            // P1 must await Addressables handles, so it is a coroutine. A `try` cannot wrap a `yield`,
            // hence the guard inside AddressablesProbe rather than around it.
            yield return AddressablesProbe.Run(report);

            report.Line();
            report.Line(new string('═', 78));
            report.Line("End of probe. Transcribe results into:");
            report.Line("  Docs/CollisionAndNavigationProposal.md §4.4  (recast agent parameters)");
            report.Line("  Docs/RiskList.md  R1 / R3 / R5");

            string path = null;
            try
            {
                path = report.WriteToDisk();
            }
            catch (Exception exception)
            {
                Logger.LogError($"Could not write probe report: {exception}");
            }

            if (path != null)
                Logger.LogMessage($"Probe complete ({trigger}). Report: {path}");

            _running = false;
        }
    }
}
