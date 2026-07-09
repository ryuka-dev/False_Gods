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
FalseGods.Plugin  ──subscribes to──►  FalseGodsIntegrations   (static broker, FalseGods.RuntimeContracts)
                                             ▲
                                             │ Register(IFalseGodsIntegration) → IIntegrationRegistration
                                             │
              FalseGods.Integration.SulfurTogether  (optional companion BepInEx plugin)
```

**The mechanism — one path, not a menu.** The adapter ships as its **own BepInEx plugin** with a **hard
`[BepInDependency]` on the base plugin's GUID**. BepInEx dependencies are GUID strings, not CLR references, so
this pins load order — base plugin `Awake` completes before adapter `Awake` begins — with no type coupling in
either direction. The adapter's only False Gods CLR reference is `FalseGods.RuntimeContracts`; it never
references `FalseGods.Plugin`. Because the base plugin has already subscribed to the broker by the time the
adapter loads, `Register(...)` lands in an initialized seam: nothing polls, nothing retries, nothing races.
The adapter's dependency on **ST** is hard if it is useless without ST (the expected case) or soft plus an
explicit capability probe if it must degrade; either way the reflective probing of point 3 still runs.

*Fallback, documented but not co-implemented:* the base plugin probes for a known assembly/type and invokes a
parameterless factory returning `IFalseGodsIntegration`, all reflection in one guarded class. This replaces the
mechanism above; the two never coexist, so exactly one path can deliver an integration.

**The broker is a registration point, not a service locator.** `FalseGodsIntegrations` is a single slot holding
at most one `IFalseGodsIntegration` plus a change event. No `Resolve<T>()`, no type-keyed dictionary.
`FalseGods.Plugin` is its **only** permitted reader; every other type is constructor-injected. The **first**
registration wins — a duplicate is *rejected* (failed result, no throw, no replacement) so load order never
silently decides which integration is authoritative. `Register` returns a disposable
`IIntegrationRegistration`; only its holder can revoke, and disposing raises the change event so the Composition
Root can tear the multiplayer composition down. Unit tests inject `IFalseGodsIntegration` fakes directly and
never touch the broker; an integration test that exercises the seam registers, asserts, and disposes, leaving a
clean slot — so there is no hidden global reset to forget.

Either way the Composition Root only ever sees `IFalseGodsIntegration` and the RuntimeContracts ports
(`IMultiplayerSession`, `IEncounterChannel`, `IPlayerRoster`, `IArenaLockdownPort`, `IEncounterReadyGate`,
`IRemoteNpcActivationPort`).

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
- **Hard CLR dependency on ST from the base plugin** — breaks vanilla single-player. Rejected. (Note this is a
  different thing from the adapter's *BepInEx* hard dependency, which is a GUID string and couples nothing.)
- **Plugin references the adapter and null-checks at runtime** — a reference is resolved at type-load, not at
  the null check; this is the failure mode this ADR exists to prevent. Rejected.
- **Soft BepInEx dependency on the base plugin** — the adapter could then load before the base plugin, so
  registration would have to poll or retry, and a failure to register would be a timing bug rather than a
  configuration error. Rejected in favour of the hard dependency, which turns "base plugin missing" into
  "BepInEx skips the adapter" — the correct outcome, decided by the loader, not by us.
- **Keeping both the companion plugin and reflective discovery live** — two paths by which an integration can
  arrive means two orderings, two failure modes, and an ambiguous answer to "why is multiplayer inert?".
  Rejected: the fallback replaces the primary if we ever take it.
- **A general DI container / service locator in RuntimeContracts** — solves a problem we do not have and would
  let any module pull any service, dissolving the boundary this ADR defends. Rejected in favour of a single
  slot with one reader.
- **Adapter compiles directly against ST's managers** — impossible today (they are `internal`); would also
  couple the adapter's build to an ST source checkout. Rejected in favour of reflection now, a public ST bridge
  later.

## Consequences
- Core/UnityRuntime/Plugin never reference ST; a second transport or ST refactor changes only this adapter.
- Requires one extra assembly (`FalseGods.RuntimeContracts`) and a registration seam.
- The adapter must be **packaged and installed as its own BepInEx plugin**, not as a loose DLL beside the base
  plugin. That is a real packaging constraint on the Thunderstore/Gale layout.
- Static state in the broker is a deliberate, bounded exception to "no global mutable state" (RiskList R26). It
  is bounded by: one slot, one type, one reader, no `Resolve`, token-scoped revocation. If any of those five
  properties is relaxed, this ADR is what has been violated.
- Reflection into ST internals is version-fragile: every ST update needs a re-probe, and every reflective call
  must degrade to "capability unavailable" rather than throw. This is accepted debt, tracked as a coordination
  request to ST for a public bridge.

## Verification status
Unverified. Gates: "loads and plays with the adapter DLL deleted" (RiskList R20/R29, PoC B0) and a metadata
check that `FalseGods.Plugin.dll` references no adapter assembly (rules `FG-ARCH-002` / `FG-ARCH-009` in
[ArchitectureEnforcement.md](../ArchitectureEnforcement.md)). ST visibility counts were read from the ST source
in this repository's neighbouring checkout and will drift with ST releases.
