# Architecture Decision Records

Short, dated records of significant architecture decisions for False Gods. Each records **Context / Decision /
Alternatives considered / Consequences / Verification status**.

ADR-001 to ADR-006 were written in the documentation phase and have since been implemented; ADR-007 was written
later, when a feature first needed a decision the earlier six had ruled out. **The Verification status
section of each one is the part kept current** — it says what the decision looks like now that it has been built,
what the implementation taught that the decision could not have known, and where a decision has been overtaken.
The Context and Decision sections are left as written: an ADR records what was decided and why *at the time*, and
rewriting that destroys the only thing it is for.

| ADR | Title | Status |
|---|---|---|
| [ADR-001](ADR-001-Unity-Prefab-As-Arena-Source-Of-Truth.md) | Unity prefab as arena source of truth | Accepted, implemented |
| [ADR-002](ADR-002-AStar-Recast-Integration.md) | A* Recast integration for arena navigation | Accepted; **superseded in part** — the shipped arena *is* the level, so the game's own scan covers it |
| [ADR-003](ADR-003-Network-Native-Boss-Architecture.md) | Network-native boss architecture (Sim/Presentation/Replication) | Accepted, implemented |
| [ADR-004](ADR-004-Optional-Sulfur-Together-Adapter.md) | Optional SULFUR Together adapter | Accepted, implemented |
| [ADR-005](ADR-005-Snapshot-And-Discrete-Event-Replication.md) | Snapshot + discrete-event replication | Accepted, implemented |
| [ADR-006](ADR-006-Ports-And-Adapters-Boundaries.md) | Ports-and-adapters module boundaries | Accepted, implemented; enforcement partial |
| [ADR-007](ADR-007-Feature-Owned-Base-Game-Adapters.md) | A feature may own its own base-game adapter | Accepted; FG-ARCH-006 now enforces an allow-list |
