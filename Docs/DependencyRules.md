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
| `FalseGods.UnityRuntime` | `FalseGods.Core`, `FalseGods.Protocol`, `UnityEngine` (+ URP/2D modules) |
| `FalseGods.Integration.Sulfur` | `FalseGods.Core`, `FalseGods.Protocol`, `UnityEngine`, game DLLs, Harmony, A*, Addressables |
| `FalseGods.Integration.SulfurTogether` | `FalseGods.Core`, `FalseGods.Protocol`, SULFUR Together, LiteNetLib/Steamworks (transitively) |
| `FalseGods.Plugin` (Composition Root) | all False Gods modules + BepInEx |

Adapters are referenced **only** by the Composition Root. No inner module references an adapter.

## 2. Forbidden dependencies (explicit)

```
FalseGods.Core MUST NOT reference UnityEngine.
FalseGods.Core MUST NOT reference BepInEx or Harmony.
FalseGods.Core MUST NOT reference PerfectRandom.Sulfur.*.
FalseGods.Core MUST NOT reference Pathfinding (A*).
FalseGods.Core MUST NOT reference SULFUR Together.
FalseGods.Core MUST NOT reference Addressables, LiteNetLib, or Steamworks.

FalseGods.Protocol MUST NOT reference LiteNetLib or Steamworks.
FalseGods.Protocol MUST NOT reference concrete SULFUR Together message types.
FalseGods.Protocol MUST NOT reference UnityEngine presentation objects.

Only Integration.Sulfur may use Harmony, reflection, or SULFUR internals.
Only Integration.Sulfur may directly operate AstarPath or Addressables for vanilla assets.
Only Integration.SulfurTogether may reference SULFUR Together internals, LiteNetLib, or Steamworks.

Boss and arena feature code MUST NOT send packets directly.
BossPresentation MUST NOT apply authoritative gameplay damage.
Network handlers MUST NOT directly manipulate arbitrary Unity presentation objects.
Prefab components MUST NOT access transport/session singletons (NetService, CoopConnection, Steamworks, ST managers).
```

## 3. Forbidden namespace / API quick list

| Namespace / API | Allowed only in |
|---|---|
| `UnityEngine.*` | UnityRuntime, Integration.* , Plugin (never Core/Protocol) |
| `HarmonyLib`, reflection into game internals | Integration.Sulfur |
| `Pathfinding.*` (AstarPath, RichAI, recast) | Integration.Sulfur |
| `UnityEngine.AddressableAssets.*` (vanilla assets) | Integration.Sulfur |
| `PerfectRandom.Sulfur.*` | Integration.Sulfur |
| `SULFURTogether.*`, `LiteNetLib.*`, `Steamworks.*` | Integration.SulfurTogether |
| `BepInEx.*` | Plugin |

## 4. Identity & lifecycle shortcuts — prohibited

Do not use as identity or lifecycle sources (CLAUDE.md §3, §6):

- scene object **names** as network identity;
- `GetInstanceID()` as replicated identity;
- arbitrary **hierarchy paths** as long-term protocol ids;
- **timing delays** or **scene-name polling** as lifecycle hooks.

Use project-owned stable ids (`BossInstanceId`, `AttackInstanceId`, `ArenaId`) and real event/state
transitions instead.

## 5. Transport, Harmony, Unity isolation rules

- **Transport isolation:** False Gods must not know whether ST uses direct UDP, LiteNetLib, Steam P2P,
  loopback, relay, or a future transport. Adding/replacing a transport must change **only**
  `Integration.SulfurTogether` (or ST itself) — never `BossSimulation`, `BossPresentation`, arena content,
  boss definitions, or the Protocol contracts.
- **Harmony/reflection isolation:** all Harmony patches and reflection into game internals live in
  `Integration.Sulfur` (game) or `Integration.SulfurTogether` (ST). Feature code never adds a patch.
- **Unity isolation:** Core/Protocol contain no `UnityEngine` types. Where a Unity math type is genuinely
  needed in the domain, wrap it in a project-owned value type at the boundary.

## 6. Optional-integration rules

- `Integration.SulfurTogether` is an **optional** assembly. The base plugin must not hard-reference it in a
  way that force-loads missing types when ST is absent.
- Discovery is isolated inside the adapter (reflection / runtime capability registration). A missing ST
  assembly yields a graceful "multiplayer unavailable", never a type-load failure (RiskList R29).

## 7. Mechanical enforcement plan (planned, not built this pass)

The boundaries above should become automated checks:

1. **Separate `.csproj` / Unity assembly definitions** per module, with **restricted project references** — the
   compiler rejects a forbidden reference. Core's csproj references **no** Unity/game/networking DLLs.
2. **CI namespace/dependency scan** — fail the build if a forbidden namespace appears in a module (e.g.
   `UnityEngine` in Core, `LiteNetLib` in Protocol, `SULFURTogether` outside its adapter).
3. **Optional Roslyn analyzer / architecture tests** (e.g. NetArchTest-style) asserting the dependency matrix
   in §1–§2.
4. **Package check** at pack time — the shipped bundle/plugin contains only original + proxy references and
   **no** vanilla SULFUR assets.
5. **Tests that run without SULFUR Together installed** — proving single-player has no hard ST dependency.
6. **Build failure on forbidden namespaces** wherever practical, so the common violation is a compile error.

Implementation of these checks is a later task; this document is the specification for them.

## 8. How to use this during development

Before adding a cross-module call, confirm it obeys §1–§2. If the correct boundary (a port) does not exist,
**stop and add the smallest appropriate abstraction** rather than reaching across layers — see
[DefinitionOfDone.md](DefinitionOfDone.md) for the per-feature process.
