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
| **R11** | **Boss encounter-start caveats** (client invulnerability / presentation) | `BossAuthority.md` notes client presentation can leave a boss permanently invulnerable if `DoneAppearing` never fires. | For an original boss, drive start from the host-authoritative `BossSimulation` (§5.5) with non-authoritative client presentation — the layered model avoids this class of bug by construction; only relevant if reusing a vanilla presentation path. |
| **R12** | **Legal / asset-redistribution** | Shipping vanilla meshes/textures/shaders is not acceptable. | Enforce: mod bundle contains only original assets + proxy keys; all vanilla content resolved at runtime (report 2). CI/pack check. |
| **R13** | **Original shader / render-pipeline compatibility** | Original arena and boss materials may work in the editor but fail in the game bundle. | Load one original opaque material, one transparent sprite material, and one emissive material in the asset PoC. |
| **R14** | **Unity-prefab editor/runtime divergence** | The arena may look correct in the editor but differ after bundle load and runtime vanilla-proxy replacement. | Compute an authored arena manifest and compare runtime hierarchy/transforms against it. |
| **R15** | **2D boss sorting / billboarding / hitbox mismatch** | A large sprite may face incorrectly, sort behind the arena, or have hitboxes detached from visual parts. | Build a temporary multi-part billboard boss with one weak point and one muzzle transform. |
| **R16** | **Boss simulation and presentation remain coupled** | Client presentation code may accidentally make gameplay decisions, recreating vanilla synchronization problems. | Run the boss presentation with simulation disabled and verify it cannot advance phase or deal damage. |
| **R17** | **Host-time / attack-timeline drift** | Clients may show telegraphs or attack commits at different times. | Replicate one attack using host simulation ticks and measure host/client telegraph and commit offsets. |
| **R18** | **Late-join recovery is incomplete** | A client joining during phase 2 may not know active mechanisms, weak points, or current attack. | Join a second client mid-attack and rebuild the complete presentation from a baseline snapshot. |
| **R19** | **Duplicate / out-of-order event handling** | Retransmission may duplicate projectiles, adds, deaths, or rewards. | Replay duplicate and reordered event sequences in a test harness and verify idempotence. |
| **R20** | **False Gods becomes hard-dependent on SULFUR Together** | Single-player may fail if networking types are referenced directly. | Run the same boss build without SULFUR Together installed; optional integration must be discovered/reflected or isolated behind an adapter assembly. |
| **R21** | **SULFUR Together internals leak outside the adapter** | ST/manager/message types spreading into boss/arena/protocol code recreate the coupling debt. | CI namespace scan: fail if `SULFURTogether.*`/`LiteNetLib.*`/`Steamworks.*` appears outside `Integration.SulfurTogether` (DependencyRules §7). |
| **R22** | **Transport-specific types leak into Protocol/Core** | Protocol/domain must be transport-neutral or a transport change ripples inward. | Analyzer/scan: `FalseGods.Protocol` and `.Core` reference neither LiteNetLib/Steamworks nor `Net*` types; Protocol carries only DTOs. |
| **R23** | **Core accidentally gains Unity/game DLL references** | Any such ref makes Core un-testable and couples it to the engine/game. | Core `.csproj` references no Unity/game/network DLLs; a build with those refs absent must compile Core + its tests. |
| **R24** | **BossSimulation couples to a particular arena** | Reaching into arena objects/mechanisms makes a boss non-reusable and hard to test. | Run `BossSimulation` in a unit test with **no** arena and a stub `EncounterCoordinator`; assert it drives phases/attacks without arena refs. |
| **R25** | **Harmony patches spread through feature code** | Distributed patches recreate ST's scattered-patch problem and obscure ownership. | Restrict Harmony to `Integration.Sulfur`; scan for `HarmonyLib`/`[HarmonyPatch]` elsewhere and fail. |
| **R26** | **Global static / service-locator growth** | Arbitrary components fetching global managers reintroduce hidden coupling. | Ban static mutable singletons/service-locators in Core/UnityRuntime; wire dependencies from the Composition Root; review new statics in DoD. |
| **R27** | **Two sources of truth (Simulation vs Presentation)** | Presentation holding authoritative state diverges from the host simulation. | Assert presentation has no authoritative fields; run presentation with simulation disabled and confirm it cannot advance state (RiskList R16). |
| **R28** | **Ambiguous lifecycle ownership** | Unclear init/teardown owner leaks resources or double-frees. | Every subsystem names one owner in its DoD entry; Composition Root owns start/stop ordering (Architecture §8). |
| **R29** | **Optional-ST absence causes type-load failure** | A hard reference to a missing ST assembly crashes single-player at load. | Launch with the ST integration assembly removed; assert no `TypeLoadException`/`FileNotFound` and single-player runs. |
| **R30** | **Cleanup responsibilities split across modules** | Partial teardown leaves objects/nav/handlers behind. | One teardown owner per subsystem; PoC P7/B10 assert nothing survives into the next level. |
| **R31** | **Premature abstraction → oversized framework** | Speculative generality slows delivery and adds untested surface. | Enforce the vertical-slice rule (DoD §3): generalize only after a second real use case; review new abstractions for a present consumer. |

## Suggested validation order (do the cheap, high-leverage probes first)

1. Unity version and own AssetBundle load. *(R2)*
2. Original materials and one 2D sprite render correctly. *(R13, R6)*
3. Vanilla Addressables resolution. *(R1)*
4. Collision and A* navigation. *(R3, R4, R5)*
5. Arena teardown. *(R8)*
6. Unity-authored prefab runtime parity. *(R14)*
7. Boss simulation/presentation separation. *(R16)*
8. One networked attack timeline. *(R17)*
9. Snapshot/event duplicate handling. *(R19)*
10. Late join. *(R18)*
11. Full arena + boss multiplayer loop. *(R7, R10, R15, R20)*

Steps 1–6 can be checked in **single-player** with a probe plugin + the PoC room (Phase A), before any
multiplayer or boss work. Steps 7–11 are the network-native boss slice (Phase B) and need a host + client.
