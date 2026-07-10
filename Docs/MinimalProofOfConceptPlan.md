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

> **P2 — TOOLING IN PLACE, BUNDLE BUILT; the in-game run is still open.** The dedicated Unity project
> (`FalseGods.Unity/`, pinned to the game's 6000.3.6f1, URP 17.3.0 editor-built-in) regenerates the §7.1 room
> deterministically (`False Gods/Generate PoC Room Prefab`; floor/pillar mesh on `Geometry(3)`, boundary-wall
> colliders on `GeometryNoNavMesh(22)`, per the measured §4.2 masks) and builds `falsegods-poc-room.bundle`
> for StandaloneWindows64 (`False Gods/Build PoC AssetBundle`, or headless via
> `PocBundleBuilder.BuildFromBatchMode`). The probe gained a P2 section that loads the bundle from
> `BepInEx/FalseGods.Probe/`, instantiates the prefab under an inactive holder, and checks
> meshes/materials/collider layers before unloading (see `tools/FalseGods.Probe/README.md`).
> **R2 stays unverified until that report has run in-game** — building the bundle in the right editor is the
> setup, not the result.
| P3 | Vanilla prefab **renders correctly** (no pink) under our lighting; test one vanilla floor material on our ground mesh | R6, R13, report 3.4 |
| P4 | Arena colliders behave (player walks, no snagging on decoration) | R3 |
| P5 | A\* nav works: bake `NavmeshPrefab` + `Apply()` **or** rescan; confirm floor walkable (watch `NavMeshCleaner`) | R4, R5 |
| P6 | The ordinary enemy tracks the player and **paths around the pillar** | P4, P5, R9 |
| P7 | **Teardown**: leave the room and *keep playing the same level* — vanilla NPCs still path, no arena objects or nav nodes remain; then load a normal level and assert handles released and its nav is correct | R8, R30 |
| P8 | **Single-player** full loop: enter → ready-gate resolves for the single local peer → fight the dummy enemy → leave, all stable; runtime hierarchy matches the authored manifest; the canonical `ContentHash` is stable across two loads with different Addressables completion order | P1–P7, R14, R34 |
| P9 | **Host+client**: both load the identical room and exchange `(ContentHashSchemaVersion, ContentHash)` in `ArenaReady`; the two machines produce **byte-identical** hashes; the gate blocks seal/teleport until both match; an NPC wakes for a client who enters first while the host is far away; a forced hash mismatch, schema mismatch, or timeout **aborts** instead of starting | R7, R10, R33, R34, report 5 |

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
`.Plugin` + `Integration.Sulfur`, per [Architecture.md](Architecture.md)) — **done: `src/`, project reference
lists only, no source files** ([ArchitectureEnforcement.md §13](ArchitectureEnforcement.md)) → arena PoC
(Phase A) → one temporary
`BossSimulation` in Core → one `BossPresentation` in UnityRuntime, driven through
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
