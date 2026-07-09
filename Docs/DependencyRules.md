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

Only Integration.Sulfur may use Harmony, reflection, or SULFUR internals.
Only Integration.Sulfur may directly operate AstarPath or Addressables for vanilla assets.
Only Integration.SulfurTogether may reference SULFUR Together internals, LiteNetLib, or Steamworks.

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
| `HarmonyLib`, reflection into game internals | Integration.Sulfur |
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
- **Harmony/reflection isolation:** all Harmony patches and reflection into game internals live in
  `Integration.Sulfur` (game) or `Integration.SulfurTogether` (ST). Feature code never adds a patch.
- **Unity isolation:** Core/Protocol/RuntimeContracts contain no `UnityEngine` types. Where a Unity math type is
  genuinely needed in the domain, wrap it in a project-owned value type at the boundary.
- **Wire/presentation isolation:** `FalseGods.Protocol` types stop at `FalseGods.Application`. Presentation is
  reached only through `PresentationState` / `PresentationEvent` (Architecture §7).

## 6. Optional-integration rules

- `FalseGods.Integration.SulfurTogether` is an **optional assembly, and the base plugin has no CLR dependency
  on it.** `FalseGods.Plugin` must contain no assembly reference, no type in any signature, no `typeof(...)`,
  and no static-initialization path that touches an adapter type.
- The dependency runs the other way: the optional adapter references the small, stable
  `FalseGods.RuntimeContracts` and registers its capabilities through `IIntegrationRegistry` /
  `IFalseGodsIntegration` at runtime. Preferred shape is a self-registering optional BepInEx plugin with soft
  dependencies; reflective discovery from the base plugin is the fallback, confined to one guarded class
  (Architecture §4.1).
- **Only stable project-owned contracts may cross the seam.** The registry hands the Composition Root
  `IPlayerRoster`, `IMultiplayerSession`, `IEncounterChannel`, `IArenaLockdownPort`, `IEncounterReadyGate`, and
  `IRemoteNpcActivationPort` — never an ST type.
- A missing ST assembly, a missing adapter assembly, or a failed registration yields a graceful "multiplayer
  unavailable" and a working single-player game, never a type-load failure (RiskList R20/R29).
- **Do not assume the adapter can compile against ST directly.** ST's relevant systems (`CoopConnection`,
  `ArenaLockdownManager`, `NetBossEncounterManager`, `NetLoadBarrier`, `RemotePlayerRegistryManager`) are
  `internal` and ST publishes no `[InternalsVisibleTo]`. The adapter must use reflection, or ST must expose a
  public integration bridge. Either way the fragility stays inside the adapter (Architecture §4.2).

## 7. Mechanical enforcement plan (planned, not built this pass)

The boundaries above should become automated checks:

1. **Separate `.csproj` / Unity assembly definitions** per module, with **restricted project references** — the
   compiler rejects a forbidden reference. Core's csproj references **no** Unity/game/networking DLLs;
   `FalseGods.Plugin.csproj` contains **no** reference to `FalseGods.Integration.SulfurTogether`;
   `FalseGods.UnityRuntime.csproj` contains **no** reference to `FalseGods.Protocol`.
2. **CI namespace/dependency scan** — fail the build if a forbidden namespace appears in a module (e.g.
   `UnityEngine` in Core, `LiteNetLib` in Protocol, `SULFURTogether` outside its adapter, `FalseGods.Protocol`
   in UnityRuntime).
3. **Assembly-reference assertion on the shipped `FalseGods.Plugin.dll`** — read its metadata references and
   fail if `FalseGods.Integration.SulfurTogether` appears. This is the check that a signature-level leak cannot
   hide from.
4. **Optional Roslyn analyzer / architecture tests** (e.g. NetArchTest-style) asserting the dependency matrix
   in §1–§2, including "no `FalseGods.Protocol` type reachable from a `BossPresentation` public member".
5. **Package check** at pack time — the shipped bundle/plugin contains only original + proxy references and
   **no** vanilla SULFUR assets.
6. **Tests that run without SULFUR Together installed** — launch with the adapter DLL deleted and assert the
   base plugin loads and single-player plays (RiskList R29).
7. **Build failure on forbidden namespaces** wherever practical, so the common violation is a compile error.

Implementation of these checks is a later task; this document is the specification for them.

## 8. How to use this during development

Before adding a cross-module call, confirm it obeys §1–§2. If the correct boundary (a port) does not exist,
**stop and add the smallest appropriate abstraction** rather than reaching across layers — see
[DefinitionOfDone.md](DefinitionOfDone.md) for the per-feature process.
