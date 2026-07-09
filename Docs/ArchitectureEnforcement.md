# Architecture Enforcement

*How the architecture rules are checked, when the checks run, what state each one is in, and how a rule is
added, excepted, or retired.*

**Nothing in this document is implemented.** There is no code, no test project, no CI configuration, and no
`scripts/verify.ps1` in this repository yet. Every rule below is `Planned`.

## 1. Purpose and authority

[DependencyRules.md](DependencyRules.md) is the authority on **what is allowed and forbidden**.
This document is the authority on **how that is checked**.

The split is deliberate, and the reason is worth stating plainly: a rule is a claim about the design, and a
check is a program that can be wrong about the rule. They fail differently, they change at different rates, and
conflating them produces a document nobody trusts — one where a broken script reads like a broken boundary.

Therefore:

- Rule text is **never duplicated here.** Each entry in §5 links to its authority and states only how it is
  verified.
- If this document and DependencyRules disagree, **DependencyRules wins** and the check is the bug.
- A check may be weaker than its rule (it catches a subset). It must never be *stronger* — a check that fails
  code the rule permits is a false positive, and §12 says what happens then.

## 2. Enforcement principles

1. **The compiler is the first line.** Most boundaries should be impossible to violate because the project
   reference does not exist. A restricted `.csproj` / `.asmdef` graph catches more than any scanner, earlier,
   and with a better error message.
2. **Check real metadata, not text.** Assembly references and public type signatures are read from compiled
   IL. Grep is a smell test, not a gate: it cannot see a type reached through a generic parameter, and it fires
   on a type name inside a comment.
3. **Only high-confidence, structural boundaries are enforced.** Assembly dependencies, public API shapes,
   patch locations, optional-integration behaviour. **Not** class counts, file sizes, private-method
   organisation, layering *within* a module, or subjective naming.
4. **Every check cites a rule id.** A failure that does not say `FG-ARCH-00N` and link its authority is an
   unactionable failure.
5. **New rules land in warning mode first.** A rule that has never run against the real codebase has never been
   tested. See §11.
6. **The fast local loop stays fast.** See §3.
7. **Enforcement serves the design, not the reverse.** If a rule is expensive, noisy, or frequently excepted,
   the rule is wrong. Fix the rule; do not train developers to route around it.

## 3. Local verification entry point

The intended single command, **not yet created**:

```powershell
.\scripts\verify.ps1
```

It will run, in order:

```text
dotnet build              (restricted project references — the compiler catches most violations)
Core unit tests           (Unity-less domain tests)
Architecture tests        (assembly + public-API assertions, §6–§9)
git diff --check          (whitespace / conflict markers)
```

**Budget: about one minute.** Anything slower does not belong in the pre-commit loop; it belongs in §4's
higher levels. When this budget is threatened, move a check outward — do not let the loop grow until people
stop running it.

Explicitly *not* in this command: launching Unity, launching SULFUR, and anything needing two game instances.

## 4. CI enforcement levels

| Level | Runs | Contains | Blocking? |
|---|---|---|---|
| **L0 — local** | `.\scripts\verify.ps1`, before commit | build, Core tests, architecture tests, `git diff --check` | Developer's own loop |
| **L1 — pull request** | every PR | everything in L0, on a clean checkout | **Yes** for rules marked `Required in CI` |
| **L2 — packaging** | on tag / release build | L1 + the package check (no vanilla assets in the bundle; adapter packaged as its own plugin) | **Yes** |
| **L3 — manual / pre-release** | by hand, before a release | in-game probes, single-player smoke test, **host + client** two-instance validation, adapter-DLL-deleted launch | Release gate, **not** a per-commit gate |

L3 is where the PoC lives ([MinimalProofOfConceptPlan.md](MinimalProofOfConceptPlan.md)). Two game instances,
a Unity editor, and a real install cannot gate an ordinary commit — that would make the boundary rules feel
like an obstacle, which is exactly how boundary rules get deleted.

## 5. Rule registry

Stable ids. **Every automated check must cite one** (`FG-ARCH-010`). Ids are never reused after retirement.

Status values: `Planned` (agreed, unimplemented) → `Implemented` (runs, reports, does not block) →
`Required in CI` (blocks a PR).

---

### FG-ARCH-001 — Core references no outer technology

- **Authority:** [DependencyRules.md §1–§2](DependencyRules.md)
- **Check:** `FalseGods.Core.dll`'s assembly-reference table contains nothing but the .NET BCL. Backed by
  `FalseGods.Core.csproj` referencing no Unity, game, BepInEx, Harmony, A\*, Addressables, or networking DLL.
