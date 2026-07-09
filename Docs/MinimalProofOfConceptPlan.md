# 7. Minimal Proof-of-Concept Plan

*A minimal test room that validates the risky mechanics — **not** the full cave arena.* Build this first; the
large square cave arena only makes sense after every check below passes.

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
  parity cannot be tested until a boss exists — deferred to a later milestone.
