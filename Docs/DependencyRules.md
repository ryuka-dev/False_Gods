# Dependency Rules

*The concrete, checkable boundary rules for False Gods, and how they will eventually be enforced
mechanically.* Companion to [Architecture.md](Architecture.md). The intent is that **common violations become
compile-time or CI failures**, not matters of discipline.

Nothing here is implemented in this documentation pass; this is the rulebook and the enforcement plan.

## 1. Allowed project references

| Module | May reference |
|---|---|
| `FalseGods.Core` | .NET base class library only |
| `FalseGods.Protocol` | `FalseGods.Core` |
| `FalseGods.RuntimeContracts` | `FalseGods.Core` |
| `FalseGods.Application` | `FalseGods.Core`, `FalseGods.Protocol`, `FalseGods.RuntimeContracts` |
| `FalseGods.UnityRuntime` | `FalseGods.Core`, `FalseGods.RuntimeContracts`, `UnityEngine` (+ URP/2D modules) |
| `FalseGods.Integration.Sulfur` | `FalseGods.Core`, `FalseGods.Protocol`, `FalseGods.RuntimeContracts`, `FalseGods.Application`, `UnityEngine`, game DLLs, Harmony, A*, Addressables |
| `FalseGods.Integration.SulfurTogether` *(optional)* | `FalseGods.Core`, `FalseGods.RuntimeContracts`, SULFUR Together, LiteNetLib/Steamworks (transitively) |
| `FalseGods.Plugin` (Composition Root) | `FalseGods.Core`, `FalseGods.Protocol`, `FalseGods.RuntimeContracts`, `FalseGods.Application`, `FalseGods.UnityRuntime`, `FalseGods.Integration.Sulfur`, BepInEx |

Note what `FalseGods.Plugin` may **not** reference: `FalseGods.Integration.SulfurTogether`. See §6.

`FalseGods.Integration.Sulfur` is referenced only by the Composition Root; no inner module references an
adapter.

## 2. Forbidden dependencies (explicit)

```
FalseGods.Core MUST NOT reference UnityEngine.
FalseGods.Core MUST NOT reference BepInEx or Harmony.
FalseGods.Core MUST NOT reference PerfectRandom.Sulfur.*.
FalseGods.Core MUST NOT reference Pathfinding (A*).
FalseGods.Core MUST NOT reference SULFUR Together.
FalseGods.Core MUST NOT reference Addressables, LiteNetLib, or Steamworks.
FalseGods.Core MUST NOT reference FalseGods.Protocol or FalseGods.RuntimeContracts.
FalseGods.Core MUST NOT declare asset, Addressables, navigation, scene, loading, channel,
                    session, roster, or replication ports. Those belong to the outer module
                    whose code consumes them (Architecture §6).
FalseGods.Core MUST NOT declare a port that has no present consumer.

FalseGods.Protocol MUST NOT reference LiteNetLib or Steamworks.
FalseGods.Protocol MUST NOT reference concrete SULFUR Together message types.
FalseGods.Protocol MUST NOT reference UnityEngine presentation objects.
FalseGods.Protocol MUST NOT contain PresentationState/PresentationEvent.

FalseGods.RuntimeContracts MUST NOT reference FalseGods.Protocol.
FalseGods.RuntimeContracts MUST NOT reference UnityEngine, ST, LiteNetLib, or Steamworks.

FalseGods.UnityRuntime MUST NOT reference FalseGods.Protocol.
BossPresentation MUST NOT accept BossSnapshot, ArenaSnapshot, BossEvent, ArenaEvent,
                    EncounterBaseline, or any other wire DTO. It accepts only
                    PresentationState / PresentationEvent.
BossPresentation MUST NOT apply authoritative gameplay damage, phase, death, target, or
                    attack outcome.

FalseGods.Plugin MUST NOT reference FalseGods.Integration.SulfurTogether — no assembly
                    reference, no type in a signature, no typeof(), no static-init touch.

Only Integration.Sulfur may apply Harmony patches (any exception needs its own ADR).
Only Integration.Sulfur may reflect into SULFUR / base-game internals.
Only Integration.Sulfur may directly operate AstarPath or Addressables for vanilla assets.
Only Integration.SulfurTogether may reference or reflect into SULFUR Together internals,
                    LiteNetLib, or Steamworks.
Neither adapter reflects into the other's target system.
No other module may reflect into ANY external system's internals.

Boss and arena feature code MUST NOT send packets directly.
Multiplayer loading and combat flows MUST NOT name GameManager.Players, ArenaLockdownManager,
                    NetService, CoopConnection, NetLoadBarrier, or any other ST/game internal.
                    They use IPlayerRoster, IArenaLockdownPort, IMultiplayerSession,
                    IEncounterChannel, IEncounterReadyGate, IRemoteNpcActivationPort.
Network handlers MUST NOT directly manipulate arbitrary Unity presentation objects.
Prefab components MUST NOT access transport/session singletons (NetService, CoopConnection, Steamworks, ST managers).
```

