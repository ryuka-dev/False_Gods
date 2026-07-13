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
        public const string PluginVersion = "0.24.2";

        private ConfigEntry<bool> _runAfterEachScan;
        private ConfigEntry<Key> _hotkey;
        private ConfigEntry<Key> _visualHotkey;
        private ConfigEntry<Key> _collisionHotkey;
        private ConfigEntry<Key> _navHotkey;
        private ConfigEntry<Key> _navPrefabHotkey;
        private ConfigEntry<Key> _navBakeHotkey;
        private ConfigEntry<Key> _navApplyHotkey;
        private ConfigEntry<Key> _navEnemyHotkey;
        private ConfigEntry<Key> _navTeardownHotkey;
        private ConfigEntry<bool> _visualApplyEnvironment;
        private ConfigEntry<bool> _visualFixOurMaterials;
        private ConfigEntry<string> _enemyUnitId;

        private readonly VisualProbe _visual = new VisualProbe();

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
                "Run the read-only P0/P1/P2 probe on demand — the authoritative report, taken when you " +
                "choose. The game uses the new Input System; legacy UnityEngine.Input would throw.");

            _visualHotkey = Config.Bind("Probe", "VisualHotkey", Key.F11,
                "P3 visible render check: press once to raise a visible stage (our room + a vanilla prefab " +
                "under our LightingRoot, with a vanilla floor material on our ground mesh), press again to " +
                "tear it down. Unlike P0/P1/P2 this shows real objects — judge pink/no-pink with your eyes.");

            _visualApplyEnvironment = Config.Bind("Probe", "VisualApplyEnvironment", true,
                "During the P3 stage, also apply basic ambient/fog to the global RenderSettings (scene state " +
                "a prefab cannot carry). Always restored on teardown. Turn off to leave the level's own " +
                "environment untouched and judge the lights alone.");

            _visualFixOurMaterials = Config.Bind("Probe", "VisualFixOurMaterials", true,
                "During the P3 stage, dress our own room meshes (floor + pillar) with a borrowed vanilla " +
                "material — the working fix for our bundle's pink URP/Lit materials (the game has no resident " +
                "URP/Lit to adopt; originals need a ShaderVariantCollection). Turn OFF to see the raw pink.");

            _collisionHotkey = Config.Bind("Probe", "CollisionHotkey", Key.F9,
                "P4 collision check: press once to place our sealed arena around you (its PlayerSpawn marker " +
                "under your feet) so you can walk it on foot — stand on the floor, hit the pillar, test the " +
                "walls — press again to remove it. The room's four walls seal it by design, which is why the " +
                "P3 stage could only be entered with F3; P4 puts you inside without teleporting the player. " +
                "Not F12 — that is the Steam/Windows screenshot key. Rebind here if F9 also conflicts.");

            _navHotkey = Config.Bind("Probe", "NavHotkey", Key.F8,
                "P5 A* nav check: spawns our room as an isolated island a few metres up and re-bakes nav the " +
                "way the game does — SnapBoundsToScene() FIRST, then Scan() (the earlier runs skipped the " +
                "bounds refit, so our floor sat outside the graph and never rasterized). Two phases: rebake " +
                "with no anchor (R4: floor should now rasterize; R5: NavMeshCleaner should ERASE it), then with " +
                "a validNavMeshPoint on it (should SURVIVE, walkable). WARNING: re-bakes the WHOLE level's nav " +
                "three times. It only appends a cleaner point and restores it, and a level change rebuilds nav " +
                "— but run it in a throwaway level; NPCs will re-path while it re-bakes.");

            _navPrefabHotkey = Config.Bind("Probe", "NavPrefabHotkey", Key.F7,
                "P5b Option-1 mechanism check: spawns our room as an isolated island, runs the game's own " +
                "NavmeshPrefab.Scan() to bake a localized navmesh over just the arena, then replicates " +
                "NavmeshPrefab.Apply() (SnapToGraph + ReplaceTiles) to insert those tiles into the live graph — " +
                "the Option-1 path, run at runtime with no editor bake. Reports whether the bake produced " +
                "geometry (R4 rasterization) and whether a walkable node lands on our floor (Apply from a mod). " +
                "Adds tiles then ClearTiles-es them; a level change rebuilds nav. Run it in a throwaway level.");

            _navBakeHotkey = Config.Bind("Probe", "NavBakeHotkey", Key.F6,
                "P5c bake & capture: spawns our room high above the level (clear of all geometry) and runs the " +
                "game's NavmeshPrefab.Scan() to bake a clean navmesh of just our arena, then writes the " +
                "serialized bytes to arena-nav-PocRoom-cell<size>.bytes next to the reports. This is the " +
                "shippable Option-1 artifact (baked once, applied by every peer). Read-only: it never touches " +
                "the live graph. Run once per distinct environment cellSize (0.1, 0.3).");

            _navApplyHotkey = Config.Bind("Probe", "NavApplyHotkey", Key.F5,
                "P5d apply shipped artifact: reads arena-nav-PocRoom-cell<size>.bytes (baked by F6) for this " +
                "level's cellSize, spawns the arena, and applies the SAVED bytes via TileMeshes.Deserialize + " +
                "SnapToGraph + ReplaceTiles — the exact runtime path — then measures walkable floor nodes before " +
                "vs after to prove the shipped artifact makes OUR floor walkable. Adds tiles then ClearTiles-es " +
                "them. Run in a level whose cellSize matches a baked artifact.");

            _navEnemyHotkey = Config.Bind("Probe", "NavEnemyHotkey", Key.F4,
                "P6 A* pathing check: spawns our arena as an isolated island, bakes + applies its navmesh " +
                "(P5c+P5d in memory), then proves pathing on OUR geometry two ways: (1) an ABPath between the " +
                "EnemySpawn and PlayerSpawn corners whose straight line crosses the central pillar must ROUTE " +
                "AROUND it (read the report), and (2) a real vanilla enemy (EnemyUnitId) is spawned, activated " +
                "and driven to the far corner past the pillar. WARNING: mutates AstarPath.active (adds tiles + " +
                "one NPC) then removes exactly that; a level change rebuilds nav. Run in a throwaway level.");

            _navTeardownHotkey = Config.Bind("Probe", "NavTeardownHotkey", Key.Equals,
                "P7 teardown check: spawns our arena as an isolated island, snapshots the level's own nav tiles " +
                "in the arena footprint, applies our baked arena nav over them (which clobbers that level nav), " +
                "then RESTORES the snapshot and destroys the arena. Measures whole-graph + footprint walkable " +
                "node counts at BASELINE / APPLIED / RESTORED to prove the level we stay in returns to baseline " +
                "(no arena objects, no arena nav nodes, level nav intact — R8/R30). WARNING: mutates " +
                "AstarPath.active (replaces then restores the footprint tiles); a level change rebuilds nav. " +
                "Run in a throwaway level, standing on solid level nav. Bound to '=' because F1-F3 are the game's " +
                "debug keys and F4-F12 are taken; the number row (1..=) is free. Rebind here if '=' conflicts.");

            _enemyUnitId = Config.Bind("Probe", "EnemyUnitId", "HellshrewSticka",
                "P6 live-enemy: the UnitIds field name of the vanilla enemy to spawn (resolved by reflection, " +
                "loaded via Addressables). Pick a normal grounded melee enemy. Change here to try another if " +
                "one misbehaves. The nav-graph proof (layer 1) does not depend on this.");

            // Subscribe to the static scan-complete delegate. It survives per-level AstarPath rebuilds
            // (the field is static), so one subscription covers every level; removed in OnDestroy.
            _scanHandler = OnNavigationScanComplete;
            AstarPath.OnPostScan = (OnScanDelegate)Delegate.Combine(AstarPath.OnPostScan, _scanHandler);

            Logger.LogMessage($"{PluginName} {PluginVersion} loaded. " +
                              $"Auto-run after each nav scan: {_runAfterEachScan.Value}. " +
                              $"P0/P1/P2 hotkey: {_hotkey.Value}. P3 visible hotkey: {_visualHotkey.Value}. " +
                              $"P4 collision hotkey: {_collisionHotkey.Value}. P5 nav hotkey: {_navHotkey.Value}. " +
                              $"P5b nav-prefab hotkey: {_navPrefabHotkey.Value}. " +
                              $"P5c bake hotkey: {_navBakeHotkey.Value}. " +
                              $"P5d apply hotkey: {_navApplyHotkey.Value}. " +
                              $"P6 enemy hotkey: {_navEnemyHotkey.Value} (enemy: {_enemyUnitId.Value}). " +
                              $"P7 teardown hotkey: {_navTeardownHotkey.Value}.");
        }

        private void OnDestroy()
        {
            if (_scanHandler != null)
                AstarPath.OnPostScan = (OnScanDelegate)Delegate.Remove(AstarPath.OnPostScan, _scanHandler);

            // Never leave the P3 stage (or its RenderSettings change) behind if the plugin unloads while up.
            if (_visual.IsUp)
                _visual.Drop(new ProbeReport(Logger));
        }

        /// <summary>Called by A* on the main thread when a scan finishes (BuildNavMeshNode's ScanAsync path).</summary>
        private void OnNavigationScanComplete(AstarPath script) => _scanCompletePending = true;

        private void Update()
        {
            if (_running)
                return;

            if (HotkeyPressed(_hotkey.Value))
            {
                _scanCompletePending = false;
                StartCoroutine(RunProbe("hotkey (authoritative — taken on demand)"));
                return;
            }

            if (HotkeyPressed(_visualHotkey.Value))
            {
                StartCoroutine(RunVisualToggle(VisualProbe.StageMode.Render));
                return;
            }

            if (HotkeyPressed(_collisionHotkey.Value))
            {
                StartCoroutine(RunVisualToggle(VisualProbe.StageMode.Collision));
                return;
            }

            if (HotkeyPressed(_navHotkey.Value))
            {
                StartCoroutine(RunNav());
                return;
            }

            if (HotkeyPressed(_navPrefabHotkey.Value))
            {
                StartCoroutine(RunNavPrefab());
                return;
            }

            if (HotkeyPressed(_navBakeHotkey.Value))
            {
                StartCoroutine(RunNavBake());
                return;
            }

            if (HotkeyPressed(_navApplyHotkey.Value))
            {
                StartCoroutine(RunNavApply());
                return;
            }

            if (HotkeyPressed(_navEnemyHotkey.Value))
            {
                StartCoroutine(RunNavEnemy());
                return;
            }

            if (HotkeyPressed(_navTeardownHotkey.Value))
            {
                StartCoroutine(RunNavTeardown());
                return;
            }

            if (_scanCompletePending)
            {
                _scanCompletePending = false;

                if (_runAfterEachScan.Value)
                    StartCoroutine(RunProbe("AstarPath.OnPostScan (navigation scan complete)"));
            }
        }

        private static bool HotkeyPressed(Key key)
        {
            try
            {
                var keyboard = Keyboard.current;
                return keyboard != null && keyboard[key].wasPressedThisFrame;
            }
            catch (Exception)
            {
                // No keyboard device, or an unmapped key. Auto-run still works.
                return false;
            }
        }

        /// <summary>
        /// Toggle the visible stage — P3 render (F11) or P4 collision (F12). Raising awaits bundle +
        /// Addressables loads, so it is a coroutine; teardown is immediate. One stage is up at a time, so
        /// either key while a stage is up tears it down. Shares the _running guard with the P0/P1/P2 probe so
        /// the two never run at once. Each action writes its own report.
        /// </summary>
        private IEnumerator RunVisualToggle(VisualProbe.StageMode mode)
        {
            _running = true;

            var label = mode == VisualProbe.StageMode.Render ? "P3 (visible render check)" : "P4 (collision check)";
            var report = new ProbeReport(Logger);
            report.Line($"False Gods — PoC probe {label}");
            report.Line($"utc:     {DateTime.UtcNow:O}");
            report.Line(new string('═', 78));

            if (_visual.IsUp)
            {
                _visual.Drop(report);
            }
            else
            {
                report.Line(mode == VisualProbe.StageMode.Render
                    ? "Raising the P3 stage in front of the camera. Look, then press the key again to drop it."
                    : "Placing the P4 arena around you. Walk it, then press the key again to remove it.");
                yield return _visual.Raise(report, mode, _visualApplyEnvironment.Value, _visualFixOurMaterials.Value);
            }

            try
            {
                var path = report.WriteToDisk();
                Logger.LogMessage($"{label} {(_visual.IsUp ? "raised" : "dropped")}. Report: {path}");
            }
            catch (Exception exception)
            {
                Logger.LogError($"Could not write {label} report: {exception}");
            }

            _running = false;
        }

        /// <summary>
        /// P5: run the A* nav check. Self-contained (spawns its own isolated island, mutates and then restores
        /// AstarPath.active), so a fresh <see cref="NavmeshProbe"/> is used each time. Shares the _running
        /// guard with the other probes so nothing overlaps a graph update.
        /// </summary>
        private IEnumerator RunNav()
        {
            _running = true;

            var report = new ProbeReport(Logger);
            report.Line("False Gods — PoC probe P5 (A* nav check)");
            report.Line($"utc:     {DateTime.UtcNow:O}");
            report.Line(new string('═', 78));

            yield return new NavmeshProbe().Run(report);

            // P5's own ScanAsync calls fired AstarPath.OnPostScan; clear the flag so they do not also kick off
            // an automatic P0/P1/P2 run once the guard drops.
            _scanCompletePending = false;

            try
            {
                var path = report.WriteToDisk();
                Logger.LogMessage($"P5 nav check done. Report: {path}");
            }
            catch (Exception exception)
            {
                Logger.LogError($"Could not write P5 report: {exception}");
            }

            _running = false;
        }

        /// <summary>
        /// P5b: run the NavmeshPrefab Scan+Apply mechanism check (Option 1, from a mod, no editor bake). Like
        /// P5 it is self-contained — spawns its own island, mutates and then restores AstarPath.active — so a
        /// fresh <see cref="NavmeshPrefabProbe"/> is used each time. Shares the _running guard.
        /// </summary>
        private IEnumerator RunNavPrefab()
        {
            _running = true;

            var report = new ProbeReport(Logger);
            report.Line("False Gods — PoC probe P5b (NavmeshPrefab Scan+Apply)");
            report.Line($"utc:     {DateTime.UtcNow:O}");
            report.Line(new string('═', 78));

            yield return new NavmeshPrefabProbe().Run(report);

            // The localized Scan does not fire AstarPath.OnPostScan, but clear the flag defensively so nothing
            // kicks off an automatic P0/P1/P2 run once the guard drops.
            _scanCompletePending = false;

            try
            {
                var path = report.WriteToDisk();
                Logger.LogMessage($"P5b nav-prefab check done. Report: {path}");
            }
            catch (Exception exception)
            {
                Logger.LogError($"Could not write P5b report: {exception}");
            }

            _running = false;
        }

        /// <summary>
        /// P5c: bake our arena's navmesh in-game and write the shippable bytes to disk. Self-contained and
        /// read-only w.r.t. the live graph (Scan builds a separate TileBuilder), so a fresh
        /// <see cref="NavmeshBakeProbe"/> is used each time. Shares the _running guard.
        /// </summary>
        private IEnumerator RunNavBake()
        {
            _running = true;

            var report = new ProbeReport(Logger);
            report.Line("False Gods — PoC probe P5c (bake & capture arena navmesh)");
            report.Line($"utc:     {DateTime.UtcNow:O}");
            report.Line(new string('═', 78));

            yield return new NavmeshBakeProbe().Run(report);

            _scanCompletePending = false;

            try
            {
                var path = report.WriteToDisk();
                Logger.LogMessage($"P5c bake done. Report: {path}");
            }
            catch (Exception exception)
            {
                Logger.LogError($"Could not write P5c report: {exception}");
            }

            _running = false;
        }

        /// <summary>
        /// P5d: apply a SAVED arena navmesh artifact (the ship->apply half of Option 1). Self-contained and
        /// restores the graph, so a fresh <see cref="NavmeshApplyProbe"/> is used each time. Shares the _running
        /// guard.
        /// </summary>
        private IEnumerator RunNavApply()
        {
            _running = true;

            var report = new ProbeReport(Logger);
            report.Line("False Gods — PoC probe P5d (apply shipped arena navmesh)");
            report.Line($"utc:     {DateTime.UtcNow:O}");
            report.Line(new string('═', 78));

            yield return new NavmeshApplyProbe().Run(report);

            _scanCompletePending = false;

            try
            {
                var path = report.WriteToDisk();
                Logger.LogMessage($"P5d apply done. Report: {path}");
            }
            catch (Exception exception)
            {
                Logger.LogError($"Could not write P5d report: {exception}");
            }

            _running = false;
        }

        /// <summary>
        /// P6: prove A* pathing on our applied arena — an ABPath routes around the pillar and a live vanilla
        /// enemy follows such a path. Self-contained (spawns its own island + one NPC, mutates then restores
        /// AstarPath.active), so a fresh <see cref="NavmeshEnemyProbe"/> is used each time. Shares the _running
        /// guard so nothing overlaps a graph update.
        /// </summary>
        private IEnumerator RunNavEnemy()
        {
            _running = true;

            var report = new ProbeReport(Logger);
            report.Line("False Gods — PoC probe P6 (A* pathing: route around pillar + live enemy)");
            report.Line($"utc:     {DateTime.UtcNow:O}");
            report.Line(new string('═', 78));

            yield return new NavmeshEnemyProbe(_enemyUnitId.Value).Run(report);

            _scanCompletePending = false;

            try
            {
                var path = report.WriteToDisk();
                Logger.LogMessage($"P6 nav-enemy check done. Report: {path}");
            }
            catch (Exception exception)
            {
                Logger.LogError($"Could not write P6 report: {exception}");
            }

            _running = false;
        }

        /// <summary>
        /// P7: prove teardown leaves the level we stay in clean — snapshot the level's footprint tiles, apply our
        /// arena nav over them, then restore the snapshot and assert the level returns to baseline (R8/R30).
        /// Self-contained (spawns its own island, replaces then restores AstarPath.active tiles), so a fresh
        /// <see cref="NavmeshTeardownProbe"/> is used each time. Shares the _running guard.
        /// </summary>
        private IEnumerator RunNavTeardown()
        {
            _running = true;

            var report = new ProbeReport(Logger);
            report.Line("False Gods — PoC probe P7 (teardown: apply arena nav, restore level to baseline)");
            report.Line($"utc:     {DateTime.UtcNow:O}");
            report.Line(new string('═', 78));

            yield return new NavmeshTeardownProbe().Run(report);

            _scanCompletePending = false;

            try
            {
                var path = report.WriteToDisk();
                Logger.LogMessage($"P7 teardown check done. Report: {path}");
            }
            catch (Exception exception)
            {
                Logger.LogError($"Could not write P7 report: {exception}");
            }

            _running = false;
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
