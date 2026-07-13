# Architecture Enforcement

*How the architecture rules are checked, when the checks run, what state each one is in, and how a rule is
added, excepted, or retired.*

**Partially implemented.** The module skeleton's restricted project references give the compiler protection
described in §5; **five rules have working automated checks** — `FG-ARCH-002`, `FG-ARCH-003`, `FG-ARCH-005`,
`FG-ARCH-006` and `FG-ARCH-010` — run by `.\scripts\verify.ps1`; a **CI workflow**
(`.github/workflows/verify.yml`) runs the game-independent subset on every push and PR; and the **local
pre-push hook runs the full `verify.ps1` and blocks a red push**, so each of those five has at least one layer
**`Required in CI`** (the term's meaning is pinned in §4.1: run in CI *and* enforced by the pre-push hook —
no longer a server-side merge gate, since branch protection was removed).

**A rule is not a single check, and its status is not a single word.** Most rules are verified at more than
one layer — the evaluated project graph, the compiled assembly metadata, a public-API scan — and the layers
run in different places and get promoted at different times. `FG-ARCH-006`'s reference layer gates every
merge while the `[HarmonyPatch]` scan its rule also demands does not exist. Statuses are therefore recorded
**per layer** (§5.1), never per rule, so that "checked" can never be read as more than it is. Five rules have
an enforced layer; **no rule is fully enforced end-to-end.** See §5 for each rule and §13 for the picture.

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
5. **A new rule is `Implemented` before it is `Required in CI`.** A rule that has never run against the real
   codebase has never been tested, and making it mandatory on day one turns its first false positive into
   everyone's problem. See §11.
6. **The fast local loop stays fast.** See §3.
7. **Enforcement serves the design, not the reverse.** If a rule is expensive, noisy, or frequently excepted,
   the rule is wrong. Fix the rule; do not train developers to route around it.

## 3. Local verification entry point

One command:

```powershell
.\scripts\verify.ps1
```

Optionally `-Configuration Release`; the default is `Debug`. It runs, in order, and stops at the first failure:

```text
1. SDK + configuration    (the version pinned by global.json; -Configuration is one the projects declare)
2. dotnet build           (restricted project references — the compiler catches most violations)
3. Architecture tests     (the FG-ARCH-* checks, told which configuration was just built)
4. Whitespace checks      (git diff HEAD --check, staged AND unstaged; plus the committed range
                           BaseRef...HEAD — default origin/master — the same range CI checks on a PR)
```

A few details that are load-bearing rather than incidental:

- **Success and failure are decided by process exit codes, never by searching output for the word "error".**
  This repository's build prints `0 个错误` on success, so a grep for `错误` reports a passing build as broken —
  and a check that is wrong about its subject is worse than no check (§12).
- **The configuration is validated, not assumed.** The allowed set is read from MSBuild's evaluated
  `$(Configurations)` (declared in `Directory.Build.props`), and an undeclared one is rejected. MSBuild will
  cheerfully build `-c Nonsense`, and every reference guarded by a real configuration would then go unchecked
  while the run stayed green.
- **The configuration is passed to the tests** through `FALSEGODS_VERIFY_CONFIGURATION`, so the metadata check
  inspects the assembly that was just built and never a stale one from the other configuration (§6.2).
- **`git diff HEAD --check`, not `git diff --check`.** The latter sees only *unstaged* changes, so whitespace
  damage that was already `git add`-ed passes the very check meant to catch it before a commit. `HEAD` covers
  staged and unstaged modifications to tracked files. It does **not** cover untracked files — they have no
  diff; `git add` brings them into scope.
- **The committed range is checked too** (`BaseRef...HEAD`, default `origin/master`, override with
  `-BaseRef`). `git diff HEAD --check` is empty right after a commit, so damage in an already-committed file
  would pass every local gate and die only in CI's PR-range step — PR #6 did exactly that (a Unity
  `ProjectSettings` file with Unity's trailing-space YAML, committed before the matching `.gitattributes`
  exemption was broad enough). If no merge base with `BaseRef` exists (a clone that never fetched), the range
  check is skipped with an explicit notice rather than silently passing.
- Native commands are judged only by exit code, with `$ErrorActionPreference` relaxed around them. In Windows
  PowerShell 5.1 a native command's redirected stderr becomes a terminating error, and `git` writes harmless
  CRLF advisories there — so the script would "fail" on a clean tree, but only when its output was piped. A
  verification script whose result depends on whether someone redirected it is not a verification script.

**Budget: about one minute.** It currently takes roughly twenty seconds. Anything slower does not belong in the
pre-commit loop; it belongs in §4's higher levels. When this budget is threatened, move a check outward — do
not let the loop grow until people stop running it.

Explicitly *not* in this command: launching Unity, launching SULFUR, and anything needing two game instances.

The build step passes `--disable-build-servers -m:1` so the loop also works in constrained or sandboxed
environments, where persistent MSBuild and Roslyn server processes are unavailable.

`.\scripts\verify.ps1 -CiSafe` runs the game-independent subset — inner assemblies + the checks that need no
compiled outer DLL. It is what CI runs; you run the full form. See §4.1 for exactly what the subset drops and
why.

## 4. CI enforcement levels