- **Status:** `Planned`
- **Expected failure message:** `FG-ARCH-001: FalseGods.Core references 'UnityEngine.CoreModule'. Core is
  Unity-less by design — see Docs/DependencyRules.md §2.`
- **Exceptions:** None. This rule has no legitimate exception.

### FG-ARCH-002 — The base plugin does not reference the ST adapter

- **Authority:** [DependencyRules.md §6](DependencyRules.md), [ADR-004](ADRs/ADR-004-Optional-Sulfur-Together-Adapter.md)
- **Check:** read `FalseGods.Plugin.dll`'s metadata references; fail if
  `FalseGods.Integration.SulfurTogether` appears. Metadata, not grep — this is the check a type in a method
  signature cannot hide from.
- **Status:** `Planned`
- **Expected failure message:** `FG-ARCH-002: FalseGods.Plugin references FalseGods.Integration.SulfurTogether.
  The adapter must self-register via FalseGodsIntegrations — see Docs/ADRs/ADR-004.`
- **Exceptions:** None. An exception here silently deletes single-player.

### FG-ARCH-003 — UnityRuntime does not reference Protocol

- **Authority:** [DependencyRules.md §2](DependencyRules.md), [Architecture.md §7](Architecture.md)
- **Check:** `FalseGods.UnityRuntime.dll` has no assembly reference to `FalseGods.Protocol`.
- **Status:** `Planned`
- **Expected failure message:** `FG-ARCH-003: FalseGods.UnityRuntime references FalseGods.Protocol. Presentation
  is driven by PresentationState/PresentationEvent — see Docs/Architecture.md §7.`
- **Exceptions:** None.

### FG-ARCH-004 — Presentation's public API accepts no wire DTO

- **Authority:** [Architecture.md §7](Architecture.md), [DependencyRules.md §2](DependencyRules.md)
- **Check:** reflect over public members of `FalseGods.UnityRuntime` presentation types; assert no parameter,
  return type, generic argument, field, or property resolves to a type in the `FalseGods.Protocol` namespace.
  Strictly stronger than FG-ARCH-003, and it survives the day someone merges the assemblies.
- **Status:** `Planned`
- **Expected failure message:** `FG-ARCH-004: BossPresentation.Apply(BossSnapshot) exposes a wire DTO. Map to
  PresentationState in FalseGods.Application — see Docs/Architecture.md §7.`
- **Exceptions:** None.

### FG-ARCH-005 — ST types and broker access stay at their seams

- **Authority:** [DependencyRules.md §2–§3](DependencyRules.md)
- **Check:** no assembly other than `FalseGods.Integration.SulfurTogether` references `SULFURTogether`,
  `LiteNetLib`, or `Steamworks`. Outside the defining `FalseGods.RuntimeContracts` assembly, only
  `FalseGods.Plugin` and `FalseGods.Integration.SulfurTogether` may reference the `FalseGodsIntegrations`
  broker type. Inspect their member references/call sites as well: the Plugin may subscribe to/read the
  single slot but may not register or revoke; the ST adapter may call `Register(...)` and dispose only its own
  registration token but may not read/resolve the slot. No other assembly may access the broker.
- **Status:** `Planned`
- **Expected failure message:** `FG-ARCH-005: FalseGods.Application references LiteNetLib. Transport is invisible
  above the adapter — see Docs/DependencyRules.md §5.`
- **Exceptions:** None.

### FG-ARCH-006 — Harmony patches live only in Integration.Sulfur

- **Authority:** [DependencyRules.md §5](DependencyRules.md)
- **Check:** no type outside `FalseGods.Integration.Sulfur` carries `[HarmonyPatch]`, and no other assembly
  references `HarmonyLib` (0Harmony).
- **Status:** `Planned`
- **Expected failure message:** `FG-ARCH-006: [HarmonyPatch] found in FalseGods.Integration.SulfurTogether.
  Patches belong in Integration.Sulfur — see Docs/DependencyRules.md §5.`
- **Exceptions:** **Requires a new ADR**, not a suppression comment. The ST adapter reflecting into ST internals
  is *not* an exception to this rule — reflection is not a patch (§5, FG-ARCH-005).

### FG-ARCH-007 — RuntimeContracts stays dependency-light

