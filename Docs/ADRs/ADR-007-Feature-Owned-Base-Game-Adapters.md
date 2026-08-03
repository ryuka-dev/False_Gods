# ADR-007 — A feature may own its own base-game adapter

**Status:** Accepted; enforcement updated in the same change (FG-ARCH-006's allow-list)

## Context

`FalseGods.Integration.Sulfur` was written as *the* anti-corruption layer to the base game, and
[DependencyRules.md §5](../DependencyRules.md) said so in the strongest available form: **only** that assembly
may apply Harmony patches, and **only** that assembly may reflect into SULFUR internals. `FG-ARCH-006`'s
project-graph check enforces the first half by scanning every project under `src/` for a `0Harmony` reference,
with a single hardcoded exemption.

The farm expansion ([FarmExpansionRoadmap.md](../FarmExpansionRoadmap.md)) is decided to be **its own assembly
and its own BepInEx plugin** (decision 1), shipped inside the same Thunderstore package. Its first slice — the
seed mark's presentation — needs exactly the two things the rule reserves:

- a Harmony postfix on `ItemDescription.Setup(InventoryItem)`, because the tooltip has no event and no seam;
- a Harmony postfix on `InventoryItem.Setup(...)`, which is both where an item's view is built and where its
  enchantments are restored from save data — so it is the *canonical* place to decide whether a badge shows;
- reflection into two private serialized fields on `ItemDescription`, because the four line pools are private
  and a clone of the panel's own line prefab is the only way to add a line that looks native.

There is no non-patching alternative that is not a heuristic. Watching for a description panel to change, or
polling the hovered item, would attach behaviour to a *correlate* of the event instead of the event — the thing
[DependencyRules.md §4](../DependencyRules.md) exists to forbid.

So the rule as written forces one of three outcomes, and the first two are worse than changing it:

1. Put the farm's game-facing code inside `FalseGods.Integration.Sulfur`, welding an optional feature into the
   base plugin and giving up the separate assembly that keeps `FalseGods.Core` boss-shaped.
2. Keep the patches in `Integration.Sulfur` and dispatch out to the farm through a second registration broker —
   a whole mechanism, built for one cosmetic line, whose only purpose is to route around the rule.
3. Change the rule.

## Decision

**A base-game anti-corruption layer is a role, not a single assembly. Harmony patching and base-game reflection
are permitted in the assemblies that hold that role, and `FG-ARCH-006` enforces an allow-list of them rather
than one name.**

Today the allow-list is:

| Assembly | The base-game surface it adapts |
|---|---|
| `FalseGods.Integration.Sulfur` | The boss/arena composition: damage, spawning, navigation, level generation, vanilla assets |
| `FalseGods.Farm` | The farm expansion: inventory item presentation, item/enchantment databases |

**What does not change, and this is the part that carries the rule's value:**

- **The split by target still holds.** These assemblies may reach into the **base game** and nothing else.
  Reflecting into SULFUR Together stays `FalseGods.Integration.SulfurTogether`'s alone, and that adapter still
  may not patch anything (`FG-ARCH-005`; reflection is not a patch). Neither adapter reaches into the other's
  target system.
- **No inner module gains anything.** `Core`, `Protocol`, `RuntimeContracts`, `Application`, `UnityRuntime` and
  `Plugin` still may not reference Harmony and still may not reflect into any external system. That is what the
  check spends most of its time asserting, and it is unchanged.
- **The allow-list is explicit and small.** It is a list of named assemblies in one place, not a naming
  convention and not a property a project can grant itself. Adding to it is a change to this ADR and to
  `DependencyRules.md §5`, reviewed as such.

**Why an allow-list rather than an exception.** [ArchitectureEnforcement.md §10](../ArchitectureEnforcement.md)
requires an exception to name a cleanup condition and an expiry, because an exception without one is not an
exception — it is a rule change pretending to be temporary. Nothing about the farm's need is temporary: it owns
a slice of the base game's UI for as long as the feature exists. So this is written as a rule change, which is
what §10 says to do in exactly this case.

## Alternatives considered

- **Farm patches inside `Integration.Sulfur`** (outcome 1 above). Cheapest, and it keeps the rule text
  untouched — but it deletes the property the roadmap's decision 1 was made for. The farm would ship as part of
  the boss plugin, its config would live in the boss plugin's file, and "release the farm separately" would go
  from a packaging change back to a rewrite. Rejected.
- **A second registration broker, patches staying in `Integration.Sulfur`** (outcome 2). Preserves the rule
  literally while defeating it in spirit: `Integration.Sulfur` would carry patch classes whose entire body is
  "call whatever the farm registered", which is a patch owned by the farm with someone else's name on the file.
  It also builds the second abstraction before the second consumer exists, against
  [ADR-006](ADR-006-Ports-And-Adapters-Boundaries.md). Rejected.
- **Drop the rule to "any `Integration.*` assembly may patch".** A convention, not a list: any future project
  could grant itself the permission by choosing a name. Rejected in favour of an explicit list, which is also
  what the check can state precisely in its failure message.
- **Let the farm reach the game without patching.** There is no seam. Every candidate is a poll or a name
  guess, which §4 forbids and which would be a worse design than the patch. Rejected.

## Consequences

- `FG-ARCH-006`'s `Patcher` constant becomes a `Patchers` set, its failure message names the whole allow-list,
  and its "the exemption is real" self-check runs over each member — so the rule can never pass by exempting a
  project that does not exist.
- The rule is measurably weaker: two assemblies can now patch the base game instead of one, and the
  `[HarmonyPatch]` attribute scan that would catch a patch *type* in the wrong assembly is still unwritten
  (`Planned`), so the reference layer is all there is.
- `FalseGods.Farm` inherits `Integration.Sulfur`'s obligations along with its permission: patches are guarded
  so a cosmetic failure cannot take a vanilla flow down, reflection is resolved once and degrades to a logged
  warning, and no third-party type crosses out of the assembly.
- The Architecture responsibility table grows a row. The dependency *direction* is unchanged — the farm sits at
  the outer edge and nothing inner references it.

## Verification status

The rule change and its check landed together; `.\scripts\verify.ps1` covers the new project the moment it
appears under `src/`, because `RepoLayout.ProductionProjectNames()` reads the directory rather than a list.

**What is checked:** that no project outside the allow-list references Harmony, in every declared
configuration, however the reference arrived; and that every project *on* the list really does reference it, so
the forbidden name cannot quietly stop matching anything.

**What is not:** that no *type* outside the allow-list carries `[HarmonyPatch]` (`FG-ARCH-006`'s
`patch attribute scan` layer, still `Planned`), and that no assembly outside it has a compiled `AssemblyRef` to
`0Harmony` (`assembly metadata`, also `Planned`). Both were unwritten before this change and remain so; this
ADR does not improve them and does not claim to.