| Level | Runs | Contains | Blocking? |
|---|---|---|---|
| **L0 — local** | `.\scripts\verify.ps1`, before commit | full build (incl. outer assemblies), architecture tests, whitespace checks (working tree + committed range vs `origin/master`) | Developer's own loop |
| **L1 — CI** | `.github/workflows/verify.yml` on push + PR | `verify.ps1 -CiSafe` (inner build + the game-independent checks) + a PR-range whitespace check against the PR's actual base ref | See §4.1 |
| **L2 — packaging** | on tag / release build | L1 + the package check (no vanilla assets in the bundle; adapter packaged as its own plugin) | Not built yet |
| **L3 — manual / pre-release** | by hand, before a release | in-game probes, single-player smoke test, **host + client** two-instance validation, adapter-DLL-deleted launch | Release gate, **not** a per-commit gate |

L3 is where the PoC lives ([MinimalProofOfConceptPlan.md](MinimalProofOfConceptPlan.md)). Two game instances,
a Unity editor, and a real install cannot gate an ordinary commit — that would make the boundary rules feel
like an obstacle, which is exactly how boundary rules get deleted.

### 4.1 What CI covers, and what it deliberately cannot

CI is `verify.ps1 -CiSafe`: it builds the **inner** assemblies (Core / Protocol / RuntimeContracts /
Application, which need no game) and the test project, and runs every architecture check except those that
read a compiled **outer** assembly. The runner has no SULFUR or BepInEx DLLs — those must never be committed —
so it cannot build UnityRuntime / Integration.* / Plugin.

