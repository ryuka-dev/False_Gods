# Changelog

## 0.4.1

- **Fixed a large frame-rate loss.** The mod was searching the entire level for two things it needed, on every
  single frame, in every level of the game — instead of only where and when it had to look. It cost roughly
  half the frame rate on the machine it was measured on (110 down to about 50), everywhere, whether or not you
  had ever been near the arena. Both searches now happen only when there is something to find.
- Nothing else changed: the boss, the arena, the way in and out, the loot and the co-op behaviour are all
  exactly as they were in 0.4.0.

## 0.4.0

First release.

- A new boss with its own hand-sculpted cave arena, and a way in and out of it.
- **Getting there:** kill the Cousin, the goblin caves' own boss, and a lit portal opens in the room where it
  fell. Walk in and the whole party goes through.
- **Getting out:** the way out is behind rock until the fight is over. Then jump the pit.
- The reward is worth about three times what the Cousin pays.
- **Co-op:** works in SULFUR Together, host-authoritative. The adapter that connects the two ships in this
  package and turns itself on only when SULFUR Together is installed.
- The boss is announced by its own name, translated into all fourteen languages the game ships, and it follows
  the language you switch to.