Concrete ST/game type names may appear in prose **only** inside an "adapter implementation note" or an
"existing-systems mapping" table — never in the description of a main flow.

## 3. Forbidden namespace / API quick list

| Namespace / API | Allowed only in |
|---|---|
| `UnityEngine.*` | UnityRuntime, Integration.* , Plugin (never Core/Protocol/RuntimeContracts) |
| `HarmonyLib` / `[HarmonyPatch]` | Integration.Sulfur |
| Reflection into **SULFUR / base-game** internals | Integration.Sulfur |
| Reflection into **SULFUR Together** internals | Integration.SulfurTogether |
| `Pathfinding.*` (AstarPath, RichAI, recast) | Integration.Sulfur |
| `UnityEngine.AddressableAssets.*` (vanilla assets) | Integration.Sulfur |
| `PerfectRandom.Sulfur.*` | Integration.Sulfur |
| `SULFURTogether.*`, `LiteNetLib.*`, `Steamworks.*` | Integration.SulfurTogether |
| `FalseGods.Protocol.*` | Protocol, Application, Plugin (never UnityRuntime / RuntimeContracts / Core) |
| `FalseGods.Integration.SulfurTogether.*` | itself only (never Plugin) |
| `BepInEx.*` | Plugin, Integration.SulfurTogether (its own optional plugin entry) |

## 4. Identity & lifecycle shortcuts — prohibited

Identity concepts that may change independently must stay separate — a transport connection is not a session
peer, a session peer is not a world entity, and a display name is never an authoritative identifier. Do not use
these as identity or lifecycle sources:

- scene object **names** as network identity;
- `GetInstanceID()` as replicated identity;
- arbitrary **hierarchy paths** as long-term protocol ids;
- **timing delays** or **scene-name polling** as lifecycle hooks.

Use project-owned stable ids (`EncounterId`, `BossInstanceId`, `AttackInstanceId`, `ArenaId`, `SessionPeerId`)
and real event/state transitions instead. Attach behaviour to the canonical event (the method that commits the
level transition, the callback that confirms the connection, the authoritative mutation), not to a correlate of
it.

## 5. Transport, Harmony, Unity isolation rules

- **Transport isolation:** False Gods must not know whether ST uses direct UDP, LiteNetLib, Steam P2P,
  loopback, relay, or a future transport. Adding/replacing a transport must change **only**
  `Integration.SulfurTogether` (or ST itself) — never `BossSimulation`, `BossPresentation`, arena content,
  boss definitions, or the Protocol contracts. The adapter sees only `EncodedPayload` + `MessageDelivery`;
  serialization of Protocol DTOs happens in `Application`.
- **Harmony isolation:** every Harmony patch lives in `Integration.Sulfur`. Feature code never adds a patch, and
  neither does the ST adapter — if one ever genuinely needs to, that exception gets its own ADR before the
  patch gets written.