| | Covered by CI | Why / why not |
|---|---|---|
| Inner assemblies build with no game | ✅ | The property CI exists to guard; the compiler enforces FG-ARCH-001/007 there. |
| **Project-graph layer of FG-ARCH-002/003/005/006** | ✅ | MSBuild *evaluation* needs no DLLs (report §6, §6.2); it catches an added-but-unused reference — the common mistake. Evaluation runs no targets, so the outer projects' `RequiresGameAssemblies` guard in `Directory.Build.targets` never fires and their graphs are readable with no game installed. |
| **FG-ARCH-010** and all self-tests | ✅ | Pure logic / evaluation / in-memory Roslyn; no game. |
| Probe isolation checks | ✅ | Project-graph evaluation only. |
| **FG-ARCH-002 metadata layer** | ❌ | Reads `FalseGods.Plugin.dll`, an outer assembly CI cannot build. Stays L0 + L3. |
| **Metadata layer of FG-ARCH-003/005/006** | ❌ | Not written. It is what would catch a *transitive* dependency, which no evaluated project graph can see (§6). Reads outer DLLs, so it would be L0/L3 anyway. |
| Outer-assembly compiler protection (FG-ARCH-003/006) | ❌ | CI does not build the outer assemblies, so their *compile-time* protection is a **local-only** guarantee (L0's full build). The reference-graph checks now cover the gap the compiler leaves — *adding* a forbidden reference — but not the gap only compilation closes. |

So CI enforces the **first line of defence** for the rules it can (evaluation + inner build); the outer
assemblies' compile-time protection and every metadata layer remain L0/L3. This is the split from report
§6.2, made concrete.

**What the project-graph layer does and does not prove.** It reads the references a project *itself*
declares. It is the layer that sees a reference which is present but unused — which compiles green, emits no
`AssemblyRef`, ships, and fails at type-load on a machine without the DLL. It is blind to a dependency
reached *through* another assembly's references. So "FG-ARCH-006 is `Required in CI`" means CI rejects a
project that names `0Harmony`; it does not mean CI would notice a `[HarmonyPatch]` attribute, and the
registry says so in as many words (§5.1).

**"Blocking" is the local pre-push hook, not branch protection.** Branch protection was removed: `master` is
pushed directly, there is no required server-side check, and CI (`verify.yml`, which runs on every push and PR)
is a **visible re-check, not a merge gate**. What actually blocks a red change from reaching `master` is the
pre-push hook, which runs the full `verify.ps1` in **both Debug and Release** and refuses the push on failure
(which is why the hook verifies both configurations — see the hook's own comment). So in this registry
**`Required in CI` means: the check runs in CI *and* is enforced by the pre-push hook** — a client-side gate a
`git push --no-verify` or an un-hooked clone can bypass, weaker than a server-side required check. The checks
CI actually runs (§4.1) are `Required in CI`; the ones it cannot run stay L0/L3.

## 5. Rule registry

Stable ids. **Every automated check must cite one** (`FG-ARCH-010`). Ids are never reused after retirement.

Each rule carries **two independent facts**, because they protect against different things and change at
different times.

**Check status** — the state of one **check layer**. A rule is checked at one or more layers, and each layer
carries its own status:

- `Planned` — agreed, no check exists.
- `Implemented` — **a check exists and fails when explicitly run** (via `.\scripts\verify.ps1` or `dotnet test`).
  It is not yet mandatory on every PR.
- `Required in CI` — the check is **mandatory on every pull request**.

A rule's status is the list of its layers' statuses, never a single word. Collapsing them forces a choice
between two lies: call `FG-ARCH-002` `Required in CI` and you claim CI catches a `typeof()` it cannot see;
call it `Implemented` and you hide that a red CI already blocks the merge. Layer names are shared across rules
wherever the technique is shared, because those layers fail for the same reasons and are promoted for the same
reasons:

| Layer | What it reads | Sees | Blind to |
|---|---|---|---|
| `project graph` | MSBuild's evaluated `ProjectReference` / `Reference` / `HintPath` | a reference that is present but **unused**; one added via an import or behind a condition | anything reached *through* another assembly |
| `assembly metadata` | the compiled `AssemblyRef` table | a real CLR dependency from a signature, `typeof()`, base class, or a transitive drag | a reference that is present but unused |
| `public API signatures` | reflection over public/protected members | a wire DTO in a presentation signature, including inside `List<T>` | anything non-public |
| `broker access` | member references and call sites | who registers with, revokes, or reads `FalseGodsIntegrations` | — (not written) |
| `patch attribute scan` | attributes on types | `[HarmonyPatch]` outside `Integration.Sulfur` | — (not written) |
| `composition root behaviour` | a running Composition Root | degradation with no integration registered | BepInEx load ordering (that is L3) |
| `registry consistency` | this document and the registry | drift between rule text, registry, and checks | whether a rule is *right* |

### 5.1 Layer status (machine-checked)

The authoritative, machine-readable projection of every rule's layers. `FG-ARCH-010` asserts that this table
and `ArchitectureRuleRegistry` agree **row for row** — a layer promoted in code but not here, or here but not
in code, fails the build. The markers around the table are load-bearing; the check fails if it cannot find
them, because an absent table would otherwise compare equal to an empty registry and silently verify nothing.

That check exists because the drift it catches had already happened: this document called `FG-ARCH-002`
`Required in CI` while the registry called it `Implemented`, and nothing noticed, because the two only ever
compared their sets of rule *ids*.

<!-- BEGIN FG-ARCH-LAYER-STATUS -->

| Rule | Check layer | Check status |
|---|---|---|
| FG-ARCH-001 | assembly metadata | Planned |
| FG-ARCH-002 | project graph | Required in CI |
| FG-ARCH-002 | assembly metadata | Implemented |
| FG-ARCH-003 | project graph | Required in CI |
| FG-ARCH-003 | assembly metadata | Planned |
| FG-ARCH-004 | public API signatures | Planned |
| FG-ARCH-005 | project graph | Required in CI |
| FG-ARCH-005 | assembly metadata | Planned |
| FG-ARCH-005 | broker access | Planned |
| FG-ARCH-006 | project graph | Required in CI |
| FG-ARCH-006 | assembly metadata | Planned |
| FG-ARCH-006 | patch attribute scan | Planned |
| FG-ARCH-007 | assembly metadata | Planned |
| FG-ARCH-008 | public API signatures | Planned |
| FG-ARCH-009 | composition root behaviour | Planned |
| FG-ARCH-010 | registry consistency | Required in CI |

<!-- END FG-ARCH-LAYER-STATUS -->

**Read this table as the answer to "what would actually stop me".** Of the sixteen layers, five are
`Required in CI`, one is `Implemented`, and ten are `Planned`. **No rule is enforced at every layer it names.**

**Compiler protection** — what the `.csproj` reference graph gives you for free today, independent of any check:

- `Full` — using the forbidden type is a compile error, because the reference is absent.
- `Partial` — some forms of the violation are compile errors; others are not.
- `None` — the violation compiles.

**Compiler protection has a precise limit, and it is the reason checks still matter.** The compiler stops you
from *using* a forbidden type, because the reference is not there. It does **not** stop you from *adding the
reference*. A developer who types `<ProjectReference Include="..\FalseGods.Protocol\..." />` into
`FalseGods.UnityRuntime` gets a green build. Closing that gap is what the project-graph and metadata checks are
for — and it is why a rule with `Full` compiler protection can still have a `Planned` check.

Symmetrically, **a check can see what the compiler cannot**: an unused forbidden reference compiles green,
never reaches the compiled `AssemblyRef` table, and still breaks at type-load on a machine where the DLL is
absent. That is why FG-ARCH-002 is checked at two layers, not one.

---

<a id="fg-arch-001"></a>

### FG-ARCH-001 — Core references no outer technology

- **Authority:** [DependencyRules.md §1–§2](DependencyRules.md)
- **Check:** `FalseGods.Core.dll`'s assembly-reference table contains nothing but the .NET BCL. Backed by
  `FalseGods.Core.csproj` referencing no Unity, game, BepInEx, Harmony, A\*, Addressables, or networking DLL.
- **Check status:** `Planned`
- **Compiler protection:** `Full`. `src/FalseGods.Core/FalseGods.Core.csproj` declares no reference of any kind.
  A `using UnityEngine;` in Core fails with `CS0246`. Core also builds on a machine with no game installed —
  verified by forcing `SulfurManagedDir` empty.
- **Expected failure message:** `FG-ARCH-001: FalseGods.Core references 'UnityEngine.CoreModule'. Core is
  Unity-less by design — see Docs/DependencyRules.md §2.`
- **Exceptions:** None. This rule has no legitimate exception.

<a id="fg-arch-002"></a>

### FG-ARCH-002 — The base plugin does not reference the ST adapter

- **Authority:** [DependencyRules.md §6](DependencyRules.md), [ADR-004](ADRs/ADR-004-Optional-Sulfur-Together-Adapter.md)
- **Check:** two layers, because each is blind to what the other catches.
  1. **Project graph.** Evaluate `FalseGods.Plugin.csproj` with MSBuild (`-getItem:ProjectReference,Reference`,
     for every `Configuration`) and fail if any evaluated `ProjectReference`, `Reference`, or `HintPath`
     resolves to the adapter — *including one added through an import or behind a condition*. This is the layer
     that sees a reference which is **present but unused**. Such a reference compiles green, emits no
     `AssemblyRef`, ships, and then fails at type-load on a machine without SULFUR Together.
  2. **Assembly metadata.** Read `FalseGods.Plugin.dll`'s `AssemblyRef` table and fail if the adapter appears.
     This is the layer a type in a method signature, a `typeof()`, a base class, or a static-initialization
     path cannot hide from, whatever the csproj says.

  Neither layer is a text search. The first is MSBuild's own evaluation, so imports and conditions are
  resolved; the second is CLI metadata, so generics and indirection are resolved.
- **Check status:** `project graph` = `Required in CI`; `assembly metadata` = `Implemented` (§5.1). The
  metadata layer does not run in CI — it reads the outer `Plugin.dll`, which CI cannot build (§4.1) — so it
  stays at L0/L3. The local pre-push hook runs both layers (full verify) before every push.
- **Compiler protection:** `Full`. `src/FalseGods.Plugin/FalseGods.Plugin.csproj` references
  `Integration.Sulfur` and not `Integration.SulfurTogether`; naming an adapter type from the Plugin fails with
  `CS0234`. **This is the rule whose residual gap matters most** — the compiler cannot stop someone from adding
  the reference, and the resulting breakage appears only on a machine without SULFUR Together.
- **Implementation:** `tests/FalseGods.ArchitectureTests/Checks/PluginDoesNotReferenceStAdapterChecks.cs`.
  Proven by fixtures in `tests/Fixtures/`, not by temporarily breaking the real project.
- **Expected failure message:** `FG-ARCH-002: FalseGods.Plugin references FalseGods.Integration.SulfurTogether.
  … See Docs/ArchitectureEnforcement.md#fg-arch-002`
- **Exceptions:** None. An exception here silently deletes single-player.

<a id="fg-arch-003"></a>

### FG-ARCH-003 — UnityRuntime does not reference Protocol

- **Authority:** [DependencyRules.md §2](DependencyRules.md), [Architecture.md §7](Architecture.md)
- **Check:** two layers.
  1. **Project graph.** `FalseGods.UnityRuntime.csproj`, evaluated for every declared `Configuration`,
     declares no `ProjectReference`, `Reference`, or `HintPath` resolving to `FalseGods.Protocol` — including
     one added through an import or behind a condition.
  2. **Assembly metadata.** `FalseGods.UnityRuntime.dll`'s `AssemblyRef` table does not name
     `FalseGods.Protocol`. This is the layer that would see a Protocol type dragged in transitively, which no
     project-graph evaluation can.
- **Check status:** `project graph` = `Required in CI`; `assembly metadata` = `Planned` (§5.1). The metadata
  layer would read an outer assembly CI cannot build, so it belongs to L0/L3 when written.
- **Compiler protection:** `Full`. `src/FalseGods.UnityRuntime/FalseGods.UnityRuntime.csproj` references Core
  and RuntimeContracts only. `BossPresentation.Apply(BossSnapshot)` — the exact signature FG-ARCH-004 exists to
  reject — fails with `CS0234`. The compiler cannot stop someone *adding* the reference; the project-graph
  layer is what closes that gap.
- **Implementation:** `tests/FalseGods.ArchitectureTests/Checks/UnityRuntimeDoesNotReferenceProtocolChecks.cs`,
  over the shared `Inspection/ForbiddenReferenceScanner.cs`. Proven by the `ForbiddenProtocolReference`
  fixture, not by temporarily breaking the real project.
- **Expected failure message:** `FG-ARCH-003: FalseGods.UnityRuntime's evaluated MSBuild project graph
  references FalseGods.Protocol … Presentation is driven by PresentationState/PresentationEvent, mapped in
  FalseGods.Application — see Docs/Architecture.md §7.`
- **Exceptions:** None.

<a id="fg-arch-004"></a>

### FG-ARCH-004 — Presentation's public API accepts no wire DTO

- **Authority:** [Architecture.md §7](Architecture.md), [DependencyRules.md §2](DependencyRules.md)
- **Check:** reflect over public members of `FalseGods.UnityRuntime` presentation types; assert no parameter,
  return type, generic argument, field, or property resolves to a type in the `FalseGods.Protocol` namespace.
  Strictly stronger than FG-ARCH-003, and it survives the day someone merges the assemblies.
- **Check status:** `Planned`
- **Compiler protection:** `Partial`. FG-ARCH-003 makes today's violation uncompilable, so this check is
  defence in depth: it is what still holds if someone adds the Protocol reference to UnityRuntime.
- **Expected failure message:** `FG-ARCH-004: BossPresentation.Apply(BossSnapshot) exposes a wire DTO. Map to
  PresentationState in FalseGods.Application — see Docs/Architecture.md §7.`
- **Exceptions:** None.

<a id="fg-arch-005"></a>

### FG-ARCH-005 — ST types and broker access stay at their seams

- **Authority:** [DependencyRules.md §2–§3](DependencyRules.md)
- **Check:** three layers, and only the first exists.
  1. **Project graph.** No project under `src/` other than `FalseGods.Integration.SulfurTogether` declares a
     `ProjectReference`, `Reference`, or `HintPath` resolving to SULFUR Together, LiteNetLib, or Steamworks,
     in any declared `Configuration`. The scanned set is **read from `src/` on disk**, so a project added
     tomorrow is covered without editing the check.

     The forbidden names are **assembly identities**, verified against the real projects rather than copied
     from prose — and prose is where this goes wrong. `SULFURTogether` and `Steamworks` are *namespaces*;
     no assembly is called either. ST's `AssemblyName` is **`SULFUR Together`**, with a space, and the
     Steamworks.NET assembly shipped in SULFUR's `Managed` directory is **`com.rlabrecque.steamworks.net`**.
     A check that forbade only the namespace spellings would match nothing, forever, while reporting green.
     Both spellings are forbidden anyway, as cheap aliases: a hand-written `<Reference Include="Steamworks">`
     should fail this rule, not `MSB3245`.
  2. **Assembly metadata.** No compiled assembly but the adapter has an `AssemblyRef` to those identities.
     This is the layer that would see a transitive drag. `Planned`.
  3. **Broker access.** Outside the defining `FalseGods.RuntimeContracts` assembly, only `FalseGods.Plugin`
     and `FalseGods.Integration.SulfurTogether` may reference the `FalseGodsIntegrations` broker type.
     Inspect their member references/call sites as well: the Plugin may subscribe to/read the single slot but
     may not register or revoke; the ST adapter may call `Register(...)` and dispose only its own registration
     token but may not read/resolve the slot. No other assembly may access the broker. `Planned` — the broker
     type does not exist yet.
- **Check status:** `project graph` = `Required in CI`; `assembly metadata` = `Planned`; `broker access` =
  `Planned` (§5.1). **A green project-graph layer is not a green rule.** It says no project outside the
  adapter *names* a transport assembly. It says nothing about the broker.
- **Compiler protection:** `Partial`. The *assembly* half is currently true by construction — no project
  references ST, LiteNetLib, or Steamworks, not even `Integration.SulfurTogether`, which reaches ST's
  `internal` types by reflection. The *broker access* half cannot be checked until the broker type exists.
- **Implementation:** `tests/FalseGods.ArchitectureTests/Checks/TransportTypesStayInTheStAdapterChecks.cs`,
  over the shared `Inspection/ForbiddenReferenceScanner.cs`. Proven by the `ForbiddenTransportReference`
  fixture, which carries LiteNetLib in every configuration and `SULFUR Together` only under Release.
- **Expected failure message:** `FG-ARCH-005: a project outside FalseGods.Integration.SulfurTogether references
  a transport or SULFUR Together assembly. … FalseGods.Application [Debug] references LiteNetLib … Transport is
  invisible above the adapter — see Docs/DependencyRules.md §5.`
- **Exceptions:** None.

<a id="fg-arch-006"></a>

### FG-ARCH-006 — Harmony patches live only in Integration.Sulfur

- **Authority:** [DependencyRules.md §5](DependencyRules.md)
- **Check:** three layers, and only the first exists. The rule makes **two distinct claims**, and it is worth
  separating them: *no other project references Harmony*, and *no type outside `Integration.Sulfur` carries
  `[HarmonyPatch]`*. The second does not follow from the first.
  1. **Project graph.** No project under `src/` other than `FalseGods.Integration.Sulfur` declares a reference
     resolving to `0Harmony` (assembly) or `HarmonyLib` (its namespace, forbidden as an alias), in any declared
     `Configuration`. A `HintPath` ending in `0Harmony.dll` is matched whatever the `Include` identity says.
     The scanned set is read from `src/` on disk, so a new project is covered automatically.
  2. **Assembly metadata.** No compiled assembly but `Integration.Sulfur` has an `AssemblyRef` to `0Harmony`.
     `Planned`.
  3. **Patch attribute scan.** No type outside `FalseGods.Integration.Sulfur` carries `[HarmonyPatch]`.
     `Planned`. Today no other project can even resolve the attribute — precisely because layer 1 holds — but
     "cannot resolve it" and "does not carry it" are different claims, and only the first is checked.
- **Check status:** `project graph` = `Required in CI`; `assembly metadata` = `Planned`; `patch attribute scan`
  = `Planned` (§5.1). **CI rejects a project that names `0Harmony`. CI does not look for `[HarmonyPatch]`.**
- **Compiler protection:** `Full`. `0Harmony.dll` is referenced by `FalseGods.Integration.Sulfur` alone, so
  `[HarmonyPatch]` does not resolve anywhere else. Note `Integration.SulfurTogether` does **not** reference
  0Harmony — it reflects into ST, and reflection is not a patch.
- **Implementation:** `tests/FalseGods.ArchitectureTests/Checks/HarmonyStaysInIntegrationSulfurChecks.cs`, over
  the shared `Inspection/ForbiddenReferenceScanner.cs`. Proven by the `ForbiddenHarmonyReference` fixture,
  whose `Reference` identity names something else entirely and betrays itself only through its `HintPath`. A
  further check asserts `Integration.Sulfur` *does* reference `0Harmony`: were Harmony renamed or dropped, the
  forbidden name would match nothing anywhere and the rule would pass while checking for a ghost.
- **Expected failure message:** `FG-ARCH-006: a project outside FalseGods.Integration.Sulfur references Harmony.
  … FalseGods.Plugin [Release] references 0Harmony … Patches belong in Integration.Sulfur — see
  Docs/DependencyRules.md §5.`
- **Exceptions:** **Requires a new ADR**, not a suppression comment. The ST adapter reflecting into ST internals
  is *not* an exception to this rule — reflection is not a patch (§5, FG-ARCH-005).

<a id="fg-arch-007"></a>

### FG-ARCH-007 — RuntimeContracts stays dependency-light

- **Authority:** [DependencyRules.md §1–§2](DependencyRules.md), [ADR-006](ADRs/ADR-006-Ports-And-Adapters-Boundaries.md)
- **Check:** `FalseGods.RuntimeContracts.dll` references only the BCL and `FalseGods.Core` — never
  `FalseGods.Protocol`, `UnityEngine`, BepInEx, or ST. This is what lets the optional adapter reference it
  cheaply.
- **Check status:** `Planned`
- **Compiler protection:** `Full`. `src/FalseGods.RuntimeContracts/FalseGods.RuntimeContracts.csproj` references
  Core alone. Naming a `FalseGods.Protocol` type from RuntimeContracts fails with `CS0234`.
- **Expected failure message:** `FG-ARCH-007: FalseGods.RuntimeContracts references FalseGods.Protocol. The
  optional adapter must not drag the wire contract — see Docs/ADRs/ADR-006.`
- **Exceptions:** None.

<a id="fg-arch-008"></a>

### FG-ARCH-008 — Boss and Arena wire state/events stay separate

- **Authority:** [ADR-005](ADRs/ADR-005-Snapshot-And-Discrete-Event-Replication.md),
  [MultiplayerLoadingContract.md §5.7](MultiplayerLoadingContract.md)
- **Check:** a `FalseGods.Protocol` test asserting that no public member of `BossSnapshot`/`BossEvent` exposes
  an arena mechanism/hazard/gate type, and none of `ArenaSnapshot`/`ArenaEvent` exposes boss phase/attack/health
  state. `EncounterBaseline` is the only type permitted to hold both.
- **Check status:** `Planned`
- **Compiler protection:** `None` — the Protocol types do not exist yet.
- **Expected failure message:** `FG-ARCH-008: BossSnapshot.ArenaMechanismState couples the boss protocol to one
  arena's mechanisms — see Docs/ADRs/ADR-005.`
- **Exceptions:** None. `EncounterBaseline` is the designed composition point.

<a id="fg-arch-009"></a>

### FG-ARCH-009 — The base plugin loads with the optional adapter absent

- **Authority:** [ADR-004](ADRs/ADR-004-Optional-Sulfur-Together-Adapter.md), [DependencyRules.md §6](DependencyRules.md)
- **Check:** two layers. **(a)** Automated, L1: FG-ARCH-002's metadata assertion, plus a test that constructs the
  Composition Root with no registered `IFalseGodsIntegration` and asserts it builds the single-player
  composition. **(b)** Manual, L3: launch the game with the adapter DLL deleted; assert no `TypeLoadException` /
  `FileNotFoundException` and that single-player plays (RiskList R20/R29, PoC B0).
- **Check status:** `Planned`. The reference half is already covered by FG-ARCH-002's two layers; the
  behavioural half needs a Composition Root to exist.
- **Compiler protection:** `Partial`, via FG-ARCH-002. Note the skeleton already demonstrates the shape:
  `FalseGods.Integration.SulfurTogether` compiles with SULFUR Together not installed at all.
- **Expected failure message:** `FG-ARCH-009: Composition Root threw with no integration registered. Multiplayer
  absence must degrade, not fail — see Docs/ADRs/ADR-004.`
- **Exceptions:** None.

<a id="fg-arch-010"></a>

### FG-ARCH-010 — Every enforced architecture test cites a valid rule id

- **Authority:** this document, §2 principle 4
- **Check:** each architecture check declares its rule id **structurally** — an
  `[ArchitectureRule("FG-ARCH-0NN")]` attribute, never a substring of a test name — and the check asserts:
  1. every cited id exists in the registry (catches *a check enforcing a rule nobody wrote down*);
  2. every rule with **at least one layer** at `Implemented` or `Required in CI` is cited by at least one check
     (catches *a rule quietly declared done that nothing enforces*);
  3. ids are well-formed and never reused;
  4. the registry and this document's §5 headings contain **the same set of ids** (catches drift between the
     authority and its machine-readable projection);
  5. every rule has an `<a id="fg-arch-0nn"></a>` anchor here, so the link each failure message prints actually
     resolves;
  6. §5.1's layer-status table and the registry agree **row for row** — same rules, same layers, same statuses
     (catches *a layer promoted in one place and not the other*, which had already happened once). The table
     must be found between its markers; an absent table fails rather than comparing equal to nothing.
- **Check status:** `registry consistency` = `Required in CI` (§5.1). It needs no game, runs fully in
  `verify.ps1 -CiSafe`, and is a required merge check (§4.1).
- **Compiler protection:** `None`. Nothing about a missing rule id is a compile error.
- **Implementation:** `tests/FalseGods.ArchitectureTests/Checks/RuleRegistryChecks.cs`, with the logic in
  `Rules/RuleRegistryValidator.cs` kept as pure functions so it can be tested against a deliberately wrong
  registry — the real one, being correct, can never demonstrate that the validator would notice if it were not.
- **Expected failure message:** `FG-ARCH-010: checks cite rule ids that no registry entry defines: FG-ARCH-042.
  … See Docs/ArchitectureEnforcement.md#fg-arch-010`
- **Exceptions:** None.

## 6. Assembly dependency checks

The backbone (FG-ARCH-001/002/003/005/007). **Two layers, and neither alone is sufficient** — this is the
central lesson of implementing FG-ARCH-002, and every later dependency rule should copy the shape:

1. **Evaluated project graph.** Ask MSBuild what the project's references actually are, via
   `dotnet msbuild <proj> -getProperty:Configurations -getItem:ProjectReference -getItem:Reference`.

   Not a regex over the `.csproj`. The XML is not the graph: items arrive from imported files
   (`Directory.Build.props`, the SDK) and item conditions depend on evaluated properties, so a text search sees
   neither — and misses exactly the interesting violations. MSBuild's evaluated output sees both, and reports
   each item's `DefiningProjectFullPath`, so a failure can name the *file the reference actually came from*.

   **This layer alone can see a reference that is present but unused.** Such a reference compiles green, emits
   no `AssemblyRef`, ships, and fails at type-load on a machine where the DLL is absent.

2. **Compiled `AssemblyRef` table.** Read the built assembly's metadata (`System.Reflection.Metadata`, no
   loading) and compare against the allow-list in [DependencyRules.md §1](DependencyRules.md).

   **This layer alone can see a real CLR dependency** introduced through a method signature, a `typeof()`, a
   base class, a field, or a static-initialization path — regardless of what the `.csproj` claims. It also
   catches a transitively-dragged reference.

