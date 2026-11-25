<!--
  README schema v3 SOURCE. Edit this file, never README.md: `node tools\readme\forge.js` writes README.md from it.
  Tokens are listed at the top of tools\readme\forge.js. Every setting name goes through {{key ...}}, and the settings
  table and default lists come from {{gen ...}}, so the page cannot drift from the DLL.
-->
# 🪦 Restless

{{rune ᛒ | RESTLESS | THE DEAD COME BACK | 2f7d5b}} {{badge VERSION | {{version}} | 8b1e1e}} {{badge INSTALL ON | SERVER | 1f4e79}}

| ⏳ **30 days** | 🗺️ **Same layout** | 🛡️ **Builds untouched** | 🖥️ **Server side** |
|---|---|---|---|
| then it all comes back | the world's own seed | and never while you're inside | players need nothing |

*The crypt was empty when Sigrun left it: every urn smashed, every draugr cut down, the last chest scraped clean. A month later she walked past the door and heard something shuffling in the dark.*

### Dungeons that fill up again.

On a long-running server every crypt, cave and camp near home gets cleared once and stays empty for good. Restless brings them back. After a set number of in-game days, a cleared location returns to exactly how the world first made it: the same rooms, full chests, and monsters back at their posts.

It happens quietly, the next time someone comes near, and never while anyone is standing in it. Nothing a player built is ever touched.

{{rune ᛞ | NEW IN {{version}} | VALHEIM 1.0.30 | 8b1e1e}}

> **⚒️ Works on Valheim 1.0.30.** That update renamed something Restless reads every location's name from, and 0.2.1 stopped resetting anything (its log filled with `MissingMethodException`). This version reads it under either name, so it runs on 1.0.30 and on older versions alike. It also leaves OreMines' mines to OreMines, which resets them itself.
>
> **From 0.2.1:** troll caves reset properly, with no more trolls, chests and mushrooms piling up.
>
> **From 0.2.0:** admin commands from chat with no mod installed (`/kick restless-reset`), the config reloads by itself when you save it, and the Queen's lair resets by default.

{{rune ᚾ | BEFORE YOU INSTALL | READ THIS | b8860b}}

| | |
|---|---|
| **Who needs it** | **The server.** Players don't need it, and admins don't either: every command works from chat. |
| **A cleared place comes back** | That's the point, but say so to your players: a crypt they emptied will be full again one day, monsters included. |
| **Loot left inside** | Items lying on the ground are kept, but a chest's contents are replaced with fresh loot. Don't store things in a dungeon's chests. |
| **Needs** | BepInEx only. No Jotunn, no asset bundles. |

{{rune ᛒ | I | WHAT COMES BACK | 2f7d5b}}

