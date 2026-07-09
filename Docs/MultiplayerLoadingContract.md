# 5. Multiplayer Loading Contract

*Host/Client responsibilities for arena load → ready-gate → fight start → phase changes → exit.* Two guiding
principles: **reuse SULFUR Together's transport/session/arena spine** (do not add new transport or authority),
and **design original bosses as network-native encounters** rather than forcing them into the vanilla-boss
compatibility model. The deep boss model lives in
[OriginalBossNetworkingArchitecture.md](OriginalBossNetworkingArchitecture.md); this report covers the
loading/arena contract and how the boss plugs into it.

## 5.1 What SULFUR Together provides, and the port it is consumed through

**Boundary rule:** these capabilities are consumed **only** through project-owned ports implemented in the
optional `FalseGods.Integration.SulfurTogether` adapter ([Architecture.md](Architecture.md),
[DependencyRules.md](DependencyRules.md)). Boss/arena/protocol/presentation code — and the main flows in §5.3
and §5.6 — never reference SULFUR Together (ST), LiteNetLib, Steam, `CoopConnection`, `NetService`,
`NetArenaCommand`, `GameManager.Players`, `ArenaLockdownManager`, or any concrete ST/game manager. False Gods
must not know which transport ST uses.

**The table below is an existing-systems mapping, not an API surface.** ST type names appear here (and in
adapter implementation notes) and nowhere else. Note also that most of them are `internal` — see §5.1.1.

| Concern | Existing system (SULFUR Together) | Consumed via port (in the ST adapter) |
|---|---|---|
| Transport | LiteNetLib (+ Steam P2P loopback), host-authoritative | `IEncounterChannel` — reliable/unreliable send/receive of `EncodedPayload`; transport is invisible to False Gods. |
| "Which level + seed" | Host owns it. `HostSceneRequest(11)`/`ClientSceneAck(12)`, deterministic gen, `NetGenerationInputCapture` (`SceneTransitionAndLinkState.md`) | `IMultiplayerSession` — host decides when everyone enters the arena and with what identity. |
| Level content agreement | **`NetLevelManifest`** — host's generation-result summary (seed, rooms, units, `GenerationHash`) the client diffs against its local world | Model `ArenaManifest` on the manifest shape (§5.2); delivered via `IEncounterChannel`. |
| Boss fight | Adapter pattern: `IBossEncounterAdapter`, `NetBossEncounterManager`, `BossReflect`; msgs **28–43** (`BossAuthority.md`) | **Reference only** — a vanilla-compat layer. Original bosses use the network-native model (§5.4–5.9); no dependency on the vanilla adapter's state model. |
| Pre-fight convergence | Room sync / **room sealing** / dialog / teleport (`BossPreFightFlow.md`) | `IArenaLockdownPort` — seal + teleport-in. |
| Arena sealing | **`ArenaLockdownManager`**, `ArenaBarrierManager`, `ArenaDoorwaySensor`, `NetArenaCommand`, `NetClientArenaEnter` | `IArenaLockdownPort` — arena boundary/seal/barrier/teleport. |
| Load readiness | **`NetLoadBarrier`** — host-side ACK tracking of `HostSceneRequest` → `ClientSceneAck` | `IEncounterReadyGate` — **pattern reuse only.** ST's barrier does not gate anything today; see §5.1.2. |
| Enemy authority | Host-driven proxy: client enemies are non-autonomous puppets; `NetRuntimeSpawn`, `NetWorldEntityRoster` (stable ids), `NetGameplayEnemyStateSnapshot` (`HostDrivenProxyPlan.md`) | `ISpawnPort` / `IEncounterReplication` — boss & adds host-owned; clients render puppets. |
| Session membership | Remote players registered as headless `Player` entries in `GameManager.Players` (`RemotePlayerRegistryManager`, `EnemyActivationAndPlayersRegistry.md`) | `IPlayerRoster` — who is in the session, their identities and positions. |
| Remote NPC activation | A **separate** `NpcUpdateManager.LateUpdate` postfix that wakes NPCs near remote players (`RemotePlayerRegistryManager.ActivateNpcsNearRemotePlayers`) | `IRemoteNpcActivationPort` — a distinct capability from the roster; see §5.1.3. |