3. **Namespace scan (advisory only).** Useful in review, never a gate. It cannot see generics and it fires on a
   type name inside a comment.

The Unity-side assemblies (`FalseGods.UnityRuntime`) are checked the same way, from their reference list and
their compiled metadata — not a special case.

Implementation: `tests/FalseGods.ArchitectureTests/Inspection/ProjectGraphInspector.cs` and
`Inspection/AssemblyReferenceInspector.cs`.

### 6.2 Two ways a dependency check quietly stops checking

Both were real defects in the first implementation, and both produce a green result while inspecting nothing:

**Evaluating the wrong set of configurations.** A reference behind `Condition="'$(Configuration)' == 'Release'"`
is invisible in a Debug evaluation. The configuration list is therefore **read from MSBuild's evaluated
`$(Configurations)`** — declared once in `Directory.Build.props` — and never hardcoded in the checks. Every
declared configuration is evaluated; an undeclared one is rejected rather than evaluated, because MSBuild will
evaluate `Nonsense` without complaint and report a graph with none of the real conditions satisfied. A test
asserts every production project declares the same set.

**Inspecting the wrong build output.** `verify.ps1 -Configuration Release` builds Release; a lookup that tries
`bin/Debug` first and falls back would then read a stale Debug DLL from a previous run and report on an artefact
nobody just built. So `BuildOutputLocator` takes the configuration explicitly and **never falls back**, the
configuration reaches the tests through `FALSEGODS_VERIFY_CONFIGURATION`, and a missing assembly is a failure
naming the exact path and where the configuration came from.

