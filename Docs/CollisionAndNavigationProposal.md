# 4. Collision & Navigation Proposal

*Recommended collision layering, how to get the arena into SULFUR's navigation, boss pathing, and runtime
teardown.* This is the most game-specific report — **SULFUR does not use Unity NavMesh; it uses the A\*
Pathfinding Project.**

## 4.1 Decoupled layers (as the request intends)

```
ArenaRoot
  ├─ VisualRoot       vanilla cave prefabs/props (report 2/3), no gameplay colliders relied upon
  ├─ CollisionRoot    our own SIMPLE colliders: 1 floor, 4 boundary walls, few pillar colliders
  ├─ NavigationRoot   what the A* recast graph should treat as walkable + off-mesh links + anchors
  ├─ GameplayRoot     PlayerSpawn, BossSpawn, seal triggers, exit, mechanisms, phase objects
  └─ LightingRoot     realtime lights, fog/ambient, reflection probes (report 3.3)
```

Rationale (from the decompile): even the vanilla game keeps *generation-time* collision simple
(`WorkingCollision`, `quickCollisonCheckRadius`, a `bakedCollision` TextAsset) rather than testing full rock
meshes. We follow the same instinct — never make the boss or players collide with decorative rock
`MeshCollider`s.

## 4.2 Collision layer vs navigation layer — they are *not* the same thing

**Measured (probe P0, see §4.4).** Physics/LOS geometry and navigation-rasterized geometry are driven by
**different layer masks and different geometry**, and the arena must satisfy both separately:

| Concern | Source | Layers (measured, Act_02_Fortress) |
|---|---|---|
| Physics, AI line-of-sight, ground checks | `GameManager.geometryLayer` | `Geometry(3)`, `StaticDoodad(12)`, `InvisibleGeometry(18)`, `GeometryNoNavMesh(22)`, `LevelGenBlock(26)` |
| What the recast graph rasterizes | `recastGraph.collectionSettings.layerMask` | `Geometry(3)`, `StaticDoodad(12)`, `InvisibleGeometry(18)`, `ProjectileTrigger(30)` |

Two consequences that rewrite the old advice here:

- **The recast graph rasterizes MESHES, not colliders** (`rasterizeMeshes = true`, `rasterizeColliders =
  false`). So a `CollisionRoot` collider on the right layer is **invisible to navigation** — it defines physics
  only. The walkable surface the boss/enemies path on is built from **`MeshRenderer` meshes on a mask layer**
  (`Geometry(3)` is the obvious one), not from colliders. `NavigationRoot` must therefore carry real geometry
  (a floor mesh on layer 3), or a prebaked `NavmeshPrefab` (report §4.3 Option 1); a bare collider will not do.
- **The two masks differ on purpose.** `GeometryNoNavMesh(22)` is physics-solid but deliberately excluded from
  the nav rasterization — exactly the layer for decorative rock that players should collide with but the boss
  should never try to walk on. `LevelGenBlock(26)` is likewise physics-only. Put arena decoration whose
  collision matters but whose walkability must not on layer 22.

Practical arena rules, updated:

- **Floor**: a real floor **mesh** on `Geometry(3)` (rasterized) plus a flat floor **collider** on a physics
  layer. The mesh makes it walkable; the collider makes it solid.
- **Boundary walls**: an invisible collider box for physics; keep it off the nav mask (or give it no mesh) so
  it never becomes a walkable ledge.
- **Decoration** (vanilla rock, props): collider on `GeometryNoNavMesh(22)` if it should block movement but not
  navigation, or strip its colliders entirely. Never let decorative meshes land on `Geometry(3)`, or the recast
  scan will treat their surfaces as walkable and produce corner-sticking.

> The layer indices and the rasterization settings are **serialized on the scene's `AstarPath` component**, not
> in code. The values above were read at runtime by `tools/FalseGods.Probe` (RiskList R3). Note the members
> moved in this A* version: read `recastGraph.collectionSettings.{layerMask, rasterizeColliders,
> rasterizeMeshes}` — `RecastGraph.mask` / `.rasterizeColliders` / `.rasterizeMeshes` are now `[Obsolete]`
> shims. Full agent parameters in §4.4.

