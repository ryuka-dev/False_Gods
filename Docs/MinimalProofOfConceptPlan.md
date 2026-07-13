# 7. Minimal Proof-of-Concept Plan

*A minimal test room that validates the risky mechanics — **not** the full cave arena.* Build this first; the
large square cave arena only makes sense after every check below passes.

The PoC is split into two phases:

- **Phase A — Arena Pipeline PoC** (§7.1–7.5 below): proves the map, materials, collision, navigation, and
  teardown. Mostly single-player, with a final host+client parity check.
- **Phase B — Original Boss Networking Vertical Slice** (§7.6): proves the network-native boss architecture
  with a throwaway test actor. Needs a host + client.

The step ids below (P0–P9, B0–B10) are the same ones [RiskList.md](RiskList.md) orders by risk; the two
documents describe one plan.

---

## Phase A — Arena Pipeline PoC

### 7.1 The test room

- Size: **~20×20 m**, flat.
- **CollisionRoot:** one floor collider + four simple boundary walls + one large central pillar collider — all
  on the game's geometry/nav layer (report 4.2). Nothing else.
- **VisualRoot:** 3–5 vanilla cave visual modules resolved at runtime via Addressables (walls/rocks/pillar
  dressing), plus our own simple ground mesh under test.
- **LightingRoot:** two realtime lights + basic ambient/fog. No baked lightmaps.
- **NavigationRoot:** one working A\* walkable surface over the floor (via `NavmeshPrefab` **or** runtime
  rescan — test both if time permits).
- **GameplayRoot:** `PlayerSpawn`, one `EnemySpawn`.
- **Enemy:** one **ordinary** vanilla enemy (not a boss), host-owned, that should track the player and path
  around the pillar.

### 7.2 Build order (each step gates the next)

| Step | Validates | Depends on |
|---|---|---|
| P0 | BepInEx probe plugin loads; can read `AstarPath.active`, `GameManager.Instance.geometryLayer`, recast params | — |
| P1 | Resolve + instantiate a vanilla cave prefab by Addressables key/GUID at runtime | R1 |

> **P0/P1 — RUN AND PASSED.** `tools/FalseGods.Probe` was run in-game (F10, game 6000.3.6f1, A\* 5.3.8, Gale
> profile `Bossmod开发`). Results:
> - **P0**: geometry/recast layer masks, the full recast agent parameters, graph layout, and the live
>   `NavMeshCleaner` point set all read successfully → transcribed into report 4.2/4.4 and RiskList R3/R5.
>   Design-changing finding: recast rasterizes **meshes, not colliders**, on a mask that differs from
>   `geometryLayer` (report 4.2).
> - **P1**: the game's own room GUIDs resolve (5/5 hits), and one vanilla prefab loaded **and instantiated**
>   with 0 null materials → RiskList **R1 verified**, R6 partly.
>
> The probe is a throwaway (outside `src/`, outside the FG-ARCH rules; see its README). Now that P0/P1 have
> been captured it can be deleted per its README, or kept to re-check R5 inside our own arena at P5.
| P2 | Load our own AssetBundle (built in the game's Unity version) with our ground mesh + layout | R2 |

> **P2 — RUN AND PASSED.** The dedicated Unity project (`FalseGods.Unity/`, pinned to the game's 6000.3.6f1,
> URP 17.3.0 editor-built-in) regenerates the §7.1 room deterministically (`False Gods/Generate PoC Room
> Prefab`; floor/pillar mesh on `Geometry(3)`, boundary-wall colliders on `GeometryNoNavMesh(22)`, per the
> measured §4.2 masks) and builds `falsegods-poc-room.bundle` for StandaloneWindows64 headlessly
> (`PocBundleBuilder.BuildFromBatchMode`, exit code 0). The probe's P2 section (v0.2.0, run in-game
> 2026-07-11, Act_01_Caves) loaded that bundle with `AssetBundle.LoadFromFileAsync`, instantiated `PocRoom`
> under an inactive holder, and found both authored meshes intact, **0 null materials**,
> `Universal Render Pipeline/Lit` reporting `isSupported = yes`, and all six collider layers correct →
> RiskList **R2 verified**. Rendering correctness (no pink) is deliberately left to P3, judged with the
> instance visible. Operational trap, fixed: a stale probe DLL from the P0/P1 run (same GUID **and version**)
> made BepInEx skip the rebuilt plugin — the probe version is now bumped per change (0.2.0).
| P3 | Vanilla prefab **renders correctly** (no pink) under our lighting; test one vanilla floor material on our ground mesh | R6, R13, report 3.4 |

