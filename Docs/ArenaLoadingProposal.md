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

Strategy B remains the recommendation for the first boss, but "standalone additive arena" means a fully
Unity-authored False Gods prefab, not a code-generated arrangement of placeholder transforms.

## 2.2 Authoring model: Unity prefab first

The proxy system is not the arena-layout format by itself.

The fixed arena is authored as a complete Unity prefab in the False Gods Unity project. The prefab contains
the real hierarchy, transforms, original geometry, original materials, collision, lights, gameplay markers,
phase objects, and editor-only debug helpers.

`VanillaAssetProxy` is only used at positions where the arena intentionally reuses content from the player's
local SULFUR installation.

Therefore the authored prefab can be previewed and iterated visually without reconstructing the arena from
hard-coded transform lists at runtime.

The recommended prefab hierarchy (authored in Unity, see [OriginalContentPipeline.md](OriginalContentPipeline.md)):

```text
ArenaRoot
├─ VisualRoot
│  ├─ OriginalGeometry
│  ├─ OriginalProps
│  ├─ OriginalBossSetPieces
│  └─ VanillaAssetProxies
├─ CollisionRoot
├─ NavigationRoot
├─ LightingRoot
├─ GameplayRoot
│  ├─ PlayerSpawns
│  ├─ BossSpawn
│  ├─ ArenaBoundary
│  ├─ ArenaMechanisms
│  ├─ PhaseObjects
│  └─ Exit
└─ DebugRoot
```

## 2.3 The proxy → runtime-asset flow (is it viable for SULFUR? Yes)

Because vanilla content is **Addressables** (report 1.2), the editor project never has to contain vanilla
assets. The flow:

**Editor:**

1. Author the complete fixed arena as a Unity prefab.
2. Place original False Gods meshes, materials, lights, colliders, markers, and phase objects directly.
3. Where vanilla SULFUR art is desired, place a `VanillaAssetProxy`.
4. Give the proxy an editor preview:
   - a simplified placeholder;
   - an extracted local-only preview mesh;
   - or an editor tool that previews the referenced asset.
5. Build the arena prefab and original dependencies into a False Gods AssetBundle.
6. Never include redistributed vanilla SULFUR assets in the published bundle.

**Runtime:**

1. Load and instantiate the complete False Gods arena prefab.
2. Preserve all original False Gods geometry/materials/lights/components already contained in it.
3. Resolve only the `VanillaAssetProxy` objects through the player's local Addressables catalog.
4. Replace those proxies with live vanilla prefabs while preserving authored transforms.
5. Register collision/navigation/gameplay roots.
6. Report `ArenaReady` with the arena identity and content hash, and wait for the gate.
7. Only after the gate passes: seal, teleport, publish the `EncounterBaseline`, start the encounter (§2.4).

The instantiated vanilla objects keep their **real materials, shaders, and Renderer setup** — this is why the
material-loss problems from raw mesh extraction are avoided (report 3). Resolution mirrors the game's own API:
`new AssetReference(guid).LoadAssetAsync<GameObject>()` / `Addressables.LoadAssetAsync<GameObject>(key)` (as in
`LevelGenGraphUtilities`); cache/refcount handles and `Addressables.Release` on teardown.

### Two levels of "vanilla reuse"
- **Whole vanilla `Room` prefab** as backdrop — simplest, most robust (materials guaranteed wired). Strip or
  ignore its spawns/connectors.
- **Individual vanilla props** (pillar, rock, wood support) — if a prop is separately addressable, resolve it
  directly; otherwise resolve its parent room once and pluck the named child at runtime.

## 2.4 Proposed runtime ownership & lifecycle (Strategy B)

A single mod-owned controller, e.g. `ArenaController` (project-owned, no vanilla base type), owns the arena's
whole lifecycle. **Ready comes before placement**, matching the multiplayer contract exactly
([MultiplayerLoadingContract.md §5.3](MultiplayerLoadingContract.md)) — a peer must prove it built the same
arena before anyone stands in it, and before a boss exists:

```
Enter:  gate/trigger fires (a real game event)
  → ArenaController.LoadAsync(ArenaManifest)
      1. instantiate our layout (VisualRoot / CollisionRoot / NavigationRoot / GameplayRoot / LightingRoot)
      2. resolve + instantiate all VanillaAssetProxy targets (Addressables)  [await all]
      3. build/apply A* navigation over the arena, via INavigationPort  (report 4)
      4. compute the local ContentHash from the realized result
      5. report ArenaReady(ArenaId, ArenaVersion, ContentHash, ProtocolVersion, BundleVersion)
           single-player : the required set is one local peer; the gate resolves immediately
           multiplayer   : host validates every required peer via IEncounterReadyGate (report 5)
                           mismatch / load failure / timeout  →  FAIL CLOSED, abort, tear down
  → after the gate passes (host authoritative)
      6. seal the arena and teleport player(s) to PlayerSpawn, via IArenaLockdownPort
      7. publish EncounterBaseline
      8. spawn the boss at BossSpawn and start the simulation
Exit:   boss dead / player leaves
  → ArenaController.Unload()
      release Addressables handles, destroy roots, remove the arena's own nodes/links/modifiers from the
      active level's A* graph, clear GameplayRoot state, unsubscribe every replication/event handler
```

There is **one** sequence, not a single-player one and a multiplayer one; single-player is the degenerate case
with a one-member required set.

Lifecycle hooks attach to **canonical events** — the gate/trigger that starts the encounter, the method that
commits the level transition, the authoritative boss-death event — never to scene names, object names, or timing
heuristics ([DependencyRules.md §4](DependencyRules.md)). SULFUR Together already patches the relevant
level-transition methods (`SwitchLevelRoutine`, `CompleteLevel`) and boss triggers, so the arena entry can be
driven from a genuine game event; False Gods reaches those through `ISceneLifecycleEvents`, not by naming the
patch.

## 2.5 What still needs verification (feeds the PoC)
- Whether our own AssetBundle built in the game's Unity version loads cleanly under BepInEx (Unity version
  match, shader stripping) — report 3, RiskList.
- Exact Addressables key/GUID stability across game updates — RiskList R1.
- Whether resolving a vanilla prop out of a room prefab (vs a top-level addressable) is needed for the props
  we want.
