# Architecture-check fixtures

Synthetic projects used to prove that the architecture checks actually detect what they claim to.

These are **not** in `False Gods.slnx` and are **never built**. The checks only *evaluate* them with
`dotnet msbuild -getItem`, which does not compile anything and does not require the referenced projects
to exist. They exist so that no check has to be proven by temporarily breaking a real project and
eyeballing the output.

| Fixture | Proves |
|---|---|
| `AllowedGraph` | A conforming graph passes — the checker is not simply always-fail. |
| `ForbiddenProjectReference` | An **unused** `ProjectReference` to the ST adapter is still caught. It has no source files at all, so nothing about it could reach a compiled `AssemblyRef` table. This is the case only the project-graph layer can see. |
| `ForbiddenReferenceViaImport` | A `Reference` injected by an **imported** `.props` behind a **condition** is still caught. This is the case a regex over the `.csproj` would miss. |
| `ForbiddenReferenceInReleaseOnly` | A `Reference` that exists **only when `Configuration == Release`** is still caught. Under MSBuild's default configuration the project is spotless. Found because the checks evaluate every configuration declared in `$(Configurations)`, read from MSBuild rather than hardcoded. |

`ForbiddenProjectReference` deliberately points at the real
`src/FalseGods.Integration.SulfurTogether` project, so the fixture stays honest if that project moves.