- **Authority:** [DependencyRules.md §1–§2](DependencyRules.md), [ADR-006](ADRs/ADR-006-Ports-And-Adapters-Boundaries.md)
- **Check:** `FalseGods.RuntimeContracts.dll` references only the BCL and `FalseGods.Core` — never
  `FalseGods.Protocol`, `UnityEngine`, BepInEx, or ST. This is what lets the optional adapter reference it
  cheaply.
- **Status:** `Planned`
- **Expected failure message:** `FG-ARCH-007: FalseGods.RuntimeContracts references FalseGods.Protocol. The
  optional adapter must not drag the wire contract — see Docs/ADRs/ADR-006.`
- **Exceptions:** None.

### FG-ARCH-008 — Boss and Arena wire state/events stay separate

- **Authority:** [ADR-005](ADRs/ADR-005-Snapshot-And-Discrete-Event-Replication.md),
  [MultiplayerLoadingContract.md §5.7](MultiplayerLoadingContract.md)
- **Check:** a `FalseGods.Protocol` test asserting that no public member of `BossSnapshot`/`BossEvent` exposes
  an arena mechanism/hazard/gate type, and none of `ArenaSnapshot`/`ArenaEvent` exposes boss phase/attack/health
  state. `EncounterBaseline` is the only type permitted to hold both.
- **Status:** `Planned`
- **Expected failure message:** `FG-ARCH-008: BossSnapshot.ArenaMechanismState couples the boss protocol to one
  arena's mechanisms — see Docs/ADRs/ADR-005.`
- **Exceptions:** None. `EncounterBaseline` is the designed composition point.

### FG-ARCH-009 — The base plugin loads with the optional adapter absent

- **Authority:** [ADR-004](ADRs/ADR-004-Optional-Sulfur-Together-Adapter.md), [DependencyRules.md §6](DependencyRules.md)
- **Check:** two layers. **(a)** Automated, L1: FG-ARCH-002's metadata assertion, plus a test that constructs the
  Composition Root with no registered `IFalseGodsIntegration` and asserts it builds the single-player
  composition. **(b)** Manual, L3: launch the game with the adapter DLL deleted; assert no `TypeLoadException` /
  `FileNotFoundException` and that single-player plays (RiskList R20/R29, PoC B0).
- **Status:** `Planned`
- **Expected failure message:** `FG-ARCH-009: Composition Root threw with no integration registered. Multiplayer
  absence must degrade, not fail — see Docs/ADRs/ADR-004.`
- **Exceptions:** None.

### FG-ARCH-010 — Every enforced architecture test cites a valid rule id

- **Authority:** this document, §2.4
- **Check:** each architecture test declares a rule id; the set of declared ids is a subset of §5's registry;
  every `Required in CI` rule has at least one test. Catches the two silent rots: a test enforcing a rule nobody
  wrote down, and a rule nobody enforces.
- **Status:** `Planned`
- **Expected failure message:** `FG-ARCH-010: test 'CoreHasNoUnityRef' cites unknown rule 'FG-ARCH-042'. Add it
  to Docs/ArchitectureEnforcement.md §5 or correct the id.`
- **Exceptions:** None.

## 6. Assembly dependency checks

The backbone (FG-ARCH-001/002/003/005/007). Mechanism, cheapest first:

1. **Project references.** The `.csproj` / `.asmdef` graph is the primary enforcement. A missing reference is a
   compile error with a good message and zero runtime cost.
2. **Metadata assertion.** For each shipped assembly, read its `AssemblyRef` table and compare against the
   allow-list in [DependencyRules.md §1](DependencyRules.md). This catches what the project graph cannot: a
   transitively-dragged reference, and a reference that only appears after a merge.
3. **Namespace scan (advisory only).** Useful in review, never a gate on its own. It cannot see generics and it
   fires on comments.

The Unity-side assemblies (`FalseGods.UnityRuntime`) are checked from their `.asmdef` reference list and, once
built, from their compiled metadata — the same two mechanisms, not a special case.

## 7. Public API and type-signature checks

For FG-ARCH-004 and FG-ARCH-008, an assembly reference is too coarse — the question is not *"does UnityRuntime
know Protocol exists"* but *"can a presentation method be handed a snapshot"*.

Reflect over public and protected members and walk the full type graph: parameters, returns, generic arguments,
array/collection element types, fields, properties, events. A `List<BossSnapshot>` parameter must fail
FG-ARCH-004 as surely as a bare `BossSnapshot` does.

These run as ordinary unit tests in the architecture test project, against built assemblies. They need no game,
no Unity, and no network.

## 8. Behavioural architecture tests

Some invariants are not visible in a signature. These are ordinary Unity-less tests, and they are named after
their rule:

