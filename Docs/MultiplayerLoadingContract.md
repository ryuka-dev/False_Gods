# 5. Multiplayer Loading Contract

*Host/Client responsibilities for arena load → ready-gate → fight start → phase changes → exit, mapped onto
SULFUR Together's existing host-authoritative systems.* The guiding principle: **reuse SULFUR Together's
spine; do not add new transport or new authority.**

## 5.1 What SULFUR Together already provides (reuse targets)

| Concern | Existing system (SULFUR Together) | Reuse for the arena |
|---|---|---|
| Transport | LiteNetLib (+ Steam P2P loopback), host-authoritative | Ride the same layer; add a couple of typed arena messages if needed. |
| "Which level + seed" | Host owns it. `HostSceneRequest(11)`/`ClientSceneAck(12)`, deterministic gen, `NetGenerationInputCapture` (`SceneTransitionAndLinkState.md`) | The host decides when everyone enters the arena and with what identity. |
| Level content agreement | **`NetLevelManifest`** — host's generation-result summary (seed, rooms, units, `GenerationHash`) the client diffs against its local world | Model `ArenaId`/`ArenaVersion` on this; for a fixed arena it's a tiny manifest. |
| Boss fight | Adapter pattern: `IBossEncounterAdapter`, `NetBossEncounterManager`, `BossReflect`; msgs **28–43**; host runs real fight via `Unit.ReceiveDamage`, client mirrors; presentation suppressed by default (`BossAuthority.md`) | Add a `FalseGodsBossAdapter` implementing `IBossEncounterAdapter` for the new boss. |
| Pre-fight convergence | Room sync / **room sealing** / dialog / teleport (`BossPreFightFlow.md`) | The arena's seal + teleport-in reuse this. |
| Arena sealing | **`ArenaLockdownManager`** (host-authoritative membership + timer + force-seal barrier + teleport), `ArenaBarrierManager`, `ArenaDoorwaySensor`, `NetArenaCommand`, `NetClientArenaEnter` | Our arena boundary/seal reuses this instead of a new mechanism. |
| Enemy authority | Host-driven proxy: client enemies are non-autonomous puppets, damage suppressed; `NetRuntimeSpawn`, `NetWorldEntityRoster` (stable `HostSpawnIndex`/HostNetId), `NetGameplayEnemyStateSnapshot` (`HostDrivenProxyPlan.md`) | The boss and any adds are host-owned; clients render puppets. |
| Enemy activation | `NpcUpdateManager.LateUpdate` is host-singleton bound; fix = register remote players in `GameManager.Players` (`EnemyActivationAndPlayersRegistry.md`) | Ensures the boss/adds wake for a client who enters first. |

## 5.2 Arena identity

Define a small, explicit contract (mirrors `NetLevelManifestHeader`):

```
ArenaManifest {
  ArenaId       : string   // stable id of the arena definition ("false_gods.arena.cave01")
  ArenaVersion  : int      // bump when layout/collision/nav/spawns change
  // fixed arena: nothing else required
  // future procedural arena (host-authoritative only):
  Seed          : int
  Modules[]     : { moduleId, position, rotation, scale }   // host-decided final layout
}
```

- **Fixed arena:** `ArenaId` + `ArenaVersion` fully determine the layout. Every client loads the identical
  arena definition shipped in the mod; the host only announces "enter arena X vN". A version mismatch between
  host and client is a hard, explicit refusal (like `ClientSceneRefused`), never a silent divergence
  (CLAUDE.md §11).
- **Procedural arena (later):** the **host** generates the module list and broadcasts it; clients **never**
  generate independently. This mirrors "host owns seed + used-sets" from `SceneTransitionAndLinkState.md`.

## 5.3 The load → ready → start sequence (proposed)

All clients must load the **same** layout, collision, obstacles, spawns, mechanism state, exit, and phase
results before the host starts the boss.

