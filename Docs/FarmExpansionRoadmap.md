# Farm Expansion — Roadmap

*A farm plot in the church hub: the player works it by hand at first, and later enslaves a goblin to work it for
them. Crops advance one stage per level cleared. High-value items become plantable by transforming them at a
vanilla shrine.*

> **Status: DESIGN ONLY.** No code, no prefabs, no assembly exists for this. Every game-API fact below was read
> from the **v0.18.5 decompile** (the version this project compiles against) or from the AssetRipper export, and
> is **not verified against DLL metadata and not verified in game**. The global rule that "the decompile can
> disagree with the DLL" has already cost this project once (`ReceiveDamage`'s overload set), so treat every
> signature here as *proposed* until it compiles and runs.
>
> The reference build is current: the live install's `PerfectRandom.Sulfur.Core.dll` and the v0.18.5 backup are
> identical in size (2 230 784 bytes) — the same build, no version drift.

## 1. Shape of the feature

```
early game   plant a crop  ->  hold 3s to water  ->  it advances one stage per level cleared
             3 stages = mature  ->  hold 3s to harvest  ->  yield x2-3

later        kill a boss (gate TBD)  ->  capture a goblin  ->  it waters, harvests and replants for you
                                          => long expeditions stop wasting the plot

later still  put a magic oil / scroll into a SHRINE  ->  it transforms into a plantable seed
             carry it home alive  ->  plant it  ->  a long cycle later, more of it
```

The two halves are deliberately independent: **phase 1 ships without goblins and without seeds.** Goblin capture
is the one mechanic with no vanilla API at all, and putting it first would block everything behind the least
certain thing in the design.

## 2. Settled decisions

| # | Decision | Why it is not worth re-litigating |
|---|---|---|
| 1 | **One Thunderstore package; its own assembly + its own BepInEx plugin** | A separate release would need the public modding API this repo deliberately deferred (see `DefinitionOfDone.md §3` and the `EncounterCoordinator` class comment): the farm needs the boss-kill unlock signal *and* heavy reuse of prop cloning, the bundle pipeline, marker realization and the ST bridge. Same-package plugins deploy and version together, so no negotiation problem. Its own assembly keeps `FalseGods.Core` boss-shaped (see the responsibility table in [Architecture.md](Architecture.md)) and makes a future separate release a packaging change rather than a rewrite. Follows the `FalseGods.Integration.SulfurTogether` precedent exactly: GUID-based `[BepInDependency]`, no CLR coupling, shipped flat in one folder. |
| 2 | **The plot lives in `WorldEnvironmentIds.ChurchHub`** | That is what "the safe zone" actually is. `SafeZoneTrigger` is only a flag-setter; the real hub is identified in `GameManager.SwitchLevelRoutine`, where `SetSafeZone(true)` fires for `Onboarding`/`ChurchHub` at `levelIndex == 0`. The church already holds the player's stash. |
| 3 | **Exactly one plot, instantiated purely locally → each player sees only their own** | ST does **not** replicate unit creation; False Gods' own minions only appear on both peers because an explicit host-authoritative owner registration was added to ST. Not registering *is* the whole privacy mechanism — there is no per-viewer filtering to build, and no networking code at all. |
| 4 | **No vanilla `Container` anywhere in the farm** | See §5 — it is the one thing that would break the "no multiplayer" property. The church's own stash makes it unnecessary. |
| 5 | **Ownership binds to persistent player identity; state lives in our own JSON** | Never write into the vanilla save. Follows SULFUR Together's `CoopSettingsStore` pattern (`<GUID>.coop.json` beside the plugin). **Open:** what the persistent identity *is* for a remote peer — likely a platform id — must be decided explicitly and owned by one component; platform identity and persistent player identity are different concepts and must not be silently equated. |
| 6 | **Hold duration = 3 s** | The cave door's own authored value, so the farm feels like the rest of the game. |
| 7 | **Growth ticks on level transition, filtered by destination** | See §4. One event source, no per-level special cases. |
| 8 | **A mature crop waits; it never rots** | Running 20 levels costs only opportunity (one harvest instead of ~6). That is already enough to make goblin automation valuable, without punishing the deep-run playstyle SULFUR is built around. |
| 9 | **Weapons are never plantable** | The game already owns that progression: `SacrificeStation` at the NPC Telia permanently unlocks a weapon once it reaches a rank. Planting weapons would duplicate it. |
| 10 | **Yield is a small multiple (2–3x provisional)** | See §6 for the framing that makes this decidable. |