> **P3 — RUN, vanilla path PASSED (probe `VisualProbe`, F11, in-game 2026-07-12).** The room's `LightingRoot`
> carries two realtime lights (`PocRoomGenerator.BuildLighting`; directional key + point fill, `Realtime`, no
> baked lightmaps per report 3.3), and the probe's visible section shows our lit room + a vanilla prefab
> (`CaveGrubGrub`, MonoBehaviours stripped while inactive so only meshes/materials render) and lays one vanilla
> floor material on our flat floor. Measured with eyes on screen:
> - **Vanilla prefab renders correctly, no pink**, under our `LightingRoot` (all shaders `supported`) → R6
>   vanilla path verified.
> - **A vanilla floor material (`CaveFloor`) sits correctly on our own flat mesh** — the projection/UV-tolerant
>   ideal case of report §3.4 → R13 floor strategy: reuse vanilla floor materials directly.
> - **Our own `URP/Lit` bundle materials went pink** (the pillar) — the predicted variant-stripping (report
>   §3.2/§3.8). Measured: `Shader.Find("Universal Render Pipeline/Lit")` **misses** — the game has no resident
>   stock URP/Lit to adopt (all vanilla content uses `Shader Graphs/*`). Working fixes: reuse a vanilla
>   material (proven), or a `ShaderVariantCollection` for original shaders. The probe dresses our meshes
>   with a borrowed vanilla material (`VisualFixOurMaterials`, 0.5.0) to demonstrate it — **confirmed in-game
>   (F11, 2026-07-12): floor and pillar wear the borrowed `CaveFloor` material, no longer pink.** Recorded in
>   RiskList R6/R13 and report §3.6.
| P4 | Arena colliders behave (player walks, no snagging on decoration) | R3 |

> **P4 — RUN AND PASSED (probe collision check, F9, in-game 2026-07-12).** The arena's four boundary walls
> (`GeometryNoNavMesh(22)`) seal it by design — which is exactly why P3's 18 m stage could only be entered with
> F3/noclip — so P4 places the room *around* the player (its `PlayerSpawn` marker, §7.1 local (-7,0,-7), under
> the feet) rather than teleporting the CMF-controlled player, whose position-set path is not in our decompiled
> reference. Judged on foot: the player stands on our floor (no clip, no float), the central pillar blocks, the
> four walls contain, and there is no snagging on edges or corners. Our box colliders on `Geometry(3)`
> (floor/pillar) and `GeometryNoNavMesh(22)` (walls) are solid to the player → RiskList **R3** player-collision
> half verified. Nav *walkability* of that floor is P5; enemy pathing around the pillar is P6.
| P5 | A\* nav works: bake `NavmeshPrefab` + `Apply()` **or** rescan; confirm floor walkable (watch `NavMeshCleaner`) | R4, R5 |

