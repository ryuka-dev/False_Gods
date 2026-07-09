# 6. Risk List

*Ranked unknowns and likely failure points, each with the cheapest first-validation.* Ordered by a rough
`impact × uncertainty`. "Validate by" items are designed to be checkable **before** committing to the full
arena.

| # | Risk | Why it matters | Cheapest first-validation |
|---|------|----------------|---------------------------|
| **R1** | **Addressables key/GUID stability & loadability** of vanilla rooms/props at runtime from a mod | The entire runtime-reuse strategy (reports 1–3) depends on `AssetReference.LoadAssetAsync` / `Addressables.LoadAssetAsync(key)` returning real vanilla prefabs. Keys/GUIDs could differ from what we recorded, or some assets may not be in a loadable group. | From a BepInEx probe plugin, call `Addressables.LoadResourceLocationsAsync` for a couple of known keys and one room GUID; log hits/misses. No arena needed. |
| **R2** | **Unity version match** for our own AssetBundle | A bundle built in the wrong Unity version loads unreliably / breaks shaders. | Read the game's exact Unity version (game files / player log); build a trivial test bundle with one cube in that version and load it under BepInEx. |
| **R3** | **A\* recast rasterization mask + geometry layer** are serialized, not in code | If our `CollisionRoot` isn't on the layer the recast graph rasterizes, the arena won't be walkable. | Runtime-read `AstarPath.active.data.recastGraph.mask/.rasterizeColliders/.rasterizeMeshes` and `GameManager.Instance.geometryLayer`; record in report 4. |
| **R4** | **`NavmeshPrefab.Apply()` from a mod + validity of a bundle-baked navmesh** | Option 1 nav (prebaked, deterministic, cheap) hinges on this. If it fails we fall back to runtime rescan (Option 2), which is heavier and needs the `NavMeshCleaner` workaround. | In the PoC room, bake a `NavmeshPrefab`, call `.Apply()` + `UpdateGraphs(bounds)`, and confirm an enemy paths on it. |
| **R5** | **`NavMeshCleaner` flood-fill discards our island** | On a runtime rescan, walkability is restricted to areas containing `validNavMeshPoints` (connectors + `navMeshAnchor`s). A custom arena with none gets marked unwalkable. | In the PoC, do a rescan and observe whether the arena floor is walkable; if not, add a `navMeshAnchor`-tagged transform / custom `GraphModifier` and retest. |
| **R6** | **Shader variants missing** for reused/own materials | Pink materials in-game even if fine in editor. | Instantiate a vanilla cave prefab at runtime and eyeball it; for any original shader, ship a variant collection. (report 3.6) |
| **R7** | **Multiplayer arena-load parity & readiness races** | If clients load a slightly different arena or the host starts the boss before a client is ready, states diverge. | PoC host+client parity check (report 7): both ends load arena, compare an arena hash, gate on all-ready before any spawn. |
| **R8** | **Teardown pollutes the next level** (stale nav nodes / leaked objects / unreleased Addressables) | `AstarPath.active` is persistent and shared; leftovers corrupt the next real level or leak memory. | PoC: enter arena → leave → load a normal level; assert no arena `GameObject`s remain, Addressables handles released, and the next level's nav is correct. |
| **R9** | **Big-boss pathing** doesn't fit a normal recast agent | A large original boss may clip walls / stick on corners with plain `RichAI`. | Prototype boss movement as raycast-ground-follow (like `EmperorBossSpiderEndless`) + navmesh target queries, not pure `RichAI`. (report 4.5) |
| **R10** | **Enemy activation for a client who enters first** | Vanilla `NpcUpdateManager.LateUpdate` wakes NPCs only near the host singleton. | Reuse SULFUR Together's registry fix (register remote players in `GameManager.Players`); verify an add wakes for a client. |
| **R11** | **Boss encounter-start caveats** (client invulnerability / presentation) | `BossAuthority.md` notes client presentation can leave the boss permanently invulnerable if `DoneAppearing` never fires. | Follow the existing "presentation suppressed by default" rule; add the new boss via an adapter, not a bespoke start path. |
| **R12** | **Legal / asset-redistribution** | Shipping vanilla meshes/textures/shaders is not acceptable. | Enforce: mod bundle contains only original assets + proxy keys; all vanilla content resolved at runtime (report 2). CI/pack check. |

## Suggested validation order (do the cheap, high-leverage probes first)
1. **R2 + R1** — can we build/load a bundle, and can we resolve vanilla Addressables? (No arena required.)
2. **R3 + R5 + R4** — nav: layers/mask, rescan vs `NavmeshPrefab`, cleaner behaviour — in the PoC room.
3. **R6** — materials render correctly at runtime.
4. **R8** — clean teardown.
5. **R7 + R10** — host+client parity and enemy activation.
6. **R9 + R11** — deferred until a boss actually exists.

Everything above R7 can be checked in **single-player** with a probe plugin + the PoC room, before any
multiplayer or boss work.