### 2.1 One plot is a balance mechanism, not a scope cut

Anything that can be planted and yields more of itself is a duplicator. If the output can be replanted, growth
is exponential (1 → 2 → 4 → …). **One plot holding one crop at a time flattens that to linear in levels
played** — total output is bounded by how deep the player actually goes, not by how many times they reinvest.

Consequence: **adding a second plot changes the balance qualitatively, not quantitatively.** Plot count is a
balance dial. It must never be treated as UI capacity or as a convenience setting.

## 3. Phases

Each phase ends with an in-game check by the user. Phases are ordered so that each is independently shippable.

### P0 — Investigation (blocking, must finish before P2 is designed)
Resolve the custom-item question in §7.1. Everything about "seeds" depends on it, and the answer may change the
whole shape of P2. Nothing else is blocked by it: **P1 does not need it.**

### P1 — The plot and manual tending
The farm exists and is playable. No goblins, no seeds, no shrines.
- Authored plot content placed in ChurchHub, instantiated locally on arrival.
- A `HoldingInteractable` subclass for water/harvest (§7.2), 3 s, radial progress, custom prompt text.
- Growth ticks on level transition (§4); 3 stages to mature; yield multiplier.
- Persistence in our own JSON, keyed by owner identity (decision 5).
- Crop set: ordinary vegetables/food only. Plant X → harvest X at the multiplier.
- **Done when:** a player can plant, leave, clear levels, come back, and harvest more than they planted — in
  single-player and with a second peer present, with each peer seeing only their own plot.

### P2 — Seeds via shrine transformation
High-value items become plantable.
- A transformation station attached to vanilla shrines (§7.3).
- A whitelist of transformable items with per-item cycle lengths (§6).
- **Blocked on P0.**

### P3 — Goblin automation
- Capture mechanic (no vanilla API — must be designed from scratch).
- A captured `UnitIds.GoblinCivilian` works the plot: waters, harvests, replants.
- Unlock gate: a boss kill, reusing the mod's only proven unlock pattern (an event-driven consequence of the
  boss dying, not a timer and not a scene-name guess).

## 4. Growth: the event source

`NextLevelTrigger.MakeTransition()` branches three ways, and **only the first ticks a level**:

```csharp
if (specificEnvironment == None)    → GameManager.CompleteLevel();          // ticks
else if (== ChurchHub)              → GameManager.GoToChurchHub(...);       // no tick
else                                → GameManager.GoToLevel(...);           // ALSO no tick
```

That third branch is every **chapter boundary**. Surveying every vanilla prefab carrying the field (values are
`WorldEnvironmentIds`, 0-indexed):

- `specificEnvironment: 1` (ChurchHub) occurs in **exactly one** vanilla prefab — `Chunks/Caves/CaveCousinNew`,
  the cave boss's room, which is where this mod's own portal lives. There are **no other** ChurchHub-pointing
  boss levels.
- Chapter transitions that silently skip a tick: `ShantyToSewers`(5), `HedgemazeToCastle`(8), `CastleLucia`(9),
  `ForestChunkEndOfBoss`(10), `PoolsEmperor`(13), `DesertBossChunkTerminator1` + `DesertEndTrigger`(14), the
  shrine rooms → `EndlessMode`(15).
- The ordinary `*End` / `*EndRoom*` rooms are `0` and do tick.

**So hooking `CompleteLevel()` would silently skip every chapter boundary — including most boss levels.**

`GameManager.SwitchLevelRoutine(chapterSO, levelIndex, loadingMode, spawnIdentifier)` is the single choke point
every transition passes through: `OnCompleteLevelRoutine` ends by calling it, `GoToLevel` calls it, and
`GoToChurchHub` → `GoToLevel(ChurchHub, 0, …)`. It already carries the discriminator, the same one that drives
`SetSafeZone`: `(chapterSO == Onboarding || chapterSO == ChurchHub) && levelIndex == 0`.

> **Rule: advance growth in `SwitchLevelRoutine` when the destination is not ChurchHub/Onboarding.**

