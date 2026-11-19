using System;
using System.Collections.Generic;

namespace RavenIron.Restless
{
    /// <summary>Pure decisions, no game types, so the test harness can compile this file on its own.</summary>
    public static class Rules
    {
        /// <summary>The stamp a location carries before Restless has ever seen it.</summary>
        public const long Unstamped = 0;

        /// <summary>
        /// Is a location stamped at <paramref name="stampSeconds"/> due a reset at <paramref name="nowSeconds"/>?
        /// Both are world time (ZNet.GetTimeSeconds), which stops while the server is empty, so "30 days" means
        /// 30 in-game days that somebody was online for. A stamp from the future (a world rolled back, or a
        /// clock moved by a mod) is treated as "just reset", never as overdue.
        /// </summary>
        public static bool IsDue(long stampSeconds, double nowSeconds, double intervalDays, double dayLengthSeconds)
        {
            if (stampSeconds == Unstamped) return false;
            if (intervalDays <= 0 || dayLengthSeconds <= 0) return false;
            double age = nowSeconds - stampSeconds;
            return age >= intervalDays * dayLengthSeconds;
        }

        /// <summary>In-game days since the stamp, for the status line; 0 when unstamped or from the future.</summary>
        public static double DaysSince(long stampSeconds, double nowSeconds, double dayLengthSeconds)
        {
            if (stampSeconds == Unstamped || dayLengthSeconds <= 0) return 0;
            return Math.Max(0, (nowSeconds - stampSeconds) / dayLengthSeconds);
        }

        /// <summary>
        /// The stamp to write. Never 0 (that means "never seen"), so a reset at world time zero still counts.
        /// </summary>
        public static long Stamp(double nowSeconds)
        {
            long s = (long)Math.Floor(nowSeconds);
            return s <= 0 ? 1 : s;
        }

        /// <summary>A comma-separated list of location prefab names, trimmed, case-insensitive, blanks dropped.</summary>
        public static HashSet<string> ParseList(string csv)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(csv)) return set;
            foreach (var raw in csv.Split(','))
            {
                var name = raw.Trim();
                if (name.Length > 0) set.Add(name);
            }
            return set;
        }

        /// <summary>
        /// Dungeons that must never be wiped: the Queen's lair (a boss) and Hildir's quest dungeons, whose chests are
        /// tied to world quest state. Only dungeons reset at all, so outdoor locations need no entry. Owners can edit it.
        /// </summary>
        /// <summary>
        /// Outdoor locations that reset as well as dungeons: fuling villages and charred fortresses.
        /// Prefab names read from the game's SoftRef manifest (Assets/world/Locations/Heath/GoblinCamp2.prefab,
        /// .../Ashlands/CharredFortress.prefab).
        /// </summary>
        /// Troll caves (TrollCave02) too: real caves dug into the hillside, with no generator, so the dungeon test misses
        /// them (a test run logged "TrollCave02: outdoor").
        public const string DefaultInclude = "GoblinCamp2, CharredFortress, TrollCave02";

        /// <summary>The largest radius `restless reset &lt;radius&gt;` accepts, in metres.</summary>
        public const float MaxCommandRadius = 5000f;

        /// <summary>
        /// Reads the console arguments after `restless`: an optional "reset" and an optional radius, in either order.
        /// No radius means 0 (the single nearest location). False, with a message, for anything else.
        /// </summary>
        public static bool ParseCommand(string[] args, out bool reset, out float radius, out string error)
        {
            reset = false; radius = 0; error = null;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i].Trim();
                if (a.Length == 0) continue;
                if (string.Equals(a, "reset", StringComparison.OrdinalIgnoreCase)) { reset = true; continue; }
                if (string.Equals(a, "status", StringComparison.OrdinalIgnoreCase)) continue;
                if (float.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r)
                    && r > 0 && !float.IsInfinity(r))
                {
                    if (r > MaxCommandRadius) { error = $"the radius can be at most {MaxCommandRadius:0} m"; return false; }
                    radius = r;
                    continue;
                }
                error = "usage: restless [radius] | restless reset [radius]";
                return false;
            }
            return true;
        }

        public const string DefaultExclude =
            "Mistlands_DvergrBossEntrance1, Hildir_crypt, Hildir_cave, Hildir_plainsfortress";
    }
}