## 4.3 Navigation — the A\* recast graph (the real system)

Verified facts (`BuildNavMeshNode`, `NavMeshManager`, `NavMeshCleaner`, `RecastTagVolume`, `AiAgent`,
`CustomRichAI`):

- There is **one `AstarPath.active`** at a time, and it is **shared global state for the currently active
  level**. Its recast graph is **graph index 0** (`NNConstraint.graphMask = 1` throughout the code), built at
  runtime rather than baked per scene.
- It is **not persistent across levels.** A normal level change destroys and rebuilds it. In
  `GameManager`'s level-switch routine:
  ```csharp
  // Decompiled/.../GameManager.cs:1097
  if (AstarPath.active != null) {
      AstarPath.active.data.ClearGraphs();
      UnityEngine.Object.Destroy(AstarPath.active.gameObject);
  }
  // …then, for the next level:
  // Decompiled/.../GameManager.cs:1137
  UnityEngine.Object.Instantiate<AstarPath>(astarPathPrefab);
  AstarPath.FindAstarPath();
  ```
  ⚠️ This does **not** license an additive arena to leak. The arena shares the graph with the level the players
  are still standing in; a future level change would clear it, but everything between arena exit and that
  transition would run against a polluted graph. See §4.6.
- The runtime scan the game performs (`BuildNavMeshNode.Execute`):
  1. `AstarPath.active.data.recastGraph.cellSize = currentEnvironment.navMeshVoxelSize` (**0.1** by default).
  2. `recastGraph.SnapBoundsToScene()` (fit graph bounds to current geometry).
  3. add each room's `bakedNavMeshLinks` to `AstarPath.active.offMeshLinks`; call `NodeLink2.TryAddLink()`.
  4. `AstarPath.active.ScanAsync()` (async in play mode) — or `.Scan()` synchronously.
  5. after scan, `RecastTagVolume.UpdateGraphTags()` runs (via `AstarPath.OnPostScan`).
- `NavMeshManager` exposes the two calls as a tidy reusable pair:
  `SetupAstarPathSize()` = `SnapForceBoundsToScene()`, `BakeNavMesh()` = `Scan()`.
- **`NavMeshCleaner : GraphModifier.OnGraphsPostUpdate`** flood-fills walkability: it looks up the graph
  `Area` of each point in `validNavMeshPoints` (built from every connector position + every room's
  `navMeshAnchors`) and marks **only nodes in those areas as walkable**; everything else becomes unwalkable.
  ⚠️ **This is the single most important gotcha for a custom arena**: if our arena's walkable island is not
  represented in `validNavMeshPoints`, the cleaner will mark it unwalkable and the boss/enemies won't move.
  **Measured (probe P0):** on a live level the cleaner is real and populated — the active `NavMeshManager(Clone)`
  had exactly **3** `validNavMeshPoints`, sourced from the single loaded `Room`'s **3** `navMeshAnchors`
  (`Room.NAVMESH_ANCHOR_TAG = "NavMeshAnchor"`). With those 3 points the scan produced 1492 nodes, **all
  walkable** — so the cleaner keeps everything reachable from an anchored area and would erase anything else.
  An additive arena contributes **zero** anchors unless we add them, which is exactly the R5 failure mode:
  place at least one `NavMeshAnchor`-tagged transform inside the arena island, or drive nav via a prebaked
  `NavmeshPrefab` (Option 1) that side-steps the cleaner. (A second, inactive `NavMeshManager` instance with a
  null point set also exists in the scene; the live clone is the one that matters.)

### Recommended approach for the arena's nav