This covers ordinary descents *and* chapter transitions, and automatically excludes returning to church, dying
back to church, and this mod's own arena→church exit — **with no per-level special case**.

Two consequences worth stating:
- **Anti-exploit is free.** Bouncing church ↔ level cannot farm growth, because arriving at church never ticks.
- **Open:** does entering *this mod's own arena* (delivered by hijacking level generation) pass through
  `SwitchLevelRoutine`? Not checked. If it does, "arena +1, exit +0" is a sane net one level per boss trip.

## 5. Why this needs no multiplayer code — and the one thing that would break it

The plot is instantiated locally and registered with nothing, so no peer learns it exists. Harvested items go
into personal inventory, which is already per-peer by default.

**The trap is containers.** `ChestPatches.Start_Post` calls `ChestSyncManager.Register(__instance)` for **every
`Container` that starts**, ours included — it does not distinguish origin. It is inert by default, because
everything is gated on `EnableChestSync && NetSessionSettings.SharedLootEnabled` and shared loot defaults off.
But when a host turns shared loot **on**, chest matching is done **by world position with a 1.0 m epsilon** —
and with one plot, *every player's plot is at the same world position*. A client asking to open its own farm
container would match the host's, 0 m away. Same class of bug as ST's destructible mirror breaking this mod's
crates.

Avoided by design: **put no vanilla `Container` in the farm.**

Still unverified: a locally-spawned `GoblinCivilian` (P3) is visible to `BatchedNPCRaycasts`, which iterates
`GameManager.Players` **including ST's ghost players**. A goblin existing on only one peer could notice remote
players. Passive unit in a safe zone, probably harmless — needs an in-game check, not an assumption.

## 6. Balance framing

Yield and cycle length are **the same dial**. "2x every 3 levels" and "4x every 6 levels" are the same economy.
So the tunable is not a multiplier per item, it is:

```
rate  =  (output - input) / levels_to_mature       // net units gained per level cleared
```

- Common crops set the **reference rate**: 3 stages, 2–3x → a net gain of roughly ⅓ to ⅔ of an input per level.
- A high-value item is then priced by giving it the **same or a lower rate with a longer cycle**, which makes
  "valuable things take longer" fall out of the arithmetic instead of being guessed per item.
- Rarity tiers within a family (oils are not all equally rare) become cycle lengths, not special cases.

This does not decide the numbers; it makes them decidable with one comparison instead of a table of guesses.
**Numbers are deliberately left open** until P1 is playable and can be measured.

## 7. Open questions and risks

### 7.1 The custom-item problem — the largest unknown in the feature
A "seed" as a **new item type** needs a valid `ItemId` and an `ItemDefinition` in the game's item database.
`RecipeDatabase` shows the shape of these databases and it is discouraging: `private RecipeData[] recipes`, a
**fixed array indexed by `RecipeId.value - 1`**. If the item database is built the same way, minting a new item
means growing a private array and inventing an id — fragile, and hostile to save compatibility.

Four ways out, in rough order of preference. **This must be settled before P2 is designed:**
1. **The seed is not a new item** — the transformation marks the *original* item (a flag/tag the plot reads), so
   nothing new is minted.
2. **Reuse an existing vanilla item as the carrier**, marked the same way.
3. **A ledger** — the shrine records "this player has N plantable X" in our own JSON and no item is carried.
   *Cheapest, but it throws away the design's best tension:* the player is supposed to have to survive the rest
   of the run to bring the seed home.
4. **Genuinely mint an item type.** Only if 1–3 fail; assume it is expensive.

### 7.2 Hold-to-interact — mostly free, with one behavioural trap
`HoldingInteractable : Interactable` is public and subclassable, and the fields that matter are `protected`, so
**no reflection is needed**:

| Member | Notes |
|---|---|
| `protected float holdingMaxTimer` | `[Range(0.1, 30)]`. The cave door authors **3**. |
| `protected virtual void OnFinishedHolding()` | The hook: "watering done" / "harvest done". |
| `protected virtual void Update()` | Ticks the timer and drives the UI. Overridable. |
| `public virtual bool OnHoldInteract(bool)` | Driven by `InteractionManager`. |
| `Interactable.useRadialProgress` | **public bool** — the radial progress ring, free. |
| `Interactable.showCustomText` / `customText` | **public** — the prompt label, free. |

