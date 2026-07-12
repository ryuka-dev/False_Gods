# FalseGods.Probe — throwaway PoC probe (P0 / P1 / P2 / P3)

A BepInEx plugin that reads real values out of a running SULFUR, so the highest-risk unknowns in
[RiskList.md](../../Docs/RiskList.md) stop being guesses. It answers PoC steps **P0**, **P1**, **P2** and
**P3** ([MinimalProofOfConceptPlan.md §7.2](../../Docs/MinimalProofOfConceptPlan.md)).

**P0/P1/P2 are read-only** (F10). **P3 is a visible render check** (F11): it deliberately shows real objects
on screen so you can judge pink/no-pink with your eyes — see the read-only note below for exactly how far that
departs from read-only, and how it is contained.

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

P0/P1/P2 mutate **no authoritative game state**: no Harmony patches, no manager registration, no world spawn.
P1's acceptance requires instantiation, so it does instantiate one prefab — but under an **inactive holder**, so
no component `Awake`/`OnEnable`/`Start` runs (Unity does not run those on an object inactive in the hierarchy),
and the instance is destroyed immediately after inspection. The Addressables handle is released.

**P3 is the one visible step**, and stays as contained as a render check can be:

- Our own room is shown active — it has **no MonoBehaviours**, so only its lights/renderers/colliders come alive.
- The vanilla prefab is instantiated **inactive**, has **every MonoBehaviour stripped** while nothing can
  `Awake`, and only then is shown — what renders is meshes + materials + shaders, never a gameplay script. It
  registers with no manager, spawns nothing.
- Ambient/fog (scene state a prefab cannot carry) is optionally applied to global `RenderSettings` and **always
  restored on teardown**. Everything the stage created is destroyed and the Addressables handle + bundle released
  when you drop it (and on plugin unload, if still up).

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
front of you — our room (lit by its own `LightingRoot`) with a vanilla prefab beside it, and a vanilla floor
material laid on our flat floor. Then judge, with your eyes:

1. Is the vanilla prefab **pink/black** or correctly textured and lit? (R6/R13)
2. Does the vanilla floor material **sit right** on our flat mesh, or swim/mis-scale? (report §3.4 — projection
   vs. authored-UV dependence, which decides the floor strategy)
3. Does **our** lighting read as the light source (visible even where the level's own lights don't reach)?

Press **F11** again to tear the stage down and restore the environment. Needs the deployed bundle (build it in
`FalseGods.Unity`, then `-p:DeployProbe=true` copies it); without it the P3 stage reports "skipped".

> Not in `verify.ps1`: launching the game is the manual, pre-release level of verification
> ([ArchitectureEnforcement.md §4](../../Docs/ArchitectureEnforcement.md)), never a per-commit gate.

## After running

1. Enter a normal level, let the probe fire, grab the report.
2. Transcribe the measured values into:
   - [CollisionAndNavigationProposal.md §4.4](../../Docs/CollisionAndNavigationProposal.md) — agent parameters
   - [RiskList.md](../../Docs/RiskList.md) — R1 (resolution works?), R2 (our bundle loads?), R3 (layers),
     R5 (cleaner points), **R6/R13** (P3: vanilla prefab renders un-pink under our lights? floor material
     projects or needs UVs?)
   - [MaterialCompatibilityReport.md §3.6](../../Docs/MaterialCompatibilityReport.md) — the P3 floor-strategy
     verdict.
   - flip the affected *proposed / unverified* notes to measured facts, citing the report.
3. Delete `tools/FalseGods.Probe/`.
