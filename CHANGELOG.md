# Changelog

## 0.2.0

- The config reloads live: save `com.raveniron.restless.cfg` and the server picks up the change within 3 seconds,
  no restart. The log names each setting that changed.
- New admin command `restless reload`: re-read the config now.
- Admin commands work from chat with no mod on the client: `/kick restless`, `/kick restless-reset-200`,
  `/kick restless-reload` and so on. The server catches the word before the game's kick sees it.
- The Queen's lair resets by default: `Mistlands_DvergrBossEntrance1` is no longer in the default `Exclude`. Its door
  seals again and the Queen comes back. A config file written by 0.1.0 keeps its own `Exclude` line; delete
  `Mistlands_DvergrBossEntrance1` from it to let her reset.

## 0.1.0

- First release. Dungeons (burial chambers, sunken crypts, frost caves, infested mines) reset after `ResetAfterDays`
  in-game days, the next time a player comes near, never while anyone is inside.
- Outdoor locations reset when listed in `Include`: fuling villages, charred fortresses and troll caves by default.
- Rebuilds use the game's own spawn code and the original seed, with the zone's terrain loaded, so the layout comes
  back as the world first made it.
- Player builds, tombstones, tamed creatures, terrain edits, trees and rocks, and dropped loot are never removed. A
  location players have built in is skipped (`SkipIfPlayerBuilt`).
- Admin console commands: `restless [radius]` and `restless reset [radius]`.
