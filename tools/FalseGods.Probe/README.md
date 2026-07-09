# FalseGods.Probe — throwaway PoC probe (P0 / P1)

A read-only BepInEx plugin that reads real values out of a running SULFUR, so the highest-risk unknowns in
[RiskList.md](../../Docs/RiskList.md) stop being guesses. It answers PoC steps **P0** and **P1**
([MinimalProofOfConceptPlan.md §7.2](../../Docs/MinimalProofOfConceptPlan.md)).

**This is disposable.** When P0/P1 have been run on a real game and their results transcribed back into the
docs (below), delete `tools/FalseGods.Probe/` and this directory with it. Deleting it is a one-line change —
it is not in `False Gods.slnx` and nothing under `src/` references it, both enforced by
`tests/FalseGods.ArchitectureTests/Checks/ProbeIsIsolatedChecks.cs`.

## What it answers

| PoC / Risk | Question | Where it reads |
|---|---|---|
| P0 / **R3** | What layer(s) does the recast graph rasterize, and what is `GameManager.geometryLayer`? | `recast.collectionSettings.layerMask`, `GameManager.Instance.geometryLayer` |
| P0 / §4.4 | The real recast agent parameters (serialized on the component, in no source file) | `recast.cellSize / characterRadius / walkableHeight / walkableClimb / maxSlope`, … |
| P0 / **R5** | Does `NavMeshCleaner` have `validNavMeshPoints`, and how many? (its flood-fill erases any island not represented there) | `NavMeshCleaner.validNavMeshPoints`, `Room.navMeshAnchors` |
| P0 | Is `AstarPath.active` really rebuilt per level? (lifecycle claim behind ADR-002 / R8) | `AstarPath.active.GetInstanceID()` changing across levels |
| P1 / **R1** | Can mod code resolve, load **and instantiate** a vanilla room prefab by the GUIDs the game itself holds? | `LevelBlock.roomPrefabsAddressable` → `LoadResourceLocationsAsync` → `LoadAssetAsync<GameObject>` → `Instantiate` |
| P1 / R6 | (bonus) What shaders and collider layers does a vanilla room prefab carry? | renderers / colliders on the instantiated prefab |

It mutates **no authoritative game state**: no Harmony patches, no manager registration, no world spawn. P1's
acceptance requires instantiation, so it does instantiate one prefab — but under an **inactive holder**, so no
component `Awake`/`OnEnable`/`Start` runs (Unity does not run those on an object inactive in the hierarchy), and
the instance is destroyed immediately after inspection. The Addressables handle is released.

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
# Build (needs LocalPaths.props — SulfurManagedDir, BepInExCoreDir):
dotnet build tools/FalseGods.Probe/FalseGods.Probe.csproj

# Build AND deploy into the BepInEx plugins folder from LocalPaths.props (opt-in):
dotnet build tools/FalseGods.Probe/FalseGods.Probe.csproj -p:DeployProbe=true
```

Then launch the game, **enter a normal level**, and either let the automatic post-scan report fire or press
**F10** once you are standing in the arena. Each run writes a timestamped `probe-YYYYMMDD-HHMMSS.txt` under
`BepInEx/FalseGods.Probe/` (gitignored) and echoes to the BepInEx console. Prefer the F10 report.

> Not in `verify.ps1`: launching the game is the manual, pre-release level of verification
> ([ArchitectureEnforcement.md §4](../../Docs/ArchitectureEnforcement.md)), never a per-commit gate.

## After running

1. Enter a normal level, let the probe fire, grab the report.
2. Transcribe the measured values into:
   - [CollisionAndNavigationProposal.md §4.4](../../Docs/CollisionAndNavigationProposal.md) — agent parameters
   - [RiskList.md](../../Docs/RiskList.md) — R1 (resolution works?), R3 (layers), R5 (cleaner points)
   - flip the affected *proposed / unverified* notes to measured facts, citing the report.
3. Delete `tools/FalseGods.Probe/`.