Both are covered by fixtures in §6.1. A third defect of the same family — a "timeout" placed *after* a blocking
`ReadToEnd`, so it can never fire — is why `Inspection/ProcessRunner.cs` exists: it drains stdout and stderr
concurrently, enforces a real deadline, kills the whole process tree, and still recovers the output produced
before the kill. Killing only the direct child leaves grandchildren holding the inherited pipe handles, and the
reads never complete.

### 6.1 The checkers are themselves tested

A check that has never been shown to fail is a check nobody has tested. The obvious way to test one — briefly
add a forbidden reference to the real project, build, look at the output — proves it works once, on one
machine, and leaves the production project one forgotten `git checkout` away from committing the violation it
was demonstrating.

So each layer is proven against fixtures instead, and nothing temporary is ever written into `src/`:

| Fixture | Proves |
|---|---|
| `tests/Fixtures/AllowedGraph` | A conforming graph passes — the checker is not always-fail. |
| `tests/Fixtures/ForbiddenProjectReference` | An **unused** forbidden `ProjectReference` is caught. The fixture has no source files, so nothing about it could ever reach an `AssemblyRef` table. This is the case only layer 1 can see. |
| `tests/Fixtures/ForbiddenReferenceViaImport` | A forbidden `Reference` injected by an **imported** `.props`, behind a **condition**, is caught — and the failure names the `.props` file. This is the case a regex over the `.csproj` would pass. |
| `tests/Fixtures/ForbiddenReferenceInReleaseOnly` | A forbidden `Reference` present **only when `Configuration == Release`** is caught. Invisible under MSBuild's default configuration; found because the checks evaluate every *declared* configuration. |
| `tests/Fixtures/ForbiddenProtocolReference` | FG-ARCH-003: an **unused** `ProjectReference` from a presentation-shaped project to `FalseGods.Protocol`. No source files, so nothing could reach an `AssemblyRef` table. |
| `tests/Fixtures/ForbiddenTransportReference` | FG-ARCH-005: `LiteNetLib` in every configuration, and `SULFUR Together` — the real assembly identity, **with a space** — only under Release. |
| `tests/Fixtures/ForbiddenHarmonyReference` | FG-ARCH-006: a `Reference` whose `Include` identity names something unrelated and whose **`HintPath` alone** reveals `0Harmony.dll`. An identity-only comparison would pass it. |
| Hand-built graphs (`SelfTests/ForbiddenReferenceScannerSelfTests.cs`) | The scanner's reporting logic, with no MSBuild and no disk: case-insensitivity, an identity containing a space, every configuration and every forbidden name reported rather than the first, and an **empty forbidden list throwing** instead of passing. |
| Roslyn, in memory (`SelfTests/SyntheticAssembly.cs`) | A synthetic assembly whose `AssemblyRef` table contains the forbidden name is caught; a clean one is not. Nothing touches disk. |
| Synthetic directory tree (`SelfTests/BuildOutputLocatorSelfTests.cs`) | Verifying Release does not read an existing Debug DLL, and vice versa; each configuration is found independently; a missing one reports the expected path. |
| Child processes (`SelfTests/ProcessRunnerSelfTests.cs`) | Normal exit, non-zero exit, ~1 MB on **both** streams without deadlocking, and a timeout that kills a grandchild process. |

