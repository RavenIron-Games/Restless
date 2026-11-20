using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace RavenIron.Restless
{
    /// <summary>
    /// Restless: dungeons reset. Burial chambers, sunken crypts, caves and mines go back to how the world first made
    /// them once enough in-game days have passed, the next time a player comes near, never while anyone is inside.
    /// Outdoor locations reset only when named in Include (default: fuling villages and charred fortresses).
    ///
    /// All the work is on the server. Clients only need the DLL for the `restless` admin command.
    /// One Update drives everything (no coroutines); at most one location resets per check.
    /// </summary>
    [BepInPlugin(Guid, "Restless", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.raveniron.restless";
        public const string Version = "0.1.0";
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> ResetAfterDays;
        internal static ConfigEntry<float> TriggerDistance;
        internal static ConfigEntry<float> SafeDistance;
        internal static ConfigEntry<bool> SkipIfPlayerBuilt;
        internal static ConfigEntry<bool> KeepDroppedItems;
        internal static ConfigEntry<string> Exclude;
        internal static ConfigEntry<string> Include;
        internal static ConfigEntry<bool> VerboseLogging;

        private const float CheckSeconds = 5f;
        private float _nextCheck;
        private ZRoutedRpc _rpcFor;
        private static HashSet<string> _exclude = new HashSet<string>();
        private static string _excludeFrom;
        private readonly Dictionary<Vector2s, float> _cooldown = new Dictionary<Vector2s, float>();
        private readonly HashSet<Vector2s> _seen = new HashSet<Vector2s>();

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("1 - General", "Enabled", true,
                "Master switch. Read on the server.");
            ResetAfterDays = Config.Bind("1 - General", "ResetAfterDays", 30f,
                new ConfigDescription("In-game days after which a location resets. World time stops while the server is empty, " +
                                      "so this counts days somebody was online. A location Restless has never seen starts its count " +
                                      "the first time a player comes near it.", new AcceptableValueRange<float>(0.1f, 1000f)));
            TriggerDistance = Config.Bind("1 - General", "TriggerDistance", 300f,
                new ConfigDescription("A due location resets when a player comes this close (metres, across the ground).",
                    new AcceptableValueRange<float>(50f, 1000f)));
            SafeDistance = Config.Bind("1 - General", "SafeDistance", 100f,
                new ConfigDescription("...but never while any player is this close to its edge, or inside its dungeon.",
                    new AcceptableValueRange<float>(0f, 500f)));
            SkipIfPlayerBuilt = Config.Bind("2 - Protection", "SkipIfPlayerBuilt", true,
                "Leave a location alone (and check again after another ResetAfterDays) when players have built inside it. " +
                "Off: reset it anyway; player pieces are never deleted either way, but the location's own walls come back around them.");
            KeepDroppedItems = Config.Bind("2 - Protection", "KeepDroppedItems", true,
                "Items lying on the ground inside a location survive its reset. Off: they are wiped with it.");
            Include = Config.Bind("1 - General", "Include", Rules.DefaultInclude,
                "Outdoor locations that reset too, comma separated (dungeons always do). Default: fuling villages (GoblinCamp2), " +
                "charred fortresses (CharredFortress) and troll caves (TrollCave02). `restless` in the console names the location you are standing at.");
            Exclude = Config.Bind("2 - Protection", "Exclude", Rules.DefaultExclude,
                "Location names that never reset, comma separated; wins over Include. `restless` in the console names the one you are standing at.");
            VerboseLogging = Config.Bind("3 - Debug", "VerboseLogging", false,
                "Log every location as it is first stamped and every reset that was held back.");

            ConfigWatch.Init(Config, Enabled, ResetAfterDays, TriggerDistance, SafeDistance, Include, SkipIfPlayerBuilt,
                KeepDroppedItems, Exclude, VerboseLogging);

            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"Restless {Version} loaded.");
        }

        private void Update()
        {
            try
            {
                if (ZRoutedRpc.instance != null && ZRoutedRpc.instance != _rpcFor)
                {
                    _rpcFor = ZRoutedRpc.instance;
                    Commands.Register(_rpcFor);
                    _cooldown.Clear();
                    _seen.Clear();
                }
                ConfigWatch.Tick(Time.deltaTime);
                Commands.Pump();
                if (Time.time < _nextCheck) return;
                _nextCheck = Time.time + CheckSeconds;
                if (!Enabled.Value || ZNet.instance == null || !ZNet.instance.IsServer() || ZoneSystem.instance == null
                    || ZDOMan.instance == null || EnvMan.instance == null) return;
                Check();
            }
            catch (Exception e)
            {
                Log.LogError($"Check failed: {e}");
            }
        }

        internal static bool IsExcluded(string name)
        {
            if (_excludeFrom != Exclude.Value) { _exclude = Rules.ParseList(Exclude.Value); _excludeFrom = Exclude.Value; }
            return _exclude.Contains(name);
        }

        private static HashSet<string> _include = new HashSet<string>();
        private static string _includeFrom;

        internal static bool IsIncluded(string name)
        {
            if (_includeFrom != Include.Value) { _include = Rules.ParseList(Include.Value); _includeFrom = Include.Value; }
            return _include.Contains(name);
        }

        internal static double Now => ZNet.instance.GetTimeSeconds();
        internal static double DayLength => Math.Max(1, EnvMan.instance.m_dayLengthSec);

        private void Check()
        {
            var players = ZNet.instance.GetAllCharacterZDOS();
            if (players.Count == 0) return;
            double now = Now;
            float trigger = TriggerDistance.Value;
            int reach = Mathf.CeilToInt(trigger / ZoneSystem.c_ZoneSize) + 1;

            // Every zone with a location within reach of any player.
            var candidates = new HashSet<Vector2s>();
            foreach (var pz in players)
            {
                Vector2s c = ZoneSystem.GetZone(pz.GetPosition());
                for (int dx = -reach; dx <= reach; dx++)
                for (int dy = -reach; dy <= reach; dy++)
                {
                    var z = new Vector2s((short)(c.x + dx), (short)(c.y + dy));
                    if (ZoneSystem.instance.m_locationInstances.ContainsKey(z)) candidates.Add(z);
                }
            }

            foreach (var zone in candidates)
            {
                if (_cooldown.TryGetValue(zone, out var until) && Time.time < until) continue;
                var site = Resetter.FindSite(zone);
                if (site == null) continue;
                if (Resetter.Eligible(site.Location, out _) != true) continue;
                if (!AnyWithin(players, site, trigger)) continue;

                long stamp = Resetter.GetStamp(site);
                if (stamp == Rules.Unstamped)
                {
                    Resetter.SetStamp(site.Proxy, now);
                    if (VerboseLogging.Value) Log.LogInfo($"First sight of {site.Name} in zone {zone}: its {ResetAfterDays.Value:0.#} days start now.");
                    continue;
                }
                if (!Rules.IsDue(stamp, now, ResetAfterDays.Value, DayLength)) continue;

                if (Blocker(players, site, SafeDistance.Value) is string who)
                {
                    if (VerboseLogging.Value && _seen.Add(zone)) Log.LogInfo($"{site.Name} in zone {zone} is due but {who} is too close.");
                    continue;
                }

                var r = Resetter.Reset(site, now);
                // Terrain still building: look again on the next check instead of in a minute.
                _cooldown[zone] = Time.time + (r.Waiting ? 0f : 60f);
                Report(r, site, "due");
                if (r.Waiting) continue;
                return; // one per check: a dungeon is a 50-200 ms spike on the server thread
            }
        }

        internal static void Report(Resetter.Result r, Resetter.Site site, string reason)
        {
            Vector3 p = site.Pos;
            if (r.Done)
            {
                Log.LogInfo($"Reset {site.Name} at ({p.x:0}, {p.z:0}), zone {site.Zone} ({reason}): removed {r.Deleted}, kept {r.Kept}, respawned {r.Spawned}.");
                if (VerboseLogging.Value && r.Kept > 0) Log.LogInfo($"  kept: {r.KeptSummary(12)}");
            }
            else if (r.Waiting)
            {
                if (VerboseLogging.Value) Log.LogInfo($"{site.Name} in zone {site.Zone}: waiting for its terrain to build.");
            }
            else if (r.Retry)
                Log.LogWarning($"Did not reset {site.Name} at ({p.x:0}, {p.z:0}): {r.Why}. Will try again.");
            else
            {
                // A held-back reset is stamped so it waits another full interval instead of retrying every five seconds.
                Resetter.SetStamp(site.Proxy, Now);
                Log.LogInfo($"Did not reset {site.Name} at ({p.x:0}, {p.z:0}): {r.Why}. Next try in {ResetAfterDays.Value:0.#} days.");
            }
        }

        private static bool AnyWithin(List<ZDO> players, Resetter.Site site, float distance)
        {
            foreach (var pz in players)
                if (Resetter.Contains(site, Flatten(pz.GetPosition(), site), distance)) return true;
            return false;
        }

        /// <summary>The name of a player in the location or within <paramref name="margin"/> of it, else null.</summary>
        internal static string Blocker(List<ZDO> players, Resetter.Site site, float margin)
        {
            foreach (var pz in players)
            {
                Vector3 p = pz.GetPosition();
                // Inside the dungeon is always a block; above ground, the margin around its edge.
                if (Resetter.Contains(site, p, 2f) || Resetter.Contains(site, Flatten(p, site), margin))
                    return pz.GetString(ZDOVars.s_playerName, "a player");
            }
            return null;
        }

        /// <summary>The point at the location's own height, so above-ground distance ignores the dungeon floor.</summary>
        private static Vector3 Flatten(Vector3 p, Resetter.Site site) => new Vector3(p.x, site.Pos.y, p.z);
    }
}