```
1. ENTER
   Host reaches the arena entry (real trigger). Host → clients: EnterArena(ArenaId, ArenaVersion[, Seed, Modules]).
   (Rides the existing scene/level authority; a new NetArenaCommand variant or a small NetArenaEnter message.)

2. LOAD (each end, locally)
   ArenaController.LoadAsync: instantiate roots, resolve+instantiate vanilla Addressables (report 2),
   build/apply A* nav (report 4). This is deterministic from ArenaId/Version (+Seed) so all ends match.

3. READY-GATE
   Each client → host: ArenaReady(ArenaId, ArenaVersion). Host waits for ALL registered players
   (GameManager.Players, incl. headless remote entries) to report ready, with a timeout fallback.
   Reuse the manifest/ack style already used for level readiness rather than inventing a new barrier.

4. SEAL + TELEPORT-IN
   Host authoritative: ArenaLockdownManager seals the arena (barrier) and teleports every player to
   PlayerSpawn (reuse LD-2b/2c seal+teleport + BossPreFightFlow teleport). Clients act on host commands
   against their OWN local player.

5. FIGHT START
   Only after all-ready + sealed, the host starts the boss via the boss adapter
   (NetBossEncounterManager / IBossEncounterAdapter, HostBossEncounterStart(29)). Client presentation stays
   suppressed by default (invulnerability-clear caveat from BossAuthority.md).
```

## 5.4 During the fight — authority split

**Host owns:** boss AI + pathing (report 4.5), target selection, boss position/rotation, attacks & damage
(`Unit.ReceiveDamage`), **phase changes**, arena mechanism state, add spawns (`NetRuntimeSpawn`), fight
start/end, exit unlock.

**Client owns / does:** load the identical arena, render vanilla visuals, play arena FX, **interpolate** the
boss puppet (unreliable position/rotation/animator snapshots), update mechanisms/environment from host state.
Clients never compute boss damage/phase/death locally — they report intent (e.g. `ClientBossHitRequest(34)`)
and apply host-authoritative results (`HostBossState(32)`, `HostBossDiscreteEvent(36)`), exactly as
`BossAuthority.md` prescribes.

**Phase changes** are host-driven discrete events: host advances the phase (its real `BossPhase`), then
broadcasts the phase transition + any arena change (lights, terrain, sealed sub-areas). Clients apply the
visual/mechanism result; they do not trigger phases from local HP.

## 5.5 Exit & teardown (synchronized)

- Host decides the fight is over (boss death event) → unlocks the exit / drops the barrier
  (`ArenaLockdownManager` release path, e.g. AllDead → gate Open) → tells clients.
- Each end runs `ArenaController.Unload()` locally (report 4.6): destroy roots, release Addressables, restore
  nav. The **host's normal level transition** (which already rescans nav and is already gated/relayed by
  SULFUR Together) is the natural carrier for leaving the arena, so seed/scene sync stays consistent.
- Personal loot/inventory stay client-owned (CLAUDE.md §16); only shared-world drops follow the existing
  world-drop path (`WorldItemDrop.md`).

## 5.6 New wire messages (keep minimal)

Prefer extending existing codecs. If new messages are unavoidable, add at most:
`EnterArena` (host→client, reliable-ordered), `ArenaReady` (client→host, reliable-ordered), and a
host→client `ArenaPhaseState` (reliable) — all following SULFUR Together's `Net*` + `Net*Codec` +
`Register` pattern (`NetworkingArchitecture.md`), with new message ids appended after the existing 46. The
boss itself should not need new ids beyond a new adapter, since 28–43 already cover encounter/damage/phase/
spawn.

## 5.7 Verification (needs two machines/instances — state this limit)
Real multiplayer verification requires a host + client (two game instances). The PoC (report 7) covers a
host+client arena-load parity check and a boss-less enemy-sync check; full boss-authority parity is a later
milestone once a boss exists.
