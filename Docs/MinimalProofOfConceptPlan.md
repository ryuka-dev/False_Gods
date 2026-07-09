# 7. Minimal Proof-of-Concept Plan

*A minimal test room that validates the risky mechanics — **not** the full cave arena.* Build this first; the
large square cave arena only makes sense after every check below passes.

The PoC is split into two phases:

- **Phase A — Arena Pipeline PoC** (§7.1–7.5 below): proves the map, materials, collision, navigation, and
  teardown. Mostly single-player, with a final host+client parity check.
- **Phase B — Original Boss Networking Vertical Slice** (§7.6): proves the network-native boss architecture
  with a throwaway test actor. Needs a host + client.

---

# Phase A — Arena Pipeline PoC

## 7.1 The test room

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

## 7.2 Build order (each step gates the next)

| Step | Validates | Depends on |
|---|---|---|
| P0 | BepInEx probe plugin loads; can read `AstarPath.active`, `GameManager.Instance.geometryLayer`, recast params | — |
| P1 | Resolve + instantiate a vanilla cave prefab by Addressables key/GUID at runtime | R1 |
| P2 | Load our own AssetBundle (built in the game's Unity version) with our ground mesh + layout | R2 |
| P3 | Vanilla prefab **renders correctly** (no pink) under our lighting; test one vanilla floor material on our ground mesh | R6, report 3.4 |
| P4 | Arena colliders behave (player walks, no snagging on decoration) | R3 |
| P5 | A\* nav works: bake `NavmeshPrefab` + `Apply()` **or** rescan; confirm floor walkable (watch `NavMeshCleaner`) | R4, R5 |
| P6 | The ordinary enemy tracks the player and **paths around the pillar** | P4, P5 |
| P7 | **Teardown**: leave the room → load a normal level; assert no leftovers, nav clean, handles released | R8 |
| P8 | **Single-player** full loop: enter → fight the dummy enemy → leave, all stable | P1–P7 |
| P9 | **SULFUR Together host+client**: both load the identical room (compare an arena hash), enemy activates for the client, both see the same layout; leave cleanly | R7, R10, report 5 |

## 7.3 Pass/fail criteria (the request's acceptance list)

- ✅ Vanilla assets load at runtime from the player's install (no redistribution).
- ✅ Materials display correctly (no pink; lighting from our `LightingRoot`).
- ✅ Collision is correct (players/enemy don't clip walls or snag on decoration).
- ✅ The ordinary enemy tracks the player and navigates around the pillar (A\* works on our geometry).
- ✅ Host and client see the **same** room (parity hash matches; enemy activates for a client who enters first).
- ✅ On exit, all arena objects **and** nav data are fully cleaned up; the next level is unaffected.

## 7.4 Explicitly out of scope for the PoC
- The full large square cave arena.
- The original boss (`BossFightHelper`/`BossPhase` subclass, adapter) — only an ordinary enemy is tested here.
- Procedural / random arena assembly (fixed room only).
- Phase-changing terrain, destructibles, mechanisms.

## 7.5 Known verification limits (report honestly)
- Runtime rendering/nav/teardown claims are **unverified** until P0–P8 actually run in-game; this document is
  a plan, not a result.
- Multiplayer parity/activation (P9) requires **two game instances** (host + client). Full boss-authority
  parity cannot be tested until a boss exists — deferred to Phase B below.

---

# Phase B — Original Boss Networking Vertical Slice

This is not the final first boss. It is a temporary test actor proving the False Gods boss architecture.

## 7.6.1 Test boss

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

## 7.6.2 Required architecture

- `BossSimulation`
- `BossPresentation`
- `BossReplication`
- stable boss instance id
- stable attack instance id
- host simulation tick/time
- full baseline snapshot
- reliable discrete events
- unreliable movement/state correction
- duplicate suppression
- join-in-progress restoration

## 7.6.3 Test sequence

- **B0.** Run the test boss in single-player without SULFUR Together.
- **B1.** Host and client load the arena and receive the same boss definition/protocol version.
- **B2.** Host selects an attack; both peers show the same `AttackInstanceId`.
- **B3.** Telegraph begins from host simulation time on both machines.
- **B4.** Host commits the projectile/area attack; client presentation does not decide damage.
- **B5.** Drop several continuous state packets; the discrete attack still completes exactly once.
- **B6.** Deliver one duplicate reliable event; no duplicate projectile, mechanism, or damage occurs.
- **B7.** Enter phase 2; all peers agree on phase and current state.
- **B8.** Start a client during phase 2; it reconstructs the current boss, attack, weak point, and arena state
  from a baseline snapshot.
- **B9.** Kill the boss; death, reward, unlock, and teardown occur exactly once.
- **B10.** Return to a normal level and verify no boss, arena, event subscription, or navigation state
  survives.

## 7.6.4 Pass criteria

- Single-player and host use the same simulation rules.
- The client never executes authoritative AI or damage.
- Phase, attack, and death events happen exactly once.
- Visual timing is tied to host simulation time.
- A late join reconstructs the current encounter.
- Packet duplication and snapshot loss do not duplicate gameplay.
- The complete arena and boss teardown is clean.

> Everything in Phase B is **proposed / unverified** until it runs in-game with a host + client; this is a
> test plan, not a result. The architecture it exercises is defined in
> [OriginalBossNetworkingArchitecture.md](OriginalBossNetworkingArchitecture.md).