**Option 1 (recommended for a fixed arena): prebaked `NavmeshPrefab`.**
`BuildNavMeshNode` shows the game already supports **`NavmeshPrefab`** objects: if any exist, it calls
`prefab.Apply()` + `AstarPath.active.UpdateGraphs(prefab.bounds)` **instead of** a full rescan. We can bake
the arena's navmesh in our editor project and ship it as a `NavmeshPrefab`, then apply it at arena load. This
is deterministic (identical on host and every client), cheap at runtime, and side-steps both the recast scan
cost and the `NavMeshCleaner` flood-fill. *Needs verification that `NavmeshPrefab.Apply()` is callable from a
mod and that a bundle-baked navmesh is valid (RiskList R4).*

**Option 2 (fallback): runtime recast rescan.**
After the arena geometry is present, call the game's own rescan (`NavMeshManager.SetupAstarPathSize()` +
`BakeNavMesh()`, i.e. `SnapForceBoundsToScene()` + `Scan()`). Then either (a) ensure a walkable anchor inside
the arena is included wherever `NavMeshCleaner.validNavMeshPoints` is computed, or (b) provide our own
`GraphModifier` / place a `navMeshAnchor`-tagged transform so our island survives cleaning. Runtime rescan is
more flexible (supports future destructible/phase-changing terrain) but costs a scan and must be sequenced
carefully in multiplayer (report 5).

Off-mesh links (jumps/drops across gaps) use A* `NodeLink2` / `OffMeshLinks.OffMeshLinkSource`; add them only
if the arena needs disconnected walkable areas. A single flat arena floor needs none.

## 4.4 Real agent parameters

- `cellSize` (voxel size) is the one nav parameter set **from game code** per environment:
  `WorldEnvironment.navMeshVoxelSize = 0.1`. Match this for the arena's environment so agent fit is
  consistent.
- The rest of the recast parameters are **serialized on the scene `AstarPath` RecastGraph**, not in code.
  **Measured** by probe P0 on a real level (Act_02_Fortress), so these are the real limits to design within,
  no longer estimates:

  | Parameter | Measured | Design implication |
  |---|---|---|
  | `cellSize` (voxel size) | **0.1** | matches `WorldEnvironment.navMeshVoxelSize`; keep arena voxel scale here |
  | `characterRadius` | **0.5** | agents keep ~0.5 m from walls; corridors narrower than ~1 m may not path |
  | `walkableHeight` | **1.5** | ceilings/overhangs below 1.5 m are not walkable underneath |
  | `walkableClimb` | **0.6** | steps up to 0.6 m are traversable without an off-mesh link |
  | `maxSlope` | **45°** | keep the main floor flat; ramps must stay ≤ 45° |
  | `maxEdgeLength` | **20** | tiling detail; not a design constraint |
  | `contourMaxError` | **2** | mesh simplification tolerance |
  | `minRegionSize` | **1** | tiny isolated islands are discarded |

  Graph tiling: `useTiles = true`, tile size 128×128, `Dimension3D`. Two graphs exist —
  **`RecastGraph` at index 0** (confirmed; `NNConstraint.graphMask = 1` targets it) and a `PointGraph` at
  index 1 (off-mesh links). Design arena slopes/steps within the limits above; keep the main floor flat, per
  the arena design requirements.

> Source: `tools/FalseGods.Probe` report, game **6000.3.6f1**, **A\* Pathfinding Project 5.3.8**, probe 0.1.0,
> Gale profile `Bossmod开发`. Values are from one real level; a second level should be spot-checked before
> treating them as universal, but the agent parameters are set per `RecastGraph` and are not expected to vary
> by environment (only `cellSize` is set per environment, and it already matches).

## 4.5 Boss / enemy pathing

