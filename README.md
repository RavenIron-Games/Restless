# Restless

A Valheim location reset mod by **Raven Iron**.

Burial chambers, sunken crypts, troll caves, frost caves, infested mines, fuling villages and charred fortresses go
back to how the world first made them once enough in-game days have passed. The reset happens the next time a player
comes near, and never while anyone is inside. Other outdoor locations (towers, ruins, draugr villages) are left
alone unless you add them to `Include`.

## Install

- **Server:** required. All the work happens there.
- **Clients:** optional. Admins need it for the `restless` console command. Nobody else needs it.

## How a reset works

1. The first time a player comes within `TriggerDistance` (300 m) of a location, Restless stamps it, and its
   `ResetAfterDays` (30 in-game days) count starts.
2. Once it is due and a player comes within 300 m again, Restless resets it, as long as no player is inside it or
   within `SafeDistance` (100 m) of its edge.
3. What the location put there is removed: the dungeon and everything in it, the village's huts, walls, chests and
   spawners, and the monsters standing in it. Then vanilla's own spawn code rebuilds it with the original seed, so
   you get the same layout, refilled chests, and spawners and monsters back.

At most one location resets every five seconds.

**Never removed:** players, tombstones, anything a player built or owns, tamed creatures, terrain edits, the trees
and rocks around a location, objects from mods the server doesn't know, and dropped items (unless
`KeepDroppedItems` is off).

**Skipped:** a location players have built in (`SkipIfPlayerBuilt`), and anything on the `Exclude` list. By default
that's the Queen's lair and Hildir's quest dungeons.

## Admin commands (F5 console)

| Command | |
|---|---|
| `restless` | The location you are at: whether it resets (and why not, if it doesn't), and when it is due. |
| `restless 200` | The same for every location within 200 m. |
| `restless reset` | Reset the nearest location that can reset, now. Stand outside it: nobody can be inside. |
| `restless reset 200` | Reset every location that can reset within 200 m (at most 5000). They run one every half second, and you get a summary at the end. Any with a player inside are skipped. |

Both commands check the server's adminlist.

## Settings

| Setting | Default | |
|---|---|---|
| `Enabled` | true | Master switch. |
| `ResetAfterDays` | 30 | In-game days between resets. The world clock stops while the server is empty. |
| `TriggerDistance` | 300 | How close a player has to come for a due location to reset (m). |
| `SafeDistance` | 100 | A reset waits while any player is this close to the location's edge (m). |
| `Include` | `GoblinCamp2, CharredFortress, TrollCave02` | Outdoor locations that reset too (dungeons always do). Troll caves are real caves in the hillside, so they are listed here. |
| `SkipIfPlayerBuilt` | true | Leave locations players have built in alone. |
| `KeepDroppedItems` | true | Items on the ground survive a reset. |
| `Exclude` | (see above) | Locations that never reset, comma separated. Wins over `Include`. |
| `VerboseLogging` | false | Logs each first sighting and each reset that had to wait. |

All settings are read on the server.