- **Every dungeon**: any location with an interior the game builds for it. On a vanilla world that's burial chambers, sunken crypts, frost caves, infested mines and the Queen's lair (its door seals again and the Queen comes back).
- **Outdoor locations you name**, in {{key Include}}. By default: {{gen include}} (fuling villages, charred fortresses and troll caves).
- **Never**: {{gen exclude}} (Hildir's quest dungeons), and anything else you add to {{key Exclude}}.

A reset removes what the location put there: the rooms and everything in them, the huts and walls, chests, spawners, any structure inside it that no player built, and the monsters standing in it. Then the game's own spawn code rebuilds it from the world's original seed, with the ground loaded so every hut lands where it first stood. The rooms come back room for room.

{{rune ᛊ | II | WHEN IT HAPPENS | 2f7d5b}}

1. **The first time a player comes near** (within {{key TriggerDistance}}, 300 m), Restless notes the location, and its {{key ResetAfterDays}} count starts. A place that was looted before you installed the mod waits a full count too.
2. **Once it's due**, the next player to come within 300 m sets it off.
3. **Never with anyone close**: the reset waits while any player is inside the location, in its dungeon, or within {{key SafeDistance}} (100 m) of its edge.

The days are in-game days, and the world clock stops while the server is empty, so 30 days means 30 days somebody was playing. At most one location resets every five seconds.

{{rune ᛉ | III | WHAT STAYS SAFE | 2f7d5b}}

- **Anything a player built or owns**: pieces, portals, carts, boats. And while {{key SkipIfPlayerBuilt}} is on, a location with player building inside is left alone entirely, and checked again after another count.
- **Players, tombstones and tamed animals.**
- **Terrain edits**, and the trees and rocks around a location.
- **Loot on the ground**, while {{key KeepDroppedItems}} is on. An item of a kind the location itself places, like a fuling camp's barley, counts as the location's and is replaced.
- **Objects from mods the server doesn't know.**

{{rune ᚠ | THE TOOLBOX | ADMIN COMMANDS | 2f7d5b}}

Open the console with **F5**. Every command checks the server's admin list.

| Command | What it does |
|---|---|
| `restless` | The location you're at: whether it resets (and why not, if it doesn't), and how long until it's due. |
| `restless 200` | The same for every location within 200 m. |
| `restless reset` | Resets the nearest location that can reset, right now. Stand just outside it: nobody can be inside. |
| `restless reset 200` | Resets every one within 200 m (up to {{gen maxradius}} m), one every half second, and sends you a summary. Any with a player inside are skipped. |
| `restless reload` | Re-reads the server's config right away and says what changed. You rarely need it: the server notices a saved config file within 3 seconds, no restart needed. |

**No mod on your game? Use chat.** The same commands work from the chat box with nothing installed, through the game's own `kick` command, which is the only admin command that carries a word to the server. Join the words with `-`:

| In chat | Same as |
|---|---|
| `/kick restless` | `restless` |
| `/kick restless-200` | `restless 200` |
| `/kick restless-reset` | `restless reset` |
| `/kick restless-reset-200` | `restless reset 200` |
| `/kick restless-reload` | `restless reload` |

The answer appears top left, and in full in the F5 console. Nobody gets kicked: a word starting with `restless-` never reaches the game's kick.

<details>
<summary>How to tell what a location is called</summary>

Stand at it and type `restless`. The first word of the answer is the name {{key Include}} and {{key Exclude}} want, for example `GoblinCamp2` or `Crypt3`. The server's log also lists each kind of location the first time a player comes near one, and says whether it resets.

</details>

{{rune ᚷ | MULTIPLAYER | WHO RUNS WHAT | 1f4e79}}

| 🖥️ Server | 👑 Admins | 🧍 Players |
|---|---|---|
| Does all the work: watches who is near what, and resets. | Install it to use `restless` in F5, or use `/kick restless` in chat with nothing installed. | Need nothing. They just find the places full again. |

There's no version check: a player with or without the mod can join. An admin who installs it should run the same version as the server for `restless` in F5 to answer; the chat commands need nothing installed.

{{rune ᛗ | COMPATIBILITY | LIVING TOGETHER | 2e5e3a}}

| | |
|---|---|
| **Other location reset mods** | Don't run two. Each would reset on its own schedule. |
| **OreMines (blacks7ar)** | Works alongside. Its mines reset themselves, so Restless leaves every `BOM_…Mine` location to it. To have Restless reset one instead, put its name in {{key Include}}. |
| **Mods that add locations** | Their dungeons reset if the game builds their interiors the way it builds its own. For anything else, add its name to {{key Include}}. Objects from a mod the server doesn't have are never removed. |
| **Mods that add items or creatures** | Fine. Restless only removes what the location placed, the creatures standing in it, and the ones its spawners made. |

{{rune ᛚ | SETTINGS | ALL ON THE SERVER | b8860b}}

Every setting is read on the server, in `BepInEx\config\com.raveniron.restless.cfg`. No restart needed: save the file and the server picks up the change within 3 seconds, and its log names each setting that changed.

{{gen settings}}

{{rune ᛟ | CREDITS | RAVEN IRON | c9a227}}

Made by **Raven Iron**. Restless uses no assets: every reset is the game's own spawn code, given back its original seed.

{{runes}}