- Ordinary enemies and most bosses are `Npc` units with an `AiAgent`; movement is via **`CustomRichAI`** (a
  subclass of A*'s `RichAI`) over the recast graph. They query
  `AstarPath.active.GetNearest(pos, NNConstraint.Walkable)` constantly (e.g. `AiAgent`, `BossFightHelper`).
- **Keep the boss's walkable area a clean, convex-ish flat island.** Because agents snap to
  `GetNearest(...Walkable)`, concave pockets, thin ledges, and decoration-induced holes cause corner-sticking
  — exactly what the arena-design requirements want to avoid. Put decorative rock **outside** the nav mask.
- **Large / special-movement bosses**: the vanilla `EmperorBossSpiderEndless` does **not** rely purely on the
  recast graph — it uses `Physics.Raycast(..., groundLayerMask)` ground-following for its body. So for a big
  original boss, movement can be **scripted/physics-driven** (raycast to ground + our own steering) and use
  the recast graph only for target-point queries, or not at all. Dashes/leaps/teleports should move the boss
  directly and then **re-snap to the navmesh** on landing via `GetNearest(...Walkable)` (the pattern
  `BossFightHelper` already uses).
- **Knockback / off-navmesh recovery**: after knockback or a leap, snap the boss back with
  `AstarPath.active.GetNearest(pos, NNConstraint.Walkable).position` before resuming pathing (vanilla bosses
  do this). Guard against being pushed outside the boundary walls (the invisible walls + a recovery snap).
- **Authority**: in multiplayer the **host runs boss AI/pathing**; the client shows an interpolated puppet
  (report 5, and SULFUR Together `HostDrivenProxyPlan.md`). Never compute boss paths on the client.

## 4.6 Runtime teardown (must not pollute the active level)

`AstarPath.active` is **shared global state for the current level**. A normal level change rebuilds it
(`ClearGraphs()` + `Destroy(...)` at `GameManager.cs:1097`, `Instantiate(astarPathPrefab)` at `:1137` — §4.3), so
arena residue does not survive a level transition.

**That is not a cleanup strategy.** The additive arena adds its nodes, links, and modifiers to the graph the
players are currently walking on. Between arena exit and the next level change, every vanilla NPC in that level
paths on whatever we left behind. The arena must therefore remove exactly what it added, when it exits — not
wait for a rebuild that may be many minutes away, and that a Boss Rush or hub-return flow may never reach.

- If **Option 1 (NavmeshPrefab)** was used: on exit, remove/replace the applied graph region and re-apply or
  rescan the region the arena overwrote, so the surrounding level's walkability is restored.
- If **Option 2 (rescan)** was used: unload the arena geometry and rescan, so the graph reflects the level
  without the arena. Do not leave stale walkable nodes that a subsequent `NavMeshCleaner` pass might treat as
  valid.
- In both cases: destroy all arena roots, `Addressables.Release` every handle taken for vanilla assets, remove
  any off-mesh links we added to `AstarPath.active.offMeshLinks`, and unregister any
  `GraphModifier`/`RecastTagVolume` we added. Verify no arena `GameObject`s or nav nodes survive — first into
  the rest of the current level, then into the next one (PoC teardown check, P7).

All of this happens behind `INavigationPort`; no gameplay or arena code names `AstarPath` directly.

## 4.7 Open verification items (RiskList)
- R3: exact geometry layer + recast rasterization mask (read at runtime).
- R4: `NavmeshPrefab.Apply()` usability from a mod + validity of a bundle-baked navmesh.
- R5: real recast agent params (read at runtime, record here).
- R8: teardown leaves the **active level's** `AstarPath` graph clean, without relying on the next level change
  to rebuild it.

## 4.8 Unity-authored navigation source

`NavigationRoot` is authored visually as part of the arena prefab.

It may contain:

- geometry markers;
- scan bounds;
- walkable anchors;
- off-mesh-link markers;
- boss movement volumes;
- recovery points;
- forbidden regions.

The final gameplay pathing still uses SULFUR's A* Recast Graph. Unity editor authoring does not imply use of
Unity NavMesh.

Editor tools may visualize the intended navigation surface, but the authoritative runtime result is the
A* graph produced or applied inside SULFUR.

### Original-boss movement models

Original bosses are not required to inherit the complete vanilla `AiAgent` movement model.

Each boss may select one of:

1. normal `CustomRichAI` pathing;
2. A* target queries plus scripted locomotion;
3. fully scripted arena-relative movement;
4. stationary boss with moving attack origins;
5. multi-part or rail-based movement.

The movement model must expose explicit authoritative state suitable for replication
(see [OriginalBossNetworkingArchitecture.md](OriginalBossNetworkingArchitecture.md)).