The door's authored values are `holdingMaxTimer: 3`, `holdingSpeedGoBack: 5`, `holdingMinTimer: 1`,
`extraCancelCooldown: 0.5`.

> **Trap: releasing rewinds, it does not pause.** The door rewinds at **5x** (the class default is 2x). "Water
> half now, finish later" is the opposite of what the component does. `Update()` is virtual so this can be
> changed — but that is a deliberate deviation from vanilla feel, not a default.

The subclass is a game type, so it must live in this feature's own anti-corruption layer, never in a Core-shaped
module.

Also note the hold locks player controls for its duration
(`ModifyControllerLock(LockStatePadlock.HoldingInteract, …)`), which suits "this takes time and commits you".

### 7.3 The shrine transformation station
The idea: a native-looking page on a shrine where an item is inserted and **transformed** rather than crafted
(Terraria's Shimmer, roughly). Shrines appear only on fixed levels within a run, which gates access naturally.

What was found:

- **A `Shrine` is not a station.** `Shrine : GiveResourceTrigger` — it is a cinematic/blood/amulet object with an
  `environmentToUnlock`. It has no inventory page today.
- **The attach seam is the game's own.** `Interactable` discovers stations with
  `serviceStations = GetComponents<ServiceStation>()`. Adding our own `ServiceStation` subclass component to the
  shrine object is therefore the native way in — **no patch required**.
- **`ServiceStation` is public and built for subclassing** (`protected ItemGrid serviceGrid`,
  `serviceGrid.SetupGrid(size, playerUnit, GridType)`, virtual `RemoveItemsOnExit` / `DisableItemDrop` /
  `CanNavigateToGrid`, `onEnter`/`onExit`). Existing subclasses: `CraftingStation`, `SacrificeStation`,
  `OilRerollStation`, `RepairStation`, `KeyStation`, `ItemFrameStation`, `BaptismalFontStation`,
  `StampPurchaseStation`, `TeliaPrintStation`.
- **But UI panels are a fixed, authored set of six**, exposed by `InventoryUI`: `TransformShopInventory`,
  `TransformStashInventory`, `TransformCraftInventory`, `TransformKeyInventory`, `TransformSacrificeInventory`,
  `TransformQuestInventory`. There is **no API to create a seventh**. A station binds `ContainerTransform` to one
  of these — e.g. `SacrificeStation` uses `TransformSacrificeInventory` and its `sacrificeFeedbackText`.

Two candidate approaches, **neither chosen yet**:

- **Borrow the sacrifice panel.** It is already the right *shape* — insert one item, press confirm, get a result,
  with a feedback line — and it can never be open at the same time as a shrine (Telia is in the church, shrines
  are in levels), so reuse is safe. Least work, least native-crafting look.
- **Inject recipes into the real crafting page.** `CraftingStation.GetAvailableRecipes()` returns
  `AsyncAssetLoading.Instance.recipeDatabase.cookingRecipes` / `.enchantmentRecipes`, and both are
  `[NonSerialized] public List<RecipeData>` rebuilt by `InitRuntime()`; `PopulateRecipesMenu()` iterates them and
  filters on `IsLearned`. **Those lists are public and mutable**, so runtime injection looks plausible — a
  transformation is just a one-input recipe. This would be maximally native. **Unverified**, and it still runs
  into §7.1, because `RecipeData.createsItem` is an `ItemId`.

### 7.4 Smaller open items
- Which boss gates the goblin unlock, and what the unlock object is.
- The transformable whitelist, and cycle length per rarity tier. Oils are not all equally rare.
- What a "food workbench" is, if the farm needs one at all: cooking in vanilla is manual/recipe driven
  (`ItemIds.Manual_GoblinCooking`, `Manual_ForestCooking`, `Manual_CookingCastle`, …).
- The goblin capture interaction itself. No vanilla API — this is the least certain part of the design, which is
  why it is last.

## 8. Related

- [Architecture.md](Architecture.md) — module boundaries; the responsibility table this feature's own assembly
  must fit into.
- [DefinitionOfDone.md](DefinitionOfDone.md) — completion gates, and the rule against building the second
  abstraction before the second consumer exists.
- [BossEncounterRunbook.md](BossEncounterRunbook.md) — the prop/room borrowing recipe and the marker-group
  realization pattern this feature reuses.