### 5.1.1 Most of ST's surface is `internal`

Do not plan on compiling against these types. In the current ST source, roughly **189 types are `internal` and
38 are `public`**, and there is **no `[InternalsVisibleTo]`**. `CoopConnection`, `ArenaLockdownManager`,
`NetBossEncounterManager`, `NetLoadBarrier`, and `RemotePlayerRegistryManager` are all `internal`; `NetService`
is public. The adapter therefore reaches them by **reflection** (guarded, version-fragile), or ST exposes a
**public integration bridge** for the capabilities False Gods needs. The latter is preferable and is a
coordination item with the ST project. Either way the fragility stays inside the adapter and surfaces as
"capability registered" / "capability unavailable" ([ADR-004](ADRs/ADR-004-Optional-Sulfur-Together-Adapter.md)).

### 5.1.2 ST's `NetLoadBarrier` is not a blocking gate

ST's `NetLoadBarrier` is **host-side, `internal`, and log/status-only by default**: `LoadBarrierLogOnlyMode` is
`true` and `LoadBarrierBlockHostAdvance` is `false`, its own summary says it "never freezes host gameplay,
suppresses runtime sync, or blocks the host from advancing", and its timeout path logs
`"(log-only; host not blocked)"`. Real host-side gating is explicitly deferred in ST.

So: **reuse its tracking/ACK idea, but do not claim it provides an all-ready start gate.** False Gods
implements a real gate behind `IEncounterReadyGate` (§5.3), which fails closed, or requires ST to expose an
equivalent public capability.

### 5.1.3 Roster ≠ NPC activation

These are two mechanisms and two responsibilities, and conflating them was an error in an earlier draft:

- **`GameManager.Players` registration** feeds the vanilla game's own detection, line-of-sight, and target
  selection — an NPC that is already awake can see and chase a remote player.
- **Waking an NPC is separate.** Vanilla `NpcUpdateManager.LateUpdate` computes activation distance from
  `GameManager.Instance.PlayerObject.transform.position` — the **local main player only**. Registering a remote
  player in `GameManager.Players` does not change that loop. ST supplements it with an additional
  `NpcUpdateManager.LateUpdate` **postfix** that wakes inactive NPCs near any remote player.

