# FalseGods.Probe — throwaway PoC probe (P0 / P1 / P2 / P3 / P4 / P5 / P6)

A BepInEx plugin that reads real values out of a running SULFUR, so the highest-risk unknowns in
[RiskList.md](../../Docs/RiskList.md) stop being guesses. It answers PoC steps **P0**, **P1**, **P2**, **P3**,
**P4** and **P5** ([MinimalProofOfConceptPlan.md §7.2](../../Docs/MinimalProofOfConceptPlan.md)).

**P0/P1/P2 are read-only** (F10). **P3 is a visible render check** (F11): it shows real objects on screen so
you can judge pink/no-pink with your eyes. **P4 is a collision check** (F9): it places our sealed arena
around you so you can walk it on foot. **P5 is an A\* nav check** (F8): it makes our floor walkable at runtime
and confirms it survives the `NavMeshCleaner`. P3/P4 show real objects; **P5 mutates the live level's nav
graph** — see the notes below for exactly how far each departs from read-only, and how it is contained.

**This is disposable.** P0/P1 have been run (game 6000.3.6f1, A\* 5.3.8, Gale profile `Bossmod开发`) and the
results transcribed into report 4.2/4.4 and RiskList R1/R3/R5. P2 (our own AssetBundle loads) runs from the
same report; once its result is transcribed into RiskList R2 the P2 section is done too. The one reason to
keep the probe a little longer: at **P5** it can re-check R5 (does *our* arena's `NavMeshAnchor` survive the
cleaner) inside the custom arena, which is the same read it already does. Delete it after P5 at the latest.
Deleting it is a one-line change — it is not in `False Gods.slnx` and nothing under `src/` references it, both
enforced by `tests/FalseGods.ArchitectureTests/Checks/ProbeIsIsolatedChecks.cs`.

## What it answers

| PoC / Risk | Question | Where it reads |
|---|---|---|
| P0 / **R3** | What layer(s) does the recast graph rasterize, and what is `GameManager.geometryLayer`? | `recast.collectionSettings.layerMask`, `GameManager.Instance.geometryLayer` |
| P0 / §4.4 | The real recast agent parameters (serialized on the component, in no source file) | `recast.cellSize / characterRadius / walkableHeight / walkableClimb / maxSlope`, … |
| P0 / **R5** | Does `NavMeshCleaner` have `validNavMeshPoints`, and how many? (its flood-fill erases any island not represented there) | `NavMeshCleaner.validNavMeshPoints`, `Room.navMeshAnchors` |
| P0 | Is `AstarPath.active` really rebuilt per level? (lifecycle claim behind ADR-002 / R8) | `AstarPath.active.GetInstanceID()` changing across levels |
| P1 / **R1** | Can mod code resolve, load **and instantiate** a vanilla room prefab by the GUIDs the game itself holds? | `LevelBlock.roomPrefabsAddressable` → `LoadResourceLocationsAsync` → `LoadAssetAsync<GameObject>` → `Instantiate` |
| P1 / R6 | (bonus) What shaders and collider layers does a vanilla room prefab carry? | renderers / colliders on the instantiated prefab |
| P2 / **R2** | Does an AssetBundle built in the game's exact Unity version (`FalseGods.Unity`, 6000.3.6f1) load under BepInEx with meshes/materials/collider layers intact? | `AssetBundle.LoadFromFileAsync` on `BepInEx/FalseGods.Probe/falsegods-poc-room.bundle` → instantiate under an inactive holder → inspect → `Unload(true)` |
| P3 / **R6, R13** | Does a vanilla prefab render correctly (no pink) under **our** `LightingRoot`, and does one vanilla floor material behave on our own flat ground mesh? | shows our room (bundle, now with lights) + a vanilla prefab, borrows a vanilla floor material onto our floor — **you judge on screen** (report §3.4) |
| P4 / **R3** | Is our arena solid to the player on foot — floor holds, pillar blocks, walls contain, no snagging? | places our room so its `PlayerSpawn` marker sits under your feet, leaving you inside the sealed arena — **you judge on foot** (no teleport, no F3) |
| P5 / **R4, R5** | Can a mod make its own arena floor walkable at runtime, and does it survive `NavMeshCleaner`'s flood-fill? | spawns our room as an isolated island, `UpdateGraphs` over it with **no** anchor (cleaner erases it) then **with** a `validNavMeshPoint` on it (it survives) — read from `GetNearest(...).node.Walkable` |
| P6 / **R9** | Does A\* pathing work on our applied arena — does a path route **around** the pillar, and does a real vanilla enemy follow it? | bakes+applies our navmesh (P5c+P5d) on an isolated island, then (1) an `ABPath` between the EnemySpawn/PlayerSpawn corners whose straight line crosses the pillar must route around it, and (2) a real vanilla `Npc` (by `UnitId`) is spawned, activated and driven past the pillar to the far corner |

