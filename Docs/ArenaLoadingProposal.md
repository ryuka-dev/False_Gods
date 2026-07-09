# 2. Arena Loading Proposal

*How a custom fixed arena is loaded, and how the "proxy object in the editor → real vanilla asset at
runtime" flow works for SULFUR.* Builds on report 1.

## 2.1 The two integration strategies

### Strategy A — feed the arena into the game's `Room` / level-gen pipeline
Author the arena as a real `Room` prefab (or a `WorldEnvironment` + `MakerSet`) and let the existing pipeline
place it, build nav, and spawn.

- **Pros:** everything "just works" — `BuildNavMeshNode` scans it, `SpawnEnemiesNode`/`SpawnPlayerNode`
  run, and SULFUR Together's level/seed sync + `NetLevelManifest` already cover it because it's a normal
  level.
- **Cons:** requires authoring `Room`/`LevelBlock`/`WorldEnvironment` ScriptableObjects and (probably)
  registering an Addressables group that the game's generation graph will select. Heavy for a **single fixed
  arena**, and couples us to the generation graph's selection logic and `MakerSet` internals.

### Strategy B — standalone additive arena (recommended for the first boss) ✅
Load our own arena hierarchy directly (from our own AssetBundle / prefab), parent it under a known root, then
**manually invoke the same runtime steps the pipeline uses** (recast rescan, spawn). Enter it via a
custom trigger/warp rather than the normal level-transition selection.

- **Pros:** decoupled, no dependency on `MakerSet`/generation-graph selection, easy to reason about, easy to
  tear down. A fixed arena doesn't need procedural placement.
- **Cons:** we replicate a few steps the pipeline did for free — the recast rescan (`NavMeshManager` /
  `BuildNavMeshNode` equivalent), player/boss placement, and the multiplayer "everyone loaded the same
  arena" handshake (report 5). All are small and well-understood from the decompile.

**Recommendation:** **Strategy B** for the first fixed boss arena. Reassess Strategy A only when moving to
procedural/random arenas, where the pipeline's placement logic earns its integration cost.

## 2.2 The proxy → runtime-asset flow (is it viable for SULFUR? Yes)

Because vanilla content is **Addressables** (report 1.2), the editor project never has to contain vanilla
assets. The flow:

**Editor (our own Unity project, matching the game's Unity version):**
1. Build the arena layout using **lightweight proxy objects** — e.g. an empty/gizmo or a cheap placeholder
   mesh — each carrying a small component, say `VanillaAssetProxy { string addressableKeyOrGuid; }`, plus
   its transform (position/rotation/scale).
2. Author our own **`CollisionRoot`** (simple box/plane colliders) and **`NavigationRoot`** hints
   (report 4). These are the pieces the proxies do *not* provide.
3. Pack the arena layout (proxies + our collision/nav + our own meshes/lights) into our mod's own
   AssetBundle. **No vanilla assets are included.**

**Runtime (in-game, BepInEx plugin):**
1. Load our arena layout bundle and instantiate the layout root.
2. For each `VanillaAssetProxy`, resolve the real vanilla asset via the game's own API —
   `new AssetReference(guid).LoadAssetAsync<GameObject>()` or `Addressables.LoadAssetAsync<GameObject>(key)`
   (mirroring `LevelGenGraphUtilities`). Cache/refcount handles; `Addressables.Release` on teardown.
3. `Instantiate` the real vanilla prefab, apply the proxy's transform, parent under `VisualRoot`, then
   destroy/disable the proxy.
4. The instantiated vanilla object keeps its **real materials, shaders, and Renderer setup** — this is why
   the material-loss problems from raw mesh extraction are avoided (report 3).

### Two levels of "vanilla reuse"
- **Whole vanilla `Room` prefab** as backdrop — simplest, most robust (materials guaranteed wired). Strip or
  ignore its spawns/connectors.
- **Individual vanilla props** (pillar, rock, wood support) — if a prop is separately addressable, resolve it
  directly; otherwise resolve its parent room once and pluck the named child at runtime.

## 2.3 Proposed runtime ownership & lifecycle (Strategy B)

A single mod-owned controller, e.g. `ArenaController` (project-owned, no vanilla base type), owns the arena's
whole lifecycle:

```
Enter:  gate/trigger fires
  → ArenaController.LoadAsync(arenaId, arenaVersion)
      1. instantiate our layout (VisualRoot / CollisionRoot / NavigationRoot / GameplayRoot / LightingRoot)
      2. resolve + instantiate all VanillaAssetProxy targets (Addressables)  [await all]
      3. rebuild A* recast over the arena  (report 4)
      4. place player(s) at PlayerSpawn; (host) spawn boss at BossSpawn
      5. signal "arena ready"  (single-player: immediate; multiplayer: ready-gate, report 5)
Exit:   boss dead / player leaves
  → ArenaController.Unload()
      release Addressables handles, destroy roots, restore/rescan nav, clear GameplayRoot state
```

Lifecycle hooks should attach to **real events** (the gate/trigger that starts the encounter, the boss death
event) rather than scene-name or timing heuristics (CLAUDE.md §6). SULFUR Together already patches the
relevant level-transition methods (`SwitchLevelRoutine`, `CompleteLevel`) and boss triggers, so the arena
entry can be driven from a genuine game event.

## 2.4 What still needs verification (feeds the PoC)
- Whether our own AssetBundle built in the game's Unity version loads cleanly under BepInEx (Unity version
  match, shader stripping) — report 3, RiskList.
- Exact Addressables key/GUID stability across game updates — RiskList R1.
- Whether resolving a vanilla prop out of a room prefab (vs a top-level addressable) is needed for the props
  we want.