- **Presentation is inert without simulation** (RiskList R16/R27): run presentation with the simulation stopped;
  assert it cannot advance phase, deal damage, or mutate authoritative state.
- **Composition Root degrades** (FG-ARCH-009a): build with no registered integration; assert the single-player
  composition and a no-op replication.
- **The broker rejects a duplicate** (ADR-004): register twice; assert the first registration remains
  authoritative, the second is rejected, and disposing the first token clears the slot.
- **Per-stream idempotence** (RiskList R19): replay duplicated and reordered `BossEvent`/`ArenaEvent`; assert no
  duplicated effect and no cross-stream stall.

They belong in the one-minute loop. None of them needs Unity — that is a consequence of the boundaries, and it
is the main practical dividend of drawing them.

## 9. Optional-integration checks

FG-ARCH-009 is the rule this whole architecture is shaped around, so it gets checked at two levels that fail
for different reasons:

- **L1, automated.** Metadata: `FalseGods.Plugin.dll` does not reference the adapter. Behavioural: the
  Composition Root builds and runs with the broker slot empty.
- **L3, manual, pre-release.** Delete `FalseGods.Integration.SulfurTogether.dll` from a real BepInEx install and
  launch. Then delete SULFUR Together instead and launch. Then install both and confirm the adapter registers
  exactly once. This is the only check that exercises BepInEx's actual load ordering, and no unit test
  substitutes for it.

A packaging check at L2 confirms the adapter ships as its **own** BepInEx plugin (its own GUID and manifest),
not as a loose DLL next to the base plugin — the load-ordering guarantee in ADR-004 depends on it.

## 10. Exceptions and ADR process

Most rules in §5 are marked "no exceptions", and that is not decoration — each of those, if excepted, deletes
the property it exists to protect.

Where an exception is genuinely possible (today: FG-ARCH-006 only):

1. It requires an **ADR**, not an inline suppression. A suppression comment records that someone wanted to; an
   ADR records why it was right.
2. The ADR states: **the reason**, the **named owner**, the **cleanup condition** that ends the exception, and
   the **expiry** — a date or a concrete milestone.
3. The exception is registered in this document's §5 entry, so it is visible where the rule is read.
4. An exception without a cleanup condition is not an exception; it is a rule change. Change the rule instead,
   in DependencyRules, where rules live.

## 11. Adding or changing a rule

1. **Change the rule text in [DependencyRules.md](DependencyRules.md)** (or the relevant ADR). That is where
   rules live.
2. **Add a §5 entry here** with the next unused `FG-ARCH-0NN`. Never reuse a retired id.
3. **Implement the check in report mode** (`Implemented`): it runs and prints, and it does not fail the build.
4. **Run it against the whole codebase.** Every hit is either a real violation to fix or a false positive that
   proves the check is wrong. Fix both before proceeding.
5. **Promote to `Required in CI`** once it is quiet on a clean tree and its failure message names the rule id
   and links its authority.

Retiring a rule is the same sequence in reverse, and the id stays burned.

## 12. Performance and false-positive policy

- **Speed.** The L0 loop targets ~1 minute. Architecture tests read metadata from already-built assemblies;
  they do not re-parse source, and they do not launch a game engine.
- **False positives are defects in the check, and they are treated as such.** A developer is **never** expected
  to silently route around a check, restructure correct code to appease a scanner, or paste a suppression to get
  green. If a check fires on code the rule permits: revert the check to report mode, fix it, re-promote.
- A rule that produces repeated false positives is evidence the *rule* is imprecise, not that developers are
  careless. Rewrite it in DependencyRules.
- **Failure messages carry the rule id and a link.** `FG-ARCH-003: ... — see Docs/Architecture.md §7`. A failure
  a developer cannot act on within a minute is a failed check, whatever it found.
- Checks that are slow, flaky, or dependent on a game install do not run at L0 or L1. They live at L3.

## 13. Current implementation status

| Rule | Check exists? | Status |
|---|---|---|
| FG-ARCH-001 … FG-ARCH-010 | No | `Planned` |

Also not created, by design, in this documentation pass:

- `scripts/verify.ps1`
- any `.csproj`, `.sln`, or `.asmdef`
- any test project
- any CI workflow

The first implementation task is the module skeleton with restricted project references
([Architecture.md §2](Architecture.md)) — because the compiler enforces FG-ARCH-001/003/007 for free the moment
those files exist, before a single test is written. Metadata assertions (FG-ARCH-002/005) come next, since they
guard the property that most quietly breaks: single-player.