> **P5 — RUN; runtime rescan (Option 2) RULED OUT, prebaked `NavmeshPrefab` (Option 1) is the path (probe, 2026-07-12).**
> The probe spawns our room as an isolated island and re-bakes nav. Reading the game first settled the mechanism:
> `NavMeshManager.BakeNavMesh` = `AstarPath.Scan()`; `BuildNavMeshNode` prefers `NavmeshPrefab.Apply()` and only
> `ScanAsync`s when there is none; `UpdateGraphs(bounds)` (what `MetalGate` uses) only edits existing node
> walkability and never rasterizes new geometry. With that understood, **no runtime bake put our floor into the
> navmesh** — not `UpdateGraphs`, not a driven `ScanAsync`, not the synchronous `Scan()` — even with the floor on
> `Geometry(3)`, `isReadable=true`, `collectionMode=Layers`, and a `RecastNavmeshModifier` (`AlwaysInclude`)
> attached (decompiled A\*: `CollectRecastNavmeshModifiers` runs in both Layers/Tags modes). Each run left our
> floor ~6 m from the nearest node, 0 walkable island nodes, while `Scan()` did re-rasterize the level and an
> anchor point flipped level areas walkable. In-game corroboration: an enemy dropped on the ground-level arena
> only pathed because it rode the level's own nav; placed far from any level nav, it could not path to our arena.
> **R5's cleaner mechanism is reconfirmed** (points keep areas), but **our floor's own walkability (R4) needs a
> prebaked `NavmeshPrefab` in the bundle** — `NavmeshPrefab.Apply()` from a mod is the untested next step.
>
> **Faithful-sequence re-test (probe 0.13.0, 2026-07-12):** replicating `BuildNavMeshNode` exactly
> (`recastGraph.SnapBoundsToScene()` then `Scan()`) still gave 0 nodes on our floor and ruled out two causes —
> **bounds** (the floor was already inside `forcedBounds`; the snap changed nothing) and **collection** (the
> floor is `isReadable=true`, so it passes A\*'s `ConvertMeshToGatheredMesh` and is gathered). The 0-nodes result
> is a rasterization/walkability failure, not a collection failure; the path to Option 1 is unchanged and is the
> determinism-correct choice for host+client parity anyway (see RiskList R4).
>
> **P5b/P5c — Option 1 plumbing works, but our floor mesh had inverted winding (probes 0.15.0–0.17.0, F7/F6,
> 2026-07-12).** The `NavmeshPrefab.Scan` + `ReplaceTiles` path works from a mod, but P5b's "our floor is
> walkable" was a **false positive** — that arena sat at ground level, so the nodes it applied were *level*
> geometry, not ours. An isolated bake in clear space (+300 m, P5c) baked our floor to **0 triangles**, and a
> side-by-side test showed the top-face geometric normal points DOWN (`(0,-1,0)`): `BuildBoxMesh` wound the box
> inside-out, and recast reads walkability from the winding, not the vertex normals (so it still rendered). A
> winding-reversed copy baked fine (`(0,1,0)`, 2 tris). **Fixed the winding and rebuilt the bundle:** the arena
> now bakes 16 triangles and P5c wrote `arena-nav-PocRoom-cell0.30.bytes`. **P5d (F5) closed the loop:** loading
> that saved artifact and applying it (`Deserialize` + `SnapToGraph` + `ReplaceTiles`) took the floated arena's
> floor from 0 → walkable, and it survived without a `NavMeshCleaner` anchor (the tile-replace path side-steps
> the cleaner — R5). **P5 (R4/R5) is resolved:** Option 1 works end-to-end from a mod; remaining is build-work
> (bytes into the bundle, arena controller, one bake per environment `cellSize`). See RiskList R4/R5.
| P6 | The ordinary enemy tracks the player and **paths around the pillar** | P4, P5, R9 |

> **P6 — RUN AND PASSED (probe P6, F4, 0.23.0, in-game 2026-07-13).** With our baked navmesh applied to a
> floated, isolated copy of the arena (P5c bake in clear space + P5d `ReplaceTiles`, on a graph-tile-aligned
> footprint), two checks passed across **10 environments** (Act_01 Castle/Sewers/Shanty/Caves, Act_02
> Bridge/Forest/Fortress, Act_03 Desert/EndChurch) with **no** non-passing verdict:
> - **Nav-graph proof:** an `ABPath` between the EnemySpawn (7,7) and PlayerSpawn (-7,-7) corners — whose
>   straight line crosses the central 2×2 pillar — **routes AROUND it** (path complete, ~12 waypoints, closest
>   approach ~2.8 m vs the 1.0 m pillar footprint, length ~21.5 m vs ~19.9 m straight).
> - **Live enemy:** a real vanilla `Npc` (`HellshrewSticka`), spawned and activated the game's own way
>   (`UnitId.GetAsset()` → `FetchAndLoadUnitLoader()` → `SetStats` → `Spawn()` → register in `GameManager.npcs`
>   → `ActivateBehaviour`, driven by `Npc.SetForcedDestination` with the behaviour tree off), **walks ~16 m
>   around the pillar** to the far corner, staying ~1 m clear → `TRACKS + ROUTES AROUND`.
>
> So plain `CustomRichAI` pathing works on our flat arena floor with the pillar as a nav hole (RiskList R9,
> ordinary-enemy half). The road there surfaced three reusable facts, all recorded in R9: spawn the NPC
> **active** so `AiAgent.Awake` runs before `Spawn()`; drive the physics→nav handoff via
> `Npc.SetNavAndDisablePhysics`; and **tile-align** the nav bounds, or `NavmeshPrefab.Apply`'s
> `SnapToGraph`/`Scan` origin mismatch shifts the floor (it left half the floor unwalkable on the 38.4 m-tile
> boss levels until fixed). A **big / special-movement** boss (raycast-ground-follow) is deferred to Phase B.
| P7 | **Teardown**: leave the room and *keep playing the same level* — vanilla NPCs still path, no arena objects or nav nodes remain; then load a normal level and assert handles released and its nav is correct | R8, R30 |

> **P7 — RUN AND PASSED (probe P7, `=` key, 0.24.2, in-game 2026-07-13, Act_03_Desert).** Teardown leaves the
> level we stay in clean. One run floats our arena onto a footprint tile the player is standing on (so it carries
> the level's own ground nav — the honest worst case) and measures three stages:
> - **BASELINE** — 64 walkable level-ground nodes in the footprint (whole-graph 2704). The level tiles there are
>   snapshotted: geometry (`TileMeshes`) **and** per-node `Walkable` flags.
> - **APPLIED** — `ReplaceTiles(arena)` makes our floor walkable (16 nodes at +3 m) and **clobbers the level
>   ground in that tile, 64 → 0** — the R8 hazard, measured. A `ClearTiles`-only teardown (all A\*'s own
>   `NavmeshPrefab.OnDisable` does) would leave that hole.
> - **RESTORED** — `ReplaceTiles(saved level tiles)` **+ reapply the saved `Walkable` flags** returns the
>   footprint to **exactly baseline** (level-ground 64 → 64, arena-band 0 → 0, whole-graph 2704 → 2690 within
>   tolerance), with **0 arena GameObjects** and the bundle unloaded → `R8/R30 verdict (same level) = CLEAN`.
>
> The walkability step is the iteration's real finding. `RecastGraph.ReplaceTiles`/`ClearTiles` operate on whole
> XZ tiles across the full Y column, so floating the arena +3 m over the player and applying it **destroys the
> level's own ground nav in that 38.4 m tile**; and `ReplaceTiles` rebuilds nodes walkable-by-geometry and does
> **not** re-run the `NavMeshCleaner` flood-fill (the same side-step P5d relied on at R5), so restoring geometry
> alone *over-restores* — the first run measured footprint 25 → 131. Snapshotting and reapplying the per-node
> `Walkable` flags fixes it exactly. **Cross-level half:** the game rebuilds `AstarPath.active` per level (a fresh
> instance each load — P0), so residue cannot cross a level change; confirmed by F10 on the next level. So the
> real arena controller's teardown owner must **snapshot + restore the overwritten tiles (geometry + walkability)**,
> not merely clear them (RiskList R8/R30). A big/special-boss teardown (B10) stays in Phase B.
| P8 | **Single-player** full loop: enter → ready-gate resolves for the single local peer → fight the dummy enemy → leave, all stable; runtime hierarchy matches the authored manifest; the canonical `ContentHash` is stable across two loads with different Addressables completion order | P1–P7, R14, R34 |

> **P8 — RUN AND PASSED (probe P8, `-` key, 0.25.0, in-game 2026-07-13, Act_03_Desert).** The first probe that
> runs our production content code (`FalseGods.Protocol`) inside the game. `P8 verdict = FULL LOOP OK`:
> - **R34** — the shipped `arena-content-PocRoom.artifact` (2451 bytes; 7 nodes / 6 colliders / 1 nav / 2 spawns /
>   14 parity) recomputes **in-game** through the deployed `Protocol.dll` to
>   `dbed0d2a…221c5acf` — byte-identical to the golden hash the offline fixture pins — and reversing every
>   authored list (a stand-in for a different Addressables completion order) does **not** move it.
> - **R14** — the arena is loaded from the bundle and **all 14** authored parity nodes are found by path with the
>   authored **local** transform (`14/14 MATCH`). The realized arena is the arena the hash was computed over.
> - **Ready gate** — the single-peer `LocalReadyGate` is fail-closed before ready, rejects a ready from an unknown
>   peer, resolves once content is validated, and a two-peer gate with one member ready still waits.
> - **Fight + leave (reused)** — P6 ran on solid applied nav (`ROUTES AROUND`, closest approach 4.20 m vs the
>   1.0 m pillar; the live `HellshrewSticka` walked 16.5 m around the pillar to within 3.5 m of the far corner),
>   then P7 restored the level to baseline, and **no `FalseGods*` object survived** the whole loop.
>
> One honest caveat: the **P7-inside-P8** teardown floated onto a footprint tile with **0 level-ground nodes**
> (the player was above level nav for that sub-check), so the R8 *clobber* demonstration was the trivial case
> there — but that hard case is already VERIFIED standalone (P7 run, footprint 64 → 0 → 64), and P8's own
> teardown still restored to exactly baseline (whole-graph 2664 → 2664) with zero residue. The live-enemy line
> is best-effort and the P8 verdict does not depend on it; here it passed anyway.
| P9 | **Host+client**: both load the identical room and exchange `(ContentHashSchemaVersion, ContentHash)` in `ArenaReady`; the two machines produce **byte-identical** hashes; the gate blocks seal/teleport until both match; an NPC wakes for a client who enters first while the host is far away; a forced hash mismatch, schema mismatch, or timeout **aborts** instead of starting | R7, R10, R33, R34, report 5 |

> **P9 — RUN AND PASSED (probe P9, `[`, 0.26.0, two instances 2026-07-13; host + Sandboxie-isolated client).**
> The first probe to run over SULFUR Together's **public bridge** (`SULFURTogether.Api.NetExternalChannel` /
> `NetSessionInfo`, built this session on ST branch `feature/external-mod-channel` — no reflection into ST). Both
> instances registered the opaque channel; the host saw `role=Host, peers=[host(local), client-1]`, broadcast
> `EnterArena`, and each peer recomputed its own `ContentHash` through the deployed `FalseGods.Protocol`. All four
> fail-closed scenarios (client `P9ClientMode`) landed exactly:
> - **Normal** → both computed byte-identical `dbed0d2a644fb8a2…` (= P8's in-game digest = the golden fixture); the
>   gate resolved **2/2**; `PARITY OK` (the FG-owned notional seal would fire). **R7 / R33 / R34 cross-instance.**
> - **ForceHashMismatch** → client sent `24ed0d2a…` (byte 0 flipped); host `ABORT ContentMismatch`.
> - **ForceSchemaMismatch** → client sent schema 8; host `ABORT ContentHashSchemaMismatch` — hashes **not** compared.
> - **StaySilent** → client sent nothing; host `ABORT Timeout`. Nothing sealed/teleported/started in any abort.
>
> In-game `P9ClientMode` changes took effect on the client with **no restart**; zero exceptions either end. **Two
> honest limits:** (1) host + client were two processes on **one** machine (Sandboxie isolation), not two physical
> PCs — but the canonical hash is defined over authored data with no machine/GPU input, so this proves the
> cross-process determinism the gate needs; (2) **the NPC-activation half of P9 (R10) is NOT covered** — it needs
> ST's remote-activation/position surface, a deferred "channel + session only" bridge ask
> ([ADR-004](ADRs/ADR-004-Optional-Sulfur-Together-Adapter.md)). This probe proves the channel + session identity
> + cross-instance hash parity + fail-closed aborts the bridge enables.

### 7.3 Pass/fail criteria (the request's acceptance list)

- ✅ Vanilla assets load at runtime from the player's install (no redistribution).
- ✅ Materials display correctly (no pink; lighting from our `LightingRoot`).
- ✅ Collision is correct (players/enemy don't clip walls or snag on decoration).
- ✅ The ordinary enemy tracks the player and navigates around the pillar (A\* works on our geometry).
- ✅ Host and client see the **same** room (`ContentHash` matches byte-for-byte across two machines; an NPC wakes
  for a client who enters first — which needs the activation port, not just roster registration).
- ✅ A deliberate content mismatch, schema mismatch, or a stalled peer **aborts** the encounter; nothing seals,
  teleports, or spawns.
- ✅ On exit, all arena objects **and** the arena's nav contributions are cleaned out of the *active* level, and
  the next level is unaffected.

### 7.4 Explicitly out of scope for the PoC
- The full large square cave arena.
- The original boss — only an ordinary enemy is tested here. (The boss is an original `FalseGods.Core`
  `BossSimulation`, **not** a `BossFightHelper`/`BossPhase` subclass; those vanilla types are references only.)
- Procedural / random arena assembly (fixed room only).
- Phase-changing terrain, destructibles, mechanisms.

### 7.5 Known verification limits (report honestly)
- Runtime rendering/nav/teardown claims are **unverified** until P0–P8 actually run in-game; this document is
  a plan, not a result.
- Multiplayer parity/activation (P9) requires **two game instances** (host + client). Full boss-authority
  parity cannot be tested until a boss exists — deferred to Phase B below.

---

## Phase B — Original Boss Networking Vertical Slice (§7.6)

This is not the final first boss. It is a temporary test actor proving the False Gods boss architecture.

**Follow the vertical-slice order** ([DefinitionOfDone.md §3](DefinitionOfDone.md)): establish the minimum
module skeleton (`FalseGods.Core` / `.Protocol` / `.RuntimeContracts` / `.Application` / `.UnityRuntime` /
`.Plugin` + `Integration.Sulfur`, per [Architecture.md](Architecture.md)) — **done** (the reference graph;
`FalseGods.Protocol` also carries the Phase A arena content/`ContentHash`, [ArchitectureEnforcement.md
§13](ArchitectureEnforcement.md)) → arena PoC (Phase A, **done**, §7.1–7.5) → one temporary
`BossSimulation` in Core (**done: `FalseGods.Core/Bosses` + `Simulation` ports, unit-tested in
`FalseGods.CoreTests`; not yet wired to a host or presentation**) → one `BossPresentation` in UnityRuntime, driven through
`PresentationState`/`PresentationEvent` → single-player → transport-neutral snapshots/events in Protocol plus
the Application mapper → connect through the **optional, separately-loaded**
`FalseGods.Integration.SulfurTogether` (a companion plugin that self-registers through `FalseGodsIntegrations`,
never referenced by `FalseGods.Plugin`)
→ host/client validation. Extract shared abstractions **only** from demonstrated repetition — do not build a
universal boss framework up front. The three layers below map to `BossSimulation` (Core), `BossPresentation`
(UnityRuntime), and `BossReplication` (Application over the adapter's `IEncounterChannel`).

### 7.6.1 Test boss

Use either:

- a simple cube;
- or a temporary billboarded 2D image.

It must have:

- idle;
- simple host-authoritative movement;
- one aimed projectile attack;
- one area telegraph attack;
- two phases;
- one weak-point or stagger state;
- death;
- arena completion.

### 7.6.2 Required architecture

- `BossSimulation` (Core), `ArenaSimulation` (Core), `EncounterCoordinator` (Core)
- `BossPresentation` (UnityRuntime), fed only `PresentationState` / `PresentationEvent`
- `BossReplication` (Application) over `IEncounterChannel`
- stable `EncounterId`, `BossInstanceId`, `AttackInstanceId`
- host simulation tick/time
- `BossSnapshot` + `ArenaSnapshot` (unreliable correction)
- `BossEvent` + `ArenaEvent` (reliable, sequenced, one sequence space each)
- `EncounterBaseline` composing boss + arena + encounter state
- per-stream duplicate suppression
- join-in-progress restoration from one baseline

### 7.6.3 Test sequence

- **B0.** Run the test boss in single-player with SULFUR Together **and the ST adapter plugin** absent — no
  `TypeLoadException`, no `FileNotFoundException`. Then install both and confirm the adapter registers through
  `FalseGodsIntegrations` **exactly once**, that a second registration attempt is rejected rather than
  replacing the first, and that disposing the registration token returns the game to the single-player
  composition (ADR-004, RiskList R35).
- **B1.** Host and client load the arena and agree on boss definition and protocol version. Assert
  `BossPresentation`'s public surface names no `FalseGods.Protocol` type.
- **B2.** Host selects an attack; both peers show the same `AttackInstanceId`.
- **B3.** Telegraph begins from host simulation time on both machines.
- **B4.** Host commits the projectile/area attack; client presentation does not decide damage.
- **B5.** Drop several continuous state packets; the discrete attack still completes exactly once.
- **B6.** Deliver one duplicate reliable event **on each stream**; no duplicate projectile, mechanism, or damage
  occurs, and a dropped `ArenaEvent` does not stall the boss stream.
- **B7.** Enter phase 2; the coordinator drives the arena's phase-2 mechanism group; all peers agree on boss
  phase and arena mechanism state.
- **B8.** Start a client during phase 2; it reconstructs the current boss, attack, weak point, **and** arena
  state from **one** `EncounterBaseline`, then resumes both event streams from the sequences it carries.
- **B9.** Kill the boss; death, reward, unlock, and teardown occur exactly once.
- **B10.** Leave the arena and keep playing the current level, then return to a normal level; verify no boss,
  arena, event subscription, or navigation state survives either transition.

### 7.6.4 Pass criteria

- Single-player and host use the same simulation rules and the same presentation entry point.
- The client never executes authoritative AI or damage, and presentation never touches a wire DTO.
- Phase, attack, and death events happen exactly once.
- Visual timing is tied to host simulation time.
- A late join reconstructs boss, arena, and encounter state from one `EncounterBaseline`.
- Packet duplication and snapshot loss do not duplicate gameplay, on either stream.
- The boss never starts unless the ready gate passed for every required peer.
- The complete arena and boss teardown is clean.

> Everything in Phase B is **proposed / unverified** until it runs in-game with a host + client; this is a
> test plan, not a result. The architecture it exercises is defined in
> [OriginalBossNetworkingArchitecture.md](OriginalBossNetworkingArchitecture.md).