The project fixtures are never built. They are only *evaluated*, which needs no compilation and does not even
require the referenced projects to exist.

The FG-ARCH-010 logic is tested the same way, against deliberately wrong registries: the real registry, being
correct, can never demonstrate that the validator would notice if it were not.

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
2. **Add a §5 entry here** with the next unused `FG-ARCH-0NN`, an `<a id="fg-arch-0nn"></a>` anchor, and a
   matching entry in `ArchitectureRuleRegistry`. Never reuse a retired id. FG-ARCH-010 fails if the document
   and the registry disagree, or if the anchor is missing.
3. **Write the check**, tagged `[ArchitectureRule("FG-ARCH-0NN")]` so FG-ARCH-010 can find it structurally.
   Set its check status to `Implemented`: it exists and fails when explicitly run, but nothing yet forces it to
   run on every change.
4. **Prove the check detects and does not over-detect** with fixtures (§6.1) — never by temporarily breaking a
   production project.
5. **Run it against the whole codebase.** Every hit is either a real violation to fix or a false positive that
   proves the check is wrong. Fix both before proceeding.
6. **Promote to `Required in CI`** once it is quiet on a clean tree and its failure message names the rule id
   and links its authority. This is the step that makes the rule mandatory on every pull request.

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

The module skeleton exists: eight projects under `src/`, a `Directory.Build.props`/`.targets` pair,
`global.json`, and `False Gods.slnx`. All **four inner** projects now carry source and unit tests —
`FalseGods.Protocol` (the arena content artifact and canonical `ContentHash`, from PoC Phase A), `FalseGods.Core`
(the temporary `BossSimulation` and its three ports), `FalseGods.RuntimeContracts` (the boss presentation
contracts), and `FalseGods.Application` (the domain→presentation mapper), the last three being the first Phase B
slices — while the **four outer** projects (UnityRuntime, both Integration.* adapters, Plugin) remain
reference-graph-only skeletons; that graph is already doing work. The architecture test project,
`scripts/verify.ps1` (with a `-CiSafe` subset), and a CI workflow (`.github/workflows/verify.yml`) all exist.

