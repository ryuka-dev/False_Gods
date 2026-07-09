# ADR-004 — Optional SULFUR Together adapter

**Status:** Proposed

## Context
False Gods must run in vanilla single-player when SULFUR Together (ST) is not installed, and provide
host-authoritative multiplayer when it is. ST — and, underneath it, LiteNetLib and Steam P2P — must not leak
into boss/arena/protocol/presentation code (the SULFUR Together coupling lesson).

"Optional" has a stricter meaning than "isolated". An adapter can be perfectly isolated and still be a hard CLR
dependency of the plugin that wires it, in which case a missing adapter DLL is a `FileNotFoundException` at
load rather than a graceful degradation.

## Decision

**1. No CLR dependency from the base plugin to the adapter.** `FalseGods.Plugin` must contain no assembly
reference to `FalseGods.Integration.SulfurTogether`, no adapter type in any field/parameter/return signature,
no `typeof(...)`, and no static-initialization path that touches an adapter type. With the adapter assembly
absent, the base plugin loads and plays single-player.

**2. The dependency points from the optional adapter to a stable contract.** `FalseGods.RuntimeContracts` is a
small, dependency-light assembly (Core only; no Protocol, no Unity, no ST) holding the capability ports and the
registration seam:

```
FalseGods.Plugin  ──creates──►  IIntegrationRegistry   (FalseGods.RuntimeContracts)
                                        ▲
                                        │ Register(IFalseGodsIntegration)
                                        │
              FalseGods.Integration.SulfurTogether  (optional assembly)
```

Preferred mechanism: the adapter ships as its **own BepInEx plugin** with soft dependencies on the False Gods
plugin and on ST, and self-registers on load. Fallback: **reflective discovery** of a known assembly/type from
one small, exception-guarded class in the base plugin. In both cases the Composition Root only ever sees
`IFalseGodsIntegration` and the RuntimeContracts ports (`IMultiplayerSession`, `IEncounterChannel`,
`IPlayerRoster`, `IArenaLockdownPort`, `IEncounterReadyGate`, `IRemoteNpcActivationPort`).

**3. The adapter cannot assume it can compile against ST.** Measured against the current ST source, ST declares
roughly **189 `internal` types to 38 `public`**, with **no `[InternalsVisibleTo]`**. The capabilities False Gods
wants are on the internal side — `CoopConnection`, `ArenaLockdownManager`, `NetBossEncounterManager`,
`NetLoadBarrier`, and `RemotePlayerRegistryManager` are all `internal`; `NetService` is public. So the adapter
must either reach them via **reflection** (guarded, version-fragile) or ST must expose a **public integration
bridge**. The latter is the preferred long-term path and is a coordination item with the ST project; the former
is what exists today. Either way the fragility is confined to the adapter and surfaces outward only as
"capability registered" / "capability unavailable".

**4. Replication is application logic over an opaque channel.** `IEncounterChannel` carries `EncodedPayload` +
`MessageDelivery`, so the adapter never touches a `FalseGods.Protocol` DTO and never needs to reference
`FalseGods.Protocol`. Serialization lives in `FalseGods.Application`.

## Alternatives considered
- **Direct ST/transport references from feature code** — the exact debt pattern to avoid. Rejected.
- **Hard dependency on ST** — breaks vanilla single-player. Rejected.
- **Plugin references the adapter and null-checks at runtime** — a reference is resolved at type-load, not at
  the null check; this is the failure mode this ADR exists to prevent. Rejected.
- **Adapter compiles directly against ST's managers** — impossible today (they are `internal`); would also
  couple the adapter's build to an ST source checkout. Rejected in favour of reflection now, a public ST bridge
  later.

## Consequences
- Core/UnityRuntime/Plugin never reference ST; a second transport or ST refactor changes only this adapter.
- Requires one extra assembly (`FalseGods.RuntimeContracts`) and a registration seam.
- Reflection into ST internals is version-fragile: every ST update needs a re-probe, and every reflective call
  must degrade to "capability unavailable" rather than throw. This is accepted debt, tracked as a coordination
  request to ST for a public bridge.

## Verification status
Unverified. Gates: "loads and plays with the adapter DLL deleted" (RiskList R20/R29, PoC B0) and a metadata
check that `FalseGods.Plugin.dll` references no adapter assembly ([DependencyRules.md §7](../DependencyRules.md)).
ST visibility counts were read from the ST source in this repository's neighbouring checkout and will drift with
ST releases.