`IPlayerRoster` therefore covers membership/identity/positions, and `IRemoteNpcActivationPort` covers "ensure
NPCs near non-local participants are awake". Both are outer ports declared in `FalseGods.RuntimeContracts` —
neither belongs in `FalseGods.Core`. In single-player `Integration.Sulfur` supplies a trivial roster and a no-op
activation port (vanilla's LOD already tracks the only player).

> **Open item.** The Harmony rule puts the `NpcUpdateManager.LateUpdate` postfix in `Integration.Sulfur`, but ST
> already patches the same method when installed. The adapter must be able to report "activation already
> provided" so the two do not double-wake. Coordination is unresolved (RiskList R10).

## 5.2 Arena identity

Define a small, explicit contract (modelled on `NetLevelManifestHeader`). It must carry enough to **prove
content agreement**, not merely to name the arena:

```
ArenaManifest {
  ArenaId                  : string   // stable id of the arena definition ("false_gods.arena.cave01")
  ArenaVersion             : int      // bump when layout/collision/nav/spawns change
  ContentHashSchemaVersion : int      // which hash definition produced ContentHash (§5.2.1)
  ContentHash              : bytes    // canonical content hash, per §5.2.1
  ProtocolVersion          : int      // the False Gods replication contract version
  BundleVersion            : string   // the mod AssetBundle build identity the content came from
  // fixed arena: nothing else required
  // future procedural arena (host-authoritative only):
  Seed                     : int
  Modules[]                : { moduleId, position, rotation, scale }   // host-decided final layout
}
```

- **Fixed arena:** `ArenaId` + `ArenaVersion` name the layout; `ContentHash` proves two peers actually realized
  the same one (a peer with a stale or tampered bundle has a matching version and a different hash). Every
  client loads the identical arena definition shipped in the mod; the host only announces "enter arena X vN".
- **Mismatch is a hard, explicit refusal** (like `ClientSceneRefused`), never a silent divergence. This is a
  compatibility boundary: validate before applying, and fail loudly.
- **Procedural arena (later):** the **host** generates the module list and broadcasts it; clients **never**
  generate independently. This mirrors "host owns seed + used-sets" from `SceneTransitionAndLinkState.md`.
- Every field is **untrusted input** on receipt. The host validates that an `ArenaReady` came from a peer that
  is a current session member, for the encounter currently being gated, with a well-formed manifest.

### 5.2.1 `ContentHash` — canonical definition

"A hash of the realized arena" is not a specification. Two peers must be able to compute **byte-identical**
hashes on different machines, different GPUs, and different frame timings, or the gate becomes a random source
of refusals. So the hash is defined over a **canonical document derived from authored data**, never over live
scene objects or runtime scan results.

**Algorithm.** SHA-256 over the canonical byte encoding below. Compared in full; never truncated. Peers compare
the pair `(ContentHashSchemaVersion, ContentHash)`.

**`ContentHashSchemaVersion`.** Bumped on **any** change to the input set, the ordering rule, the quantization
rule, or the algorithm. A change to the schema is a protocol-compatibility change, not a refactor.

**Inputs, encoded in this fixed order.** Every string is UTF-8 with a length prefix; every integer is
little-endian:

1. `ContentHashSchemaVersion`
2. `ArenaId`, `ArenaVersion`
3. `ArenaContentId` — the authored prefab / content-definition identity (bundle-relative address + content
   GUID). A definition identity, never a runtime instance.
4. **Hierarchy / markers:** for each authored node, `(StableMarkerId, NodeKind, ParentStableMarkerId,
   quantized local transform)`
5. **Vanilla proxies:** for each `VanillaAssetProxy`, `(StableMarkerId, AddressableKeyOrGuid,
   quantized local transform)` — the *resolved* stable asset identity, not the loaded object
6. **Collision authoring:** for each collider definition, `(StableMarkerId, ColliderKind, quantized geometry
   parameters, LayerName)`
7. **Navigation authoring:** for each nav definition, `(StableMarkerId, NavKind, quantized bounds)` — walkable
   surface, anchor, off-mesh-link endpoint, forbidden region — plus `NavmeshPrefabContentId` when the prebaked
   `NavmeshPrefab` path (report 4, Option 1) is used
8. **Spawn definitions:** `(StableMarkerId, SpawnKind, DefinitionId, quantized local transform)`
9. **Mechanism definitions:** `(StableMarkerId, MechanismDefinitionId, MechanismGroupId, quantized local
   transform)`

`StableMarkerId` is a GUID assigned in the editor and serialized into the prefab. It is not a name, not a
hierarchy path, and not `GetInstanceID()` ([DependencyRules.md §4](DependencyRules.md)).

**Ordering.** Every list is sorted by `StableMarkerId`, compared as raw UTF-8 bytes (ordinal). Hierarchy
enumeration order, `Transform` child order, and Addressables resolution order are all forbidden as ordering
sources — they are not stable across machines or Unity versions.

**Float quantization.** Floats never enter the hash.

- Positions, scales, sizes, bounds: `(long)Math.Round(value * 10_000, MidpointRounding.ToEven)` (0.1 mm),
  encoded as `int64`.
- Rotations: normalize the quaternion and normalize negative zero, then inspect `(w, x, y, z)` in that order.
  Find the first non-zero component; if it is negative, multiply the **entire** quaternion by `-1`. This
  lexicographic sign rule gives `q` and `-q` — the same rotation — one representation, including the
  `(0, 0, 0, ±1)` case. Quantize each component with the same round-half-to-even rule at 1e-6. A zero-length
  quaternion is a build-time export failure.
- Negative zero is normalized to zero before quantization.
- A `NaN` or infinity anywhere in the authored data is a **build-time export failure**, not a runtime hash.

**Explicitly excluded.** Anything that varies per machine, per process, or per frame:

- Unity `InstanceID`, `GetHashCode()`, and object references
- hierarchy / component / enumeration order
- memory addresses
- local file paths and absolute asset paths
- **runtime A\* node ids, node order, or recast scan output**
- non-authoritative visual temporaries: particles, VFX instances, spawned FX, anything under `DebugRoot`
- lighting bakes, probe data, and anything the renderer derives at load

**Runtime validation vs hashing — two different jobs.** After load and proxy resolution the runtime recomputes
the hash from the same canonical authored inputs it realized (marker ids, resolved addressable keys, authored
transforms). Separately, it *may* verify that the realized hierarchy matches the exported manifest (RiskList
R14) and that navigation built successfully. Those checks can abort the load — but **their output never feeds
the hash.** In particular, an A\* recast scan is not required to be bit-identical across machines, and hashing
its result would manufacture exactly the cross-machine determinism requirement that
[ADR-003](ADRs/ADR-003-Network-Native-Boss-Architecture.md) refuses to take on.

**Schema mismatch is its own refusal.** If two peers report different `ContentHashSchemaVersion` values, the
host refuses with `ContentHashSchemaMismatch` and **never compares the hashes** — hashes from different schemas
are not comparable, and an accidental collision would be worse than a clean refusal. Same schema, different
hash → `ContentMismatch`. Both abort the encounter (§5.3.1).

## 5.3 The canonical load → ready → start sequence (proposed)

All peers must load the **same** layout, collision, obstacles, spawns, mechanism state, and exit before the host
starts the boss. **Players are placed only after the gate passes** — the arena is not a place anyone stands in
until everyone has proven they built the same one.

```text
Load locally
  -> resolve assets and navigation
    -> report ArenaReady with identity/content hash
      -> host validates all required peers
        -> seal and teleport
          -> publish EncounterBaseline
            -> start simulation
```

In full:

```
1. ENTER (host)
   Host reaches the arena entry (a real game trigger, never a scene-name or timing heuristic).
   Host → peers: EnterArena(ArenaManifest). Sent over IEncounterChannel, reliable-ordered.

2. LOAD (each peer, locally)
   ArenaController.LoadAsync: instantiate the authored arena prefab and resolve only its VanillaAssetProxy
   objects (report 2). No player is moved. No boss exists yet.

3. RESOLVE ASSETS + NAVIGATION (each peer, locally)
   Await every Addressables handle; build/apply the arena's A* navigation via INavigationPort (report 4).
   Compute the local ContentHash from the canonical authored inputs (§5.2.1) — NOT from the nav scan result.

4. REPORT READY (each peer → host)
   ArenaReady(ArenaId, ArenaVersion, ContentHashSchemaVersion, ContentHash, ProtocolVersion, BundleVersion),
   reliable-ordered, through IEncounterChannel. A peer that failed to load sends ArenaLoadFailed(reason).

5. HOST VALIDATES ALL REQUIRED PEERS
   IEncounterReadyGate: the required set is every current session member from IPlayerRoster (including the
   host's own local readiness). The gate passes only when every required peer has reported ready AND every
   reported ArenaId/ArenaVersion/ProtocolVersion/BundleVersion matches the host's AND every peer used the
   host's ContentHashSchemaVersion AND every ContentHash matches. The gate FAILS CLOSED — see §5.3.1.

6. SEAL + TELEPORT (host authoritative)
   Host commands the seal and the teleport through IArenaLockdownPort. Each peer applies the command to its
   OWN local player. Only now do players stand in the arena.

7. PUBLISH EncounterBaseline (host → all)
   One reliable EncounterBaseline (BossSnapshot + ArenaSnapshot + encounter state; §5.7) establishes the
   starting state on every peer, including the host's own presentation path.

8. START SIMULATION (host)
   The host starts BossSimulation and ArenaSimulation (§5.5) and begins streaming snapshots and events.
   Client presentation stays non-authoritative (and, when reusing any vanilla presentation path, suppressed
   by default per the invulnerability-clear caveat in BossAuthority.md).
```

Single-player runs the identical sequence with a one-member required set; the gate resolves immediately at
step 5. There is no second code path.

### 5.3.1 Failure policy — fail closed

The gate never degrades into "start anyway". There is no timeout fallback that begins the fight.

| Condition | Behaviour |
|---|---|
| A peer reports `ArenaLoadFailed` | Host aborts the encounter, tears the arena down on every peer, and returns everyone to the pre-arena state. The boss never starts. |
| `ArenaId`/`ArenaVersion`/`ProtocolVersion`/`BundleVersion` mismatch | Explicit refusal to that peer with the reason; encounter aborted. This is a version-incompatibility, not a race. |
| `ContentHashSchemaVersion` mismatch | `ContentHashSchemaMismatch` refusal and abort. The hashes are **not compared** — they were produced by different definitions and mean different things (§5.2.1). |
| `ContentHash` mismatch on a matching schema and matching versions | `ContentMismatch` refusal and abort. The peers named the same arena and built different ones; continuing would desync silently. |
| Gate timeout (a required peer never answers) | Host **aborts** the encounter and reports which peers were outstanding. It does not start the boss with a partial roster. |
| A required peer disconnects **during** the gate | `IMultiplayerSession` reports the departure; the peer leaves the required set and the gate re-evaluates. If a required peer is merely unresponsive, the timeout rule above applies. |
| A peer disconnects **after** the fight starts | The fight continues for the remaining peers. On rejoin the peer receives one `EncounterBaseline` (§5.7) and resumes. |

Aborting is cheap (the arena is torn down, nobody has been teleported, no boss exists). Starting a boss against
a half-loaded party is not.

## 5.4 Existing boss adapters are references, not a hard constraint

SULFUR Together's current boss adapters solve compatibility with vanilla bosses whose internal simulation
was not designed for networking.

False Gods owns the source and design of its original bosses, so it should not inherit every limitation of
that compatibility layer.

The project should consume SULFUR Together's transport/session capabilities **through the project-owned ports
of §5.1** (never by direct dependency), and define a purpose-built original-boss replication protocol where
needed.

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
- authoritative decisions (random outcomes are decided **once on the host**; the result is replicated, not the RNG state);
- movement intent;
- damage;
- health;
- weak points;
- summons;
- death and completion.

`BossSimulation` owns **only the boss** — arena mechanisms belong to `ArenaSimulation` (below).

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

**Presentation never receives a wire DTO.** `BossSnapshot`, `ArenaSnapshot`, `BossEvent`, `ArenaEvent`, and
`EncounterBaseline` all stop at `FalseGods.Application`, which maps them into project-owned, transport-agnostic
`PresentationState` / `PresentationEvent`:

```text
Network DTO -> replication/application mapper -> PresentationState / PresentationEvent -> BossPresentation
```

Single-player feeds the same mapper from the local simulation's domain state and domain events, so both modes
drive the identical presentation entry point ([Architecture.md §7](Architecture.md)).

### BossReplication

Enabled when a multiplayer integration is registered.

Owns:

- host snapshots;
- discrete events;
- sequence numbers;
- simulation tick/time;
- interpolation targets;
- duplicate suppression;
- late-join state restoration;
- desync detection;
- recovery baselines.

### ArenaSimulation & EncounterCoordinator

The arena is **not** part of the boss. `ArenaSimulation` (a.k.a. `ArenaStateMachine`) owns arena gameplay
state — gates, hazards, destructible/phase objects, safe/unsafe regions, arena mechanisms, and the exit
state. An `EncounterCoordinator` bridges boss and arena via project-owned commands/events, so neither reaches
into the other's internals:

```
BossSimulation emits PhaseChanged(2)
    → EncounterCoordinator evaluates encounter rules
        → ArenaSimulation receives ActivateMechanismGroup("phase_2")
```

Both are `FalseGods.Core` concerns and, like the boss, are host-authoritative in multiplayer and consume
outer systems only through ports. This keeps one boss reusable across arenas and one arena reusable across
bosses ([Architecture.md §5](Architecture.md)).

## 5.6 During the fight — authority split

**Host owns:** `BossSimulation` (AI + pathing (report 4.5), target selection, boss position/rotation, attacks
& damage, **phase changes**, add spawns) and `ArenaSimulation` (arena mechanism state), plus fight start/end
and exit unlock — coordinated by the `EncounterCoordinator`.

**Client (BossPresentation) owns / does:** load the identical arena, render visuals, play arena FX,
**interpolate** the boss puppet, and update mechanisms/environment — all from the `PresentationState` /
`PresentationEvent` stream the `Application` mapper produces from host snapshots and events. Clients never
compute boss damage/phase/death/target/attack locally — they report intent (e.g. a hit request) and apply
host-authoritative results.

**Phase changes** are host-driven discrete events: host advances the phase (BossSimulation), then broadcasts
the phase transition + the resulting snapshot state; clients apply the visual/mechanism result and never
trigger phases from local HP.

## 5.7 Replicated state & discrete events

Boss state and arena state are **separate wire types**, and boss and arena transitions are **separate event
streams**. An arena's mechanism vocabulary must never be welded into a boss's snapshot — that is what makes a
boss reusable across arenas ([ADR-005](ADRs/ADR-005-Snapshot-And-Discrete-Event-Replication.md)).

Continuous correction (unreliable snapshots):

```
BossSnapshot {
  EncounterId
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
  LastProcessedBossEventSequence
}

ArenaSnapshot {
  EncounterId
  ArenaId
  ArenaVersion
  ProtocolVersion
  SimulationTick
  MechanismStates[]
  HazardStates[]
  GateStates[]
  LastProcessedArenaEventSequence
}
```

> The default snapshot carries **decision results**, not RNG state — the client does not re-simulate, so it
> needs outcomes (selected attack/target), not the host's random generator. The host's authoritative RNG state
> is host-internal and is replicated **only** if a concrete need arises (replay, save/restore, host recovery);
> it is deliberately **not** a default snapshot field (see
> [ADR-005](ADRs/ADR-005-Snapshot-And-Discrete-Event-Replication.md), RiskList R31 — a field with no consumer is
> premature).

Discrete transitions (reliable, sequenced — one sequence space per stream):

```
BossEvent {
  EncounterId
  BossInstanceId
  Sequence            // boss stream
  SimulationTick
  EventType
  AttackInstanceId
  Payload
}

ArenaEvent {
  EncounterId
  ArenaId
  Sequence            // arena stream, independent of the boss stream
  SimulationTick
  EventType
  Payload
}
```

`BossEvent` types (at least): `AttackSelected`, `TelegraphStarted`, `AttackCommitted`, `ProjectileSpawned`,
`PhaseChanged`, `WeakPointChanged`, `AddSpawned`, `BossStaggered`, `BossDefeated`.

`ArenaEvent` types (at least): `MechanismGroupActivated`, `MechanismStateChanged`, `HazardTriggered`,
`GateStateChanged`, `ArenaExitUnlocked`.

Late join and recovery (reliable, exactly once per join):

```
EncounterBaseline {
  EncounterId
  ProtocolVersion
  ArenaId / ArenaVersion / ContentHash
  SimulationTick
  EncounterPhase                    // pre-fight / fighting / defeated / exiting
  Boss   : BossSnapshot
  Arena  : ArenaSnapshot
  LastProcessedBossEventSequence
  LastProcessedArenaEventSequence
}
```

`EncounterBaseline` is the **only** composition point for boss + arena + encounter state. A joining or
recovering client applies exactly one baseline, then resumes per-stream event processing from the sequences it
carries. Whenever either half gains state, the baseline must gain it too — otherwise late join silently loses
it.

## 5.8 Synchronization principles

- Attack selection is performed once by the host.
- Random choices are never independently rolled by clients.
- An attack instance has a stable `AttackInstanceId`.
- Repeated packets must not repeat the attack.
- Visual playback is aligned to host simulation time.
- Damage remains host authoritative.
- Phase changes are host discrete events plus snapshot state.
- Reliable events carry transitions; unreliable snapshots carry continuous correction.
- Boss and arena events are ordered within their own stream and never share a sequence space.
- A client joining mid-fight receives one `EncounterBaseline` before normal event processing.
- Determinism means stable identity, stable per-stream order, idempotent application, and once-only
  authoritative decisions on the host — **not** cross-machine-identical physics, pathfinding, or simulation.

## 5.9 Synchronization invariants

1. All peers agree on `EncounterId`, `BossInstanceId`, `PhaseId`, `StateId`, and `AttackInstanceId`.
2. A phase transition occurs exactly once.
3. A boss death occurs exactly once.
4. Clients cannot trigger authoritative damage or death.
5. Duplicate reliable messages cannot duplicate projectiles, adds, rewards, or mechanisms.
6. A late-joining client can reconstruct boss, arena, and encounter state from one `EncounterBaseline`.
7. Packet loss of continuous snapshots does not cancel or repeat discrete attacks.
8. Host and clients use the same arena mechanism state, carried by `ArenaSnapshot`/`ArenaEvent`.
9. Client animation delay remains bounded and is corrected from host simulation time.
10. Single-player uses the same `BossSimulation` rules, and the same presentation entry point, without any
    multiplayer integration present.
11. The boss does not start until the ready gate passes for every required peer (§5.3.1).

## 5.10 Message strategy

Do not pre-commit to "no new boss message ids."

First determine whether the existing generic message envelope can carry False Gods snapshots/events cleanly.
If it can, reuse the envelope and add False Gods codecs/payloads.

If it cannot, add a small versioned message family dedicated to original bosses. Avoid one-off message types
per individual boss.

**Message ids, codecs, and registration live entirely inside `FalseGods.Integration.SulfurTogether`.**
`FalseGods.Protocol` defines the transport-neutral DTOs (`BossSnapshot`, `ArenaSnapshot`, `BossEvent`,
`ArenaEvent`, `EncounterBaseline`, `ArenaManifest`); `FalseGods.Application` serializes them into an
`EncodedPayload` with a `MessageDelivery` mode and hands that to `IEncounterChannel`. The adapter maps
`EncodedPayload` onto whatever envelope/ids ST uses. Nothing outside the adapter knows a message id or the
`Net*` types — and the adapter never sees a Protocol DTO.

Arena-loading messages stay minimal and (inside the adapter) follow SULFUR Together's `Net*` + `Net*Codec` +
`Register` pattern (`NetworkingArchitecture.md`): at most `EnterArena` (host→client, reliable-ordered),
`ArenaReady` / `ArenaLoadFailed` (client→host, reliable-ordered), with ids appended after the existing 46.

## 5.11 Exit & teardown (synchronized)

- Host decides the fight is over (`BossDefeated`) → unlocks the exit / drops the barrier through
  `IArenaLockdownPort` → tells clients. *(Adapter note: the ST implementation drives `ArenaLockdownManager`'s
  release path, e.g. AllDead → gate Open.)*
- Each end runs `ArenaController.Unload()` locally (report 4.6): destroy roots, release Addressables, remove
  the arena's own nodes / off-mesh links / graph modifiers from the **active level's** A\* graph, and unsubscribe
  all boss and arena replication/event handlers. Do **not** rely on a later level change to clean the graph —
  a level change does rebuild `AstarPath` (see report 4.6), but until then the leak is in the level everyone is
  still standing in.
- The **host's normal level transition** (already gated/relayed by SULFUR Together, and already rebuilding the
  A\* graph) is the natural carrier for leaving the arena, so seed/scene sync stays consistent.
- Personal loot and inventory stay client-owned — a project invariant, not a convenience. Only shared-world
  drops follow the existing world-drop path (`WorldItemDrop.md`).

## 5.12 Verification (needs two machines/instances — state this limit)

Real multiplayer verification requires a host + client (two game instances). The Arena Pipeline PoC (report 7,
Phase A) covers a host+client arena-load parity check and a boss-less enemy-sync check. Full network-native
boss parity — sim/presentation separation, host-time attack timelines, duplicate/loss handling, and
join-in-progress — is validated by the Original Boss Networking Vertical Slice (report 7, Phase B) once a test
boss exists.

Nothing in this document has been observed at runtime. Compilation has not been attempted; there is no code.