Per-layer statuses are in **§5.1**, which is the machine-checked authority. This table adds what a status
cannot say: what each rule's *enforced* layer actually buys, and what it leaves open.

| Rule | Enforced layer(s) | What is still unchecked | Compiler protection |
|---|---|---|---|
| FG-ARCH-001 | — (inner build only; not a cited check) | the whole rule | `Full` — Core declares no reference at all |
| **FG-ARCH-002** | `project graph` ✅ CI; `assembly metadata` (L0/L3) | — (both layers exist) | `Full` — Plugin does not reference the adapter |
| **FG-ARCH-003** | `project graph` ✅ CI | a Protocol type reached transitively | `Full` — UnityRuntime does not reference Protocol |
| FG-ARCH-004 | — | the whole rule | `Partial`, via FG-ARCH-003 |
| **FG-ARCH-005** | `project graph` ✅ CI | transitive drags; **the entire broker-access half** | `Partial` — no project references ST/LiteNetLib/Steamworks |
| **FG-ARCH-006** | `project graph` ✅ CI | transitive drags; **`[HarmonyPatch]` outside Integration.Sulfur** | `Full` — only Integration.Sulfur references 0Harmony |
| FG-ARCH-007 | — (inner build only; not a cited check) | the whole rule | `Full` — RuntimeContracts references Core alone |
| FG-ARCH-008 | — | the whole rule | `None` — needs the Protocol types to exist |
| FG-ARCH-009 | `project graph` of FG-ARCH-002 | the behavioural half (needs a Composition Root) | `Partial`, via FG-ARCH-002 |
| **FG-ARCH-010** | `registry consistency` ✅ CI | whether a rule is *right* | `None` |