P0/P1/P2 mutate **no authoritative game state**: no Harmony patches, no manager registration, no world spawn.
P1's acceptance requires instantiation, so it does instantiate one prefab — but under an **inactive holder**, so
no component `Awake`/`OnEnable`/`Start` runs (Unity does not run those on an object inactive in the hierarchy),
and the instance is destroyed immediately after inspection. The Addressables handle is released.

**P3 and P4 are the visible steps**, and each stays as contained as its check can be:

- Our own room is shown active — it has **no MonoBehaviours**, so only its lights/renderers/colliders come alive.
- The vanilla prefab (P3 only) is instantiated **inactive**, has **every MonoBehaviour stripped** while nothing
  can `Awake`, and only then is shown — what renders is meshes + materials + shaders, never a gameplay script. It
  registers with no manager, spawns nothing.
- Ambient/fog (scene state a prefab cannot carry) is optionally applied to global `RenderSettings` and **always
  restored on teardown**. Everything the stage created is destroyed and the Addressables handle + bundle released
  when you drop it (and on plugin unload, if still up).
- P4 **never moves the player** — it moves the room so `PlayerSpawn` lands under your feet. The player's CMF
  movement controller is untouched (its position-set path is not in our decompiled reference, so we do not
  depend on it); when you drop the room you simply fall onto the level floor.
