# 5. Multiplayer Loading Contract

*Host/Client responsibilities for arena load → ready-gate → fight start → phase changes → exit.* Two guiding
principles: **reuse SULFUR Together's transport/session/arena spine** (do not add new transport or authority),
and **design original bosses as network-native encounters** rather than forcing them into the vanilla-boss
compatibility model. The deep boss model lives in
[OriginalBossNetworkingArchitecture.md](OriginalBossNetworkingArchitecture.md); this report covers the
loading/arena contract and how the boss plugs into it.

## 5.1 What SULFUR Together already provides (reuse targets)

| Concern | Existing system (SULFUR Together) | Reuse for the arena |
|---|---|---|
| Transport | LiteNetLib (+ Steam P2P loopback), host-authoritative | Ride the same layer; add a small versioned message family only if the generic envelope can't carry it. |
| "Which level + seed" | Host owns it. `HostSceneRequest(11)`/`ClientSceneAck(12)`, deterministic gen, `NetGenerationInputCapture` (`SceneTransitionAndLinkState.md`) | The host decides when everyone enters the arena and with what identity. |
| Level content agreement | **`NetLevelManifest`** — host's generation-result summary (seed, rooms, units, `GenerationHash`) the client diffs against its local world | Model `ArenaId`/`ArenaVersion` on this; for a fixed arena it's a tiny manifest. |
| Boss fight | Adapter pattern: `IBossEncounterAdapter`, `NetBossEncounterManager`, `BossReflect`; msgs **28–43**; host runs real fight via `Unit.ReceiveDamage`, client mirrors; presentation suppressed by default (`BossAuthority.md`) | **Reference only** — this is a compatibility layer for vanilla bosses. Original bosses use a purpose-built replication protocol (§5.4–5.9); they may reuse the manager/envelope where it fits, but are not required to fit the vanilla adapter's state model. |
| Pre-fight convergence | Room sync / **room sealing** / dialog / teleport (`BossPreFightFlow.md`) | The arena's seal + teleport-in reuse this. |
| Arena sealing | **`ArenaLockdownManager`** (host-authoritative membership + timer + force-seal barrier + teleport), `ArenaBarrierManager`, `ArenaDoorwaySensor`, `NetArenaCommand`, `NetClientArenaEnter` | Our arena boundary/seal reuses this directly. |
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
   ArenaController.LoadAsync: instantiate the authored arena prefab, resolve only its VanillaAssetProxy
   objects (report 2), build/apply A* nav (report 4). Deterministic from ArenaId/Version (+Seed) so all match.

3. READY-GATE
   Each client → host: ArenaReady(ArenaId, ArenaVersion). Host waits for ALL registered players
   (GameManager.Players, incl. headless remote entries) to report ready, with a timeout fallback.
   Reuse the manifest/ack style already used for level readiness rather than inventing a new barrier.

4. SEAL + TELEPORT-IN
   Host authoritative: ArenaLockdownManager seals the arena (barrier) and teleports every player to
   PlayerSpawn (reuse LD-2b/2c seal+teleport + BossPreFightFlow teleport). Clients act on host commands
   against their OWN local player.

5. FIGHT START
   Only after all-ready + sealed, the host starts the boss's BossSimulation (§5.5) and broadcasts a baseline
   snapshot. Client presentation stays non-authoritative (and, when reusing any vanilla presentation path,
   suppressed by default per the invulnerability-clear caveat in BossAuthority.md).