**The pre-push hook is the blocking gate** (branch protection was removed; CI re-checks but does not block).
Five rules have a layer that is `Required in CI` — run in CI and enforced by that hook. What CI cannot run —
every metadata layer, and the outer assemblies' compile-time protection — remains L0 (the pre-push hook runs
the full verify, Debug and Release) and L3.

**Say it precisely: five rules have an enforced layer; zero rules are fully enforced.** FG-ARCH-005 and
FG-ARCH-006 in particular each have a second half — broker access, and the `[HarmonyPatch]` scan — that
nothing checks. "The reference graph is checked" is the claim these entries support. It is not the same
sentence as "the rule holds".

Every `Full` above was checked by writing the violation and confirming the compiler rejected it (`CS0246` /
`CS0234`), not by reading the csproj and assuming. The `-CiSafe` path was checked the same way: the inner
assemblies and the test project build with `SulfurManagedDir`/`BepInExCoreDir` empty, and the CI-safe test
subset passes with **every outer DLL physically removed** (so nothing silently depends on one).

The FG-ARCH-003/005/006 reference-graph checks read the outer projects' *evaluated graphs*, which needs no
game: `dotnet msbuild -getItem` evaluates without running targets, so `Directory.Build.targets`'
`RequiresGameAssemblies` guard never fires and the unresolvable `HintPath`s are never resolved. That is the
same property the FG-ARCH-002 project-graph layer and the probe-isolation checks already rely on in CI.

Still not created:

- the `assembly metadata` layer for FG-ARCH-003/005/006 — the only layer that can see a *transitive* drag.
  It reads outer DLLs, so it would be L0/L3, not CI.
- FG-ARCH-005's `broker access` layer and FG-ARCH-006's `patch attribute scan` — each is the unchecked half of
  a rule whose other half is now a merge gate.
- any `.asmdef` (the Unity authoring project is separate — see
  [OriginalContentPipeline.md §8.2](OriginalContentPipeline.md))
- any source file in the **four outer** production projects that are still skeletons (UnityRuntime, both
  Integration.* adapters, and Plugin)

**Next.** The cheapest remaining win is the `assembly metadata` layer for FG-ARCH-001/003/007 — one allow-list
of assembly names each, over `AssemblyReferenceInspector`, which already exists and is already fixture-tested.
FG-ARCH-001 and FG-ARCH-007 read *inner* assemblies, so unlike the outer-assembly layers they can run in CI.
