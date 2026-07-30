# False Gods

In SULFUR, divinity leaks. The Earth Mother's blood seeps out through the cracks in her shrines, down into the
mud, into the water, into anything soaking in it. Whatever drinks it does not become a god. It just grows — grows
large, grows old, grows a following — and then starts believing it is one.

The priest's job is to collect that blood. False Gods exist because some of it was drunk before he got there.

This mod adds an original boss and the cave it has made its own. It is not a reskin: new arena, new fight, new
threat, and a reason for both to be there.

## Where to find it

Kill the Cousin — the goblin caves' own boss, the one that fell into something it should not have. A lit portal
opens in the room where it dies. Walk into it and you go through, and so does everyone playing with you.

You will want to be ready. The way back out is behind rock until the thing in there is dead.

The payout is worth roughly three times what the Cousin gives you.

## Co-op

False Gods works in SULFUR Together. The host owns the boss, the arena and everything the room does; each player
sees it happen locally, and your own inventory, loot and health stay your own.

The adapter that connects the two is included in this package. It only turns on when SULFUR Together is actually
installed — if it is not, the mod runs perfectly well as single-player and the adapter simply never loads. You do
not need to install or configure anything extra either way.

## Compatibility

*Requires* BepInEx 5, which Thunderstore installs for you.

*Optional* — SULFUR Together, for co-op. Any recent build with the public mod bridge.

The mod adds nothing to a level you are not in. It does not change the Cousin's own fight, its loot, or anything
else about the caves — the only thing it adds to that room is a door, and only after the fight there is over.

No vanilla game files are shipped or modified. Everything borrowed from the game — rock, materials, props — is
loaded from your own installation at runtime. The art that is ours is ours.

## Language

The boss announces itself in your language. All fourteen the game ships are translated, and it follows you when
you switch languages mid-game.

## Configuration

The config file is written on first launch as `BepInEx/config/ryuka.sulfur.false_gods.cfg`. There is one setting,
and most players never need it.

### MaxClientHitDamage

Host only, and multiplayer only: the largest single hit a connected player is allowed to report against the boss.
It is a sanity ceiling against a forged message, not a damage cap — set it above any legitimate single weapon hit.
The host clamps to this value; the fight itself still decides everything else.

```ini
[Multiplayer]
MaxClientHitDamage = 1000
```

## Known issues

**Playing without SULFUR Together logs an error, and it is not one.** You will see this:

```ini
[Error: BepInEx] Could not load [False Gods - SULFUR Together Adapter] because it has missing
dependencies: com.ryuka.sulfur.together
```

That is BepInEx reporting a *skipped* plugin, and skipping it is the whole point — the co-op adapter declares
SULFUR Together as a hard requirement precisely so that without it the adapter is never loaded at all, instead of
loading and then failing somewhere less obvious. The mod itself has already loaded by then and says so on the line
above. Nothing is broken and there is nothing to fix. BepInEx just files "skipped" under Error.

**The fight is tuned but new.** If a phase feels too long or too cheap, say so — the numbers are easy to move, and
feedback on pacing is the most useful thing you can send.

## Credits

Made by Ryuka. Source: https://github.com/ryuka-dev/False_Gods

Thanks to the SULFUR team at Perfect Random, whose level generation, navigation and boss UI this mod drives
rather than replaces.