```

## 5.4 Existing boss adapters are references, not a hard constraint

SULFUR Together's current boss adapters solve compatibility with vanilla bosses whose internal simulation
was not designed for networking.

False Gods owns the source and design of its original bosses, so it should not inherit every limitation of
that compatibility layer.

The project should reuse SULFUR Together's transport/session infrastructure, but define a purpose-built
original-boss replication protocol where needed.

## 5.5 Network-native False Gods boss model

A False Gods boss is split into three layers. (Full detail:
[OriginalBossNetworkingArchitecture.md](OriginalBossNetworkingArchitecture.md).)

### BossSimulation

Runs only:

- in vanilla single-player;
- or on the multiplayer host.

Owns:

- phase;
- state-machine transition;
- target selection;
- attack selection;
- authoritative random decisions;
- movement intent;
- damage;
- health;
- weak points;
- summons;
- arena mechanisms;
- death and completion.

### BossPresentation

Runs on every machine.

Owns only:

- renderer/sprite state;
- animation playback;
- particles;
- audio;
- telegraphs;
- camera effects;
- non-authoritative visual projectiles;
- interpolation.

Presentation may never decide damage, phase, death, target, or attack outcome.

### BossReplication

Enabled when SULFUR Together is present.

Owns:

- host snapshots;
- discrete events;
- sequence numbers;
- simulation tick/time;
- interpolation targets;
- duplicate suppression;
- late-join state restoration;
- desync detection;
- recovery snapshots.

## 5.6 During the fight — authority split

**Host (BossSimulation) owns:** boss AI + pathing (report 4.5), target selection, boss position/rotation,
attacks & damage, **phase changes**, arena mechanism state, add spawns, fight start/end, exit unlock.

**Client (BossPresentation) owns / does:** load the identical arena, render visuals, play arena FX,
**interpolate** the boss puppet from host snapshots, update mechanisms/environment from host state. Clients
never compute boss damage/phase/death/target/attack locally — they report intent (e.g. a hit request) and
apply host-authoritative results.

**Phase changes** are host-driven discrete events: host advances the phase (BossSimulation), then broadcasts
the phase transition + the resulting snapshot state; clients apply the visual/mechanism result and never
trigger phases from local HP.

## 5.7 Replicated state & discrete events

Continuous correction (unreliable snapshot):

```
BossReplicatedState {
  BossInstanceId
  DefinitionId
  ProtocolVersion
  SimulationTick
  PhaseId
  StateId
  StateStartTick
  AttackInstanceId
  AttackDefinitionId
  TargetPlayerId
  Position
  Rotation
  Health
  MaxHealth
  WeakPointStates[]
  ArenaMechanismState
  AuthoritativeRandomStateOrSeed
  LastProcessedEventSequence
}
```

Discrete transitions (reliable, sequenced):

```
BossDiscreteEvent {
  BossInstanceId
  Sequence
  SimulationTick
  EventType
  AttackInstanceId
  Payload
}
```

Event types (at least):

- `AttackSelected`
- `TelegraphStarted`
- `AttackCommitted`
- `ProjectileSpawned`
- `PhaseChanged`
- `WeakPointChanged`
- `AddSpawned`
- `ArenaMechanismChanged`
- `BossStaggered`
- `BossDefeated`

## 5.8 Synchronization principles

- Attack selection is performed once by the host.
- Random choices are never independently rolled by clients.
- An attack instance has a stable `AttackInstanceId`.
- Repeated packets must not repeat the attack.
- Visual playback is aligned to host simulation time.
- Damage remains host authoritative.
- Phase changes are host discrete events plus snapshot state.
- Reliable events carry transitions; unreliable snapshots carry continuous correction.
- A client joining mid-fight receives a full baseline snapshot before normal event processing.

## 5.9 Synchronization invariants

1. All peers agree on `BossInstanceId`, `PhaseId`, `StateId`, and `AttackInstanceId`.
2. A phase transition occurs exactly once.
3. A boss death occurs exactly once.
4. Clients cannot trigger authoritative damage or death.
5. Duplicate reliable messages cannot duplicate projectiles, adds, rewards, or mechanisms.
6. A late-joining client can reconstruct the current visual state from one baseline snapshot.
7. Packet loss of continuous snapshots does not cancel or repeat discrete attacks.
8. Host and clients use the same arena mechanism state.
9. Client animation delay remains bounded and is corrected from host simulation time.
10. Single-player uses the same `BossSimulation` rules without requiring SULFUR Together.

## 5.10 Message strategy

Do not pre-commit to "no new boss message ids."

First determine whether the existing generic message envelope can carry False Gods snapshots/events cleanly.
If it can, reuse the envelope and add False Gods codecs/payloads.

If it cannot, add a small versioned message family dedicated to original bosses. Avoid one-off message types
per individual boss.

Arena-loading messages stay minimal and follow SULFUR Together's `Net*` + `Net*Codec` + `Register` pattern
(`NetworkingArchitecture.md`): at most `EnterArena` (host→client, reliable-ordered) and `ArenaReady`
(client→host, reliable-ordered), with ids appended after the existing 46.

## 5.11 Exit & teardown (synchronized)

- Host decides the fight is over (`BossDefeated`) → unlocks the exit / drops the barrier
  (`ArenaLockdownManager` release path, e.g. AllDead → gate Open) → tells clients.
- Each end runs `ArenaController.Unload()` locally (report 4.6): destroy roots, release Addressables, restore
  nav, and unsubscribe all boss replication/event handlers. The **host's normal level transition** (which
  already rescans nav and is already gated/relayed by SULFUR Together) is the natural carrier for leaving the
  arena, so seed/scene sync stays consistent.
- Personal loot/inventory stay client-owned (CLAUDE.md §16); only shared-world drops follow the existing
  world-drop path (`WorldItemDrop.md`).

## 5.12 Verification (needs two machines/instances — state this limit)

Real multiplayer verification requires a host + client (two game instances). The Arena Pipeline PoC (report 7,
Phase A) covers a host+client arena-load parity check and a boss-less enemy-sync check. Full network-native
boss parity — sim/presentation separation, host-time attack timelines, duplicate/loss handling, and
join-in-progress — is validated by the Original Boss Networking Vertical Slice (report 7, Phase B) once a test
boss exists.
