# Architecture-check fixtures

Synthetic projects used to prove that the architecture checks actually detect what they claim to.

These are **not** in `False Gods.slnx` and are **never built**. The checks only *evaluate* them with
`dotnet msbuild -getItem`, which does not compile anything and does not require the referenced projects
to exist. They exist so that no check has to be proven by temporarily breaking a real project and
eyeballing the output.

| Fixture | Rule | Proves |
|---|---|---|
| `AllowedGraph` | 002 | A conforming graph passes — the checker is not simply always-fail. Also used by FG-ARCH-003/005/006, where it pins that evaluation is **not transitive**: it references `Integration.Sulfur`, which does reference `0Harmony`, and the Harmony scan must still be quiet. |
| `ForbiddenProjectReference` | 002 | An **unused** `ProjectReference` to the ST adapter is still caught. It has no source files at all, so nothing about it could reach a compiled `AssemblyRef` table. This is the case only the project-graph layer can see. |
| `ForbiddenReferenceViaImport` | 002 | A `Reference` injected by an **imported** `.props` behind a **condition** is still caught. This is the case a regex over the `.csproj` would miss. |
| `ForbiddenReferenceInReleaseOnly` | 002 | A `Reference` that exists **only when `Configuration == Release`** is still caught. Under MSBuild's default configuration the project is spotless. Found because the checks evaluate every configuration declared in `$(Configurations)`, read from MSBuild rather than hardcoded. |
| `ForbiddenProtocolReference` | 003 | An **unused** `ProjectReference` from a presentation-shaped project to `FalseGods.Protocol` is caught. No source files, for the same reason as `ForbiddenProjectReference`. |
| `ForbiddenTransportReference` | 005 | `LiteNetLib` in every configuration, and `SULFUR Together` — ST's **real assembly identity, which contains a space** — only under `Release`. A check written from the prose spelling `SULFURTogether` (a namespace) would match neither. |
| `ForbiddenHarmonyReference` | 006 | A `Reference` whose `Include` identity names something unrelated, and whose **`HintPath` alone** reveals `0Harmony.dll`. A check comparing `Include` identities would report it clean. |

`ForbiddenProjectReference` and `ForbiddenProtocolReference` deliberately point at the real
`src/FalseGods.Integration.SulfurTogether` and `src/FalseGods.Protocol` projects, so the fixtures stay honest
if those projects move.

The transport and Harmony fixtures instead use **fictional `HintPath`s**. Nothing here is compiled, so nothing
has to resolve — and committing a real `LiteNetLib.dll` or `0Harmony.dll` to prove a check works would be a
worse trade than any check is worth.