- P5 is the one step that **writes to shared state** — `AstarPath.active`, the level's nav graph. It only
  *appends* to `NavMeshCleaner.validNavMeshPoints` (the level's own areas stay valid), restores that list, and
  re-bakes the nav (`ScanAsync`) before it returns, and destroys its island. Any residue is wiped on the next
  level change — GameManager rebuilds `astarPathPrefab` per level (P0). Run it in a throwaway level all the same.

## When it runs (timing matters)

`AstarPath.active` exists early, but the graph is not configured or scanned until the MakerGraph pipeline
reaches `BuildNavMeshNode` — which sets the cell size, fills `NavMeshCleaner.validNavMeshPoints`, then scans.
Reading at "AstarPath exists" would capture default values, a null cleaner point set, and zero scanned nodes.

So the probe fires on the game's own **`AstarPath.OnPostScan`** (the same static event `BuildNavMeshNode` uses),
by which point rooms, graph and cleaner points are all ready. **F10 is the authoritative fallback**: stand
inside a loaded arena and press it — that report is the one to trust, because you control when it is taken. Auto
reports are labelled by trigger in the file, so a too-early one is identifiable.

## Correction it already forced

Building the probe against the real `AstarPathfindingProject.dll` showed that
[CollisionAndNavigationProposal.md §4.2](../../Docs/CollisionAndNavigationProposal.md) is wrong about where the
rasterization settings live: `mask` / `rasterizeColliders` / `rasterizeMeshes` are no longer `RecastGraph`
fields — they moved to `recast.collectionSettings` (`layerMask`, `rasterizeColliders`, …), and the old names
are `[Obsolete]` shims. This was found at compile time, before the game was ever launched.

## Build and run

```powershell
# P2 only — build the PoC bundle first (or use the editor menu "False Gods/Build PoC AssetBundle"):
& "D:\Unity\6000.3.6f1\Editor\Unity.exe" -batchmode -nographics -projectPath FalseGods.Unity `
    -executeMethod FalseGods.EditorTools.PocBundleBuilder.BuildFromBatchMode -logFile unity-build.log

# Build (needs LocalPaths.props — SulfurManagedDir, BepInExCoreDir):
dotnet build tools/FalseGods.Probe/FalseGods.Probe.csproj

# Build AND deploy into the BepInEx plugins folder from LocalPaths.props (opt-in).
# Also copies FalseGods.Unity/Build/falsegods-poc-room.bundle → BepInEx/FalseGods.Probe/ when it exists;
# without the bundle, the P2 section reports "skipped" and P0/P1 still run.
dotnet build tools/FalseGods.Probe/FalseGods.Probe.csproj -p:DeployProbe=true
```

Then launch the game, **enter a normal level**, and either let the automatic post-scan report fire or press
**F10** once you are standing in the arena. Each run writes a timestamped `probe-YYYYMMDD-HHMMSS.txt` under
`BepInEx/FalseGods.Probe/` (gitignored) and echoes to the BepInEx console. Prefer the F10 report.

**P3 (F11) — the visible render check.** Stand in a loaded level and press **F11**: a stage appears ~18 m in
front of you — our room (lit by its own `LightingRoot`) with a vanilla prefab beside it. Then judge, with your
eyes:

1. Is the vanilla prefab **pink/black** or correctly textured and lit? (R6/R13)
2. With `VisualFixOurMaterials` on (default), our floor + pillar wear a **borrowed vanilla material**: do they
   render un-pink, and does the material **sit right** on our flat mesh or swim/mis-scale? (report §3.4)
3. Does **our** lighting read as the light source (visible even where the level's own lights don't reach)?

**Measured (2026-07-12):** the vanilla prefab and a borrowed vanilla material render correctly under our lights;
our **own** stock-`URP/Lit` materials render **pink** and the game has no resident `URP/Lit` to adopt
(`Shader.Find` misses) — original materials need a vanilla material or a `ShaderVariantCollection` (report §3.8).
Turn `VisualFixOurMaterials` **off** to see the raw pink.

Press **F11** again to tear the stage down and restore the environment. Needs the deployed bundle (build it in
`FalseGods.Unity`, then `-p:DeployProbe=true` copies it); without it the P3 stage reports "skipped".

**P4 (F9) — the collision check.** Stand in a loaded level and press **F9** (not F12 — that is the
screenshot key): our sealed arena appears *around* you, with its `PlayerSpawn` marker under your feet — so you
are inside it, on the floor, clear of the central pillar and the walls. The room's four boundary walls seal it
by design, which is why the P3 stage 18 m away could only be entered with F3/noclip; P4 puts you inside without
teleporting the player. It wears the same borrowed vanilla material as P3 (the donor prefab sits outside the
walls — ignore it), so the arena reads normally rather than pink. Then judge, on foot:

1. Do you stand **on** our floor — not sinking through, not floating? (R3)
2. Does the central **pillar block** you (no clipping, no snagging)?
3. Do the four **walls contain** you (you cannot walk out)?
4. Circle the pillar and the perimeter — any **snagging** on edges or corners?

Press **F9** again to remove the room; you drop back onto the level floor. Same bundle requirement as P3.

**P5 (F8) — the A\* nav check.** Stand in a **throwaway** loaded level and press **F8**. There is nothing to
judge by eye — read the report. It spawns our room as an isolated island ~3 m above your feet (so its floor is
its own nav area, not merged with the level floor below) and runs two phases. Each phase **re-bakes the whole
level's nav** with `AstarPath.ScanAsync` — the game's own bake path (`NavMeshManager.BakeNavMesh` /
`BuildNavMeshNode`). `UpdateGraphs(bounds)` was tried first and does **not** work: it only edits existing node
walkability (that is all `MetalGate` uses it for), so our floor was never rasterized — the first run showed the
node total unchanged and our floor 3.9 m from the nearest node. A full `ScanAsync` did not collect it either,
because `RecastGraph.CollectMeshes` gathers scene meshes only by the graph's `collectionMode` (Layers or
Tags), and an untagged bundle mesh is skipped in Tags mode. The floor therefore also gets a
`RecastNavmeshModifier` (`AlwaysInclude`), which `CollectRecastNavmeshModifiers()` picks up in either mode.

1. **Phase 1 — no anchor:** re-bake with the cleaner's points unchanged. R5 predicts our floor is **erased**
   (`nearest node … walkable=False`, `nodes inside island bounds` ~0).
2. **Phase 2 — anchored:** append one `validNavMeshPoint` on our floor and re-bake again. The floor should
   **survive** — `walkable=True`, `distance` ~0.1 m, and the in-bounds walkable count rises.

The report prints an **`R5 verdict`** line (`CONFIRMED` when phase 1 is unwalkable-or-absent and phase 2 is
walkable-and-close). It then restores the cleaner's points, destroys the island, and re-bakes once more.

> **This is the one probe that writes to shared authoritative state** — `AstarPath.active` — and it re-bakes
> the whole level's nav three times, so **NPCs will re-path** while it runs. It only appends a point and
> restores it, and a level change rebuilds the graph, but run it in a level you do not care about. The scan is
> driven over frames (no freeze); each is followed by a fixed 1 s settle for the cleaner's work item.

**P6 (F4) — the A\* pathing check.** Stand in a **throwaway** loaded level and press **F4**. Nothing to judge
by eye until the enemy appears — read the report. It spawns our arena as an isolated island a few metres up,
bakes and applies its navmesh in memory (P5c + P5d, so our floor is walkable and the pillar is a nav hole),
then runs two layers:

1. **Layer 1 — nav-graph proof (deterministic).** Requests an `ABPath` from the EnemySpawn corner (7,7) to the
   PlayerSpawn corner (-7,-7); their straight line passes through the central pillar. The report's
   **`R9 verdict (nav-graph)`** reads `ROUTES AROUND` when the path's closest approach to the pillar centre
   stays outside the footprint (≥ ~0.85 m), or `THROUGH THE PILLAR` (~0 m) if the hole is missing.
2. **Layer 2 — live enemy (visible, best-effort).** Spawns the vanilla enemy named by `Probe/EnemyUnitId`
   (default `HellshrewSticka`), registers + activates it, and drives it to the far corner past the pillar with
   the game's own scripted-movement handle (`SetForcedDestination`, behaviour tree off). Watch it thread past
   the pillar; the **`R9 verdict (live enemy)`** line summarises distance travelled and closest pillar approach.
   If the enemy misbehaves, change `EnemyUnitId` to another `UnitIds` field name — Layer 1 stands regardless.

> **This probe writes to shared authoritative state** — `AstarPath.active` (adds tiles) and
> `GameManager.npcs` (one NPC) — then removes exactly what it added (`ClearTiles` over the same rect,
> unregister + destroy the NPC). A level change rebuilds nav anyway, but run it in a level you do not care about.

> Not in `verify.ps1`: launching the game is the manual, pre-release level of verification
> ([ArchitectureEnforcement.md §4](../../Docs/ArchitectureEnforcement.md)), never a per-commit gate.

## After running

1. Enter a normal level, let the probe fire, grab the report.
2. Transcribe the measured values into:
   - [CollisionAndNavigationProposal.md §4.4](../../Docs/CollisionAndNavigationProposal.md) — agent parameters
   - [RiskList.md](../../Docs/RiskList.md) — R1 (resolution works?), R2 (our bundle loads?), R3 (P0: layers;
     **P4: floor/pillar/walls solid to the player on foot, no snagging?**), R5 (cleaner points), **R6/R13**
     (P3: vanilla prefab renders un-pink under our lights? floor material projects or needs UVs?)
   - [MaterialCompatibilityReport.md §3.6](../../Docs/MaterialCompatibilityReport.md) — the P3 floor-strategy
     verdict.
   - flip the affected *proposed / unverified* notes to measured facts, citing the report.
3. Delete `tools/FalseGods.Probe/`.