- **Reflection isolation, split by target:** `Integration.Sulfur` is the only module that may reflect into
  **SULFUR / base-game** internals; `Integration.SulfurTogether` is the only module that may reflect into
  **SULFUR Together** internals (which is unavoidable — see §6). Neither reflects into the other's target, and
  no other module reflects into any external system's internals. Reflection over *False Gods'* own types (e.g.
  in tests) is not what this rule is about.
- **Unity isolation:** Core/Protocol/RuntimeContracts contain no `UnityEngine` types. Where a Unity math type is
  genuinely needed in the domain, wrap it in a project-owned value type at the boundary.
- **Wire/presentation isolation:** `FalseGods.Protocol` types stop at `FalseGods.Application`. Presentation is
  reached only through `PresentationState` / `PresentationEvent` (Architecture §7).

## 6. Optional-integration rules

- `FalseGods.Integration.SulfurTogether` is an **optional assembly, and the base plugin has no CLR dependency
  on it.** `FalseGods.Plugin` must contain no assembly reference, no type in any signature, no `typeof(...)`,
  and no static-initialization path that touches an adapter type.
- The dependency runs the other way. The adapter is a **companion BepInEx plugin** that references only
  `FalseGods.RuntimeContracts` (never `FalseGods.Plugin`), declares a **hard `[BepInDependency]` on the base
  plugin's GUID** — a string, not a CLR reference — so that the base plugin's `Awake` has already run, and then
  calls the static `FalseGodsIntegrations.Register(IFalseGodsIntegration)` broker. Its dependency on ST itself
  is hard or soft depending on whether it can do anything useful without ST. This is the **single** preferred
  path; reflective discovery from the base plugin is a documented *alternative to* it, never implemented
  alongside it (Architecture §4.1).
- **The broker is a single-slot registration point, not a service locator.** No `Resolve<T>()`, no type-keyed
  dictionary. `FalseGods.Plugin` is its only permitted reader; everything else is constructor-injected. The
  first registration wins and a duplicate is rejected rather than replacing it. `Register` returns a disposable
  token, and only the token's holder can revoke. Unit tests inject fakes and never touch the broker.
- **Only stable project-owned contracts may cross the seam.** The registry hands the Composition Root
  `IPlayerRoster`, `IMultiplayerSession`, `IEncounterChannel`, `IArenaLockdownPort`, `IEncounterReadyGate`, and
  `IRemoteNpcActivationPort` — never an ST type.
- A missing ST assembly, a missing adapter assembly, or a failed registration yields a graceful "multiplayer
  unavailable" and a working single-player game, never a type-load failure (RiskList R20/R29).
- **Do not assume the adapter can compile against ST directly.** ST's relevant systems (`CoopConnection`,
  `ArenaLockdownManager`, `NetBossEncounterManager`, `NetLoadBarrier`, `RemotePlayerRegistryManager`) are
  `internal` and ST publishes no `[InternalsVisibleTo]`. The adapter must use reflection, or ST must expose a
  public integration bridge. Either way the fragility stays inside the adapter (Architecture §4.2).

## 7. Enforcement

**This document says what is allowed.** [ArchitectureEnforcement.md](ArchitectureEnforcement.md) says **how the
rules are checked, when they run, what state each check is in, and how a rule is added, excepted, or retired.**
Deliberately separate: a rule is a claim about the design, and a check is a program that can be wrong about it.

The rules above are the authority. The enforcement document carries the stable rule ids (`FG-ARCH-001` …) that
every automated check must cite, and none of the rule text is duplicated there.

Nothing is enforced mechanically yet; every rule is currently `Planned`.

## 8. How to use this during development

Before adding a cross-module call, confirm it obeys §1–§2. If the correct boundary (a port) does not exist,
**stop and add the smallest appropriate abstraction** rather than reaching across layers — see
[DefinitionOfDone.md](DefinitionOfDone.md) for the per-feature process.
