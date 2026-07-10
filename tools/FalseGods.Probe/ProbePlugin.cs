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
    /// Read-only diagnostic plugin for PoC steps P0, P1 and P2. Applies no Harmony patches and mutates no
    /// authoritative game state.
    ///
    /// Timing is driven by the game's own canonical "navigation scan finished" event, not by polling for
    /// objects to appear. <c>AstarPath.active</c> exists early (GameManager.cs:1137), but the graph is not
    /// configured or scanned until the MakerGraph pipeline reaches <c>BuildNavMeshNode</c>, which sets the
    /// cell size, fills <c>NavMeshCleaner.validNavMeshPoints</c>, then calls <c>ScanAsync()</c>. Reading at
    /// "AstarPath exists" would capture default cell size, a null cleaner point set, zero scanned nodes, and
    /// possibly zero rooms. So the probe subscribes to the static <c>AstarPath.OnPostScan</c> that
    /// <c>BuildNavMeshNode</c> itself uses (BuildNavMeshNode.cs:65-75) — by the time it fires, the rooms are
    /// built, the graph is scanned, and the cleaner points are set. That is the game's own canonical
    /// scan-completion event, not a timing heuristic.
    ///
    /// The hotkey (F10) is the authoritative fallback: stand inside a loaded arena and press it. Its report
    /// is the one to trust, because you control exactly when it is taken.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class ProbePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "ryuka_labs.falsegods.probe";
        public const string PluginName = "False Gods Probe";
        public const string PluginVersion = "0.1.0";

        private ConfigEntry<bool> _runAfterEachScan;
        private ConfigEntry<Key> _hotkey;

        private OnScanDelegate _scanHandler;

        /// <summary>Set on the (main-thread) scan-complete callback; consumed in Update so the coroutine
        /// starts from a clean main-thread frame rather than inside A*'s callback.</summary>
        private volatile bool _scanCompletePending;

        private bool _running;

        private void Awake()
        {
            _runAfterEachScan = Config.Bind("Probe", "RunAfterEachScan", true,
                "Run the probe automatically after each navigation scan completes (AstarPath.OnPostScan). " +
                "This is the earliest point at which the level's rooms, graph and cleaner points are all ready.");

            _hotkey = Config.Bind("Probe", "Hotkey", Key.F10,
                "Run the probe on demand — the authoritative report, taken when you choose. " +
                "The game uses the new Input System; legacy UnityEngine.Input would throw.");

            // Subscribe to the static scan-complete delegate. It survives per-level AstarPath rebuilds
            // (the field is static), so one subscription covers every level; removed in OnDestroy.
            _scanHandler = OnNavigationScanComplete;
            AstarPath.OnPostScan = (OnScanDelegate)Delegate.Combine(AstarPath.OnPostScan, _scanHandler);

            Logger.LogMessage($"{PluginName} {PluginVersion} loaded. Read-only. " +
                              $"Auto-run after each nav scan: {_runAfterEachScan.Value}. Hotkey: {_hotkey.Value}.");
        }

        private void OnDestroy()
        {
            if (_scanHandler != null)
                AstarPath.OnPostScan = (OnScanDelegate)Delegate.Remove(AstarPath.OnPostScan, _scanHandler);
        }

        /// <summary>Called by A* on the main thread when a scan finishes (BuildNavMeshNode's ScanAsync path).</summary>
        private void OnNavigationScanComplete(AstarPath script) => _scanCompletePending = true;

        private void Update()
        {
            if (_running)
                return;

            if (HotkeyPressed())
            {
                _scanCompletePending = false;
                StartCoroutine(RunProbe("hotkey (authoritative — taken on demand)"));
                return;
            }

            if (_scanCompletePending)
            {
                _scanCompletePending = false;

                if (_runAfterEachScan.Value)
                    StartCoroutine(RunProbe("AstarPath.OnPostScan (navigation scan complete)"));
            }
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

            report.Line("False Gods — PoC probe P0/P1/P2");
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

            // P2 awaits AssetBundle loads — same coroutine + inner-guard structure as P1.
            yield return BundleProbe.Run(report);

            report.Line();
            report.Line(new string('═', 78));
            report.Line("End of probe. Transcribe results into:");
            report.Line("  Docs/CollisionAndNavigationProposal.md §4.4  (recast agent parameters)");
            report.Line("  Docs/RiskList.md  R1 / R2 / R3 / R5");

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
