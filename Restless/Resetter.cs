using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RavenIron.Restless
{
    /// <summary>
    /// Wipes one location back to how the world generator first made it, on the server, using vanilla's own spawn
    /// path so the result is the same layout, the same seed and the same dungeon rooms.
    ///
    /// The anchor is the location's LocationProxy ZDO: vanilla stores the location hash and seed on it, and its
    /// position and rotation are the ones the location was placed with. A reset destroys what the location put
    /// there (and the monsters standing in it), destroys the old proxy, and calls ZoneSystem.SpawnLocation in
    /// GHOST mode, exactly as vanilla does when the server generates a zone nobody is standing in: the objects
    /// are created as ZDOs, the GameObjects are thrown away, and each client builds them when it arrives.
    /// The reset time is stamped on the NEW proxy.
    ///
    /// Three shapes of location, read from the prefab:
    ///  - INTERIOR dungeon: a DungeonGenerator ~5000 m up (crypts, caves, mines). Everything in the zone-centred
    ///    dungeon box up there belongs to it.
    ///  - GROUND generator: a DungeonGenerator at ground level (the fuling village's DG_GoblinCamp). Its huts are
    ///    rooms, not part of the location prefab, so inside its box everything is wiped except vegetation.
    ///  - PLAIN: no generator. Only prefabs that occur in the location prefab itself are wiped, within its radius.
    /// Above ground the creatures standing in it go too.
    ///
    /// Never deleted: players, tombstones, anything with a creator (player pieces, carts, boats), tamed creatures,
    /// terrain edits, the zone's control object, vegetation, objects whose prefab the server cannot resolve, and
    /// (by default) dropped items.
    /// </summary>
    internal static class Resetter
    {
        internal static readonly int ProxyHash = "LocationProxy".GetStableHashCode();
        internal static readonly int StampHash = "RavenIron.Restless.ResetAt".GetStableHashCode();
        private static readonly int PlayerHash = "Player".GetStableHashCode();
        private static readonly int TombstoneHash = "Player_tombstone".GetStableHashCode();
        private static readonly int TerrainHash = "_TerrainCompiler".GetStableHashCode();

        /// <summary>Anything this far above the entrance is the dungeon interior (vanilla puts them ~5000 m up).</summary>
        internal const float InteriorAbove = 1000f;

        // ZoneSystem.SpawnLocation is PRIVATE in the real assembly (publicized assemblies are compile-time only),
        // so it is reached through a cached delegate, resolved until it succeeds.
        private delegate GameObject SpawnLocationFn(ZoneSystem zs, ZoneSystem.ZoneLocation location, int seed, Vector3 pos,
            Quaternion rot, ZoneSystem.SpawnMode mode, List<GameObject> spawnedGhostObjects, bool cheated);
        private static SpawnLocationFn _spawn;
        private static bool _spawnMissingLogged;

        private static readonly Dictionary<int, LocationShape> Shapes = new Dictionary<int, LocationShape>();
        private static readonly Dictionary<int, PrefabKind> Kinds = new Dictionary<int, PrefabKind>();
        private static readonly List<ZDO> Scratch = new List<ZDO>();
        private static HashSet<int> _vegetation;
        private static int _zoneCtrlHash;

        internal sealed class Site
        {
            public ZoneSystem.ZoneLocation Location;
            public ZDO Proxy;
            public Vector2s Zone;
            public string Name => Location.m_prefab.Name;
            public Vector3 Pos => Proxy.GetPosition();
        }

        internal enum Shape { Plain, Interior, GroundGenerator }

        private sealed class LocationShape
        {
            /// <summary>Every networked prefab the location can place: its own children and, for a generator, every room's.</summary>
            public readonly HashSet<int> Prefabs = new HashSet<int>();
            public Shape Shape;
            public Vector3 DungeonSize;
            /// <summary>How far from its centre the location's own above-ground objects reach (a troll cave runs back into the hill).</summary>
            public float Reach;
            /// <summary>
            /// How far from its centre the location's objects placed high above it reach, or -1 for none. A troll cave
            /// (TrollCave02) has no DungeonGenerator but builds its interior 5000 m up, like a dungeon: its spawner, chests
            /// and pickables sit there. Seen in testing: those were never swept, so each reset stacked another copy.
            /// </summary>
            public float HighReach = -1f;
        }

        [Flags]
        private enum PrefabKind { None = 0, Unknown = 1, Character = 2, Item = 4, Piece = 8, Structure = 16 }

        internal static bool TryResolve()
        {
            if (_spawn != null) return true;
            MethodInfo m = AccessTools.Method(typeof(ZoneSystem), "SpawnLocation", new[]
            {
                typeof(ZoneSystem.ZoneLocation), typeof(int), typeof(Vector3), typeof(Quaternion),
                typeof(ZoneSystem.SpawnMode), typeof(List<GameObject>), typeof(bool)
            });
            if (m == null)
            {
                if (!_spawnMissingLogged)
                {
                    Plugin.Log.LogError("ZoneSystem.SpawnLocation(ZoneLocation, int, Vector3, Quaternion, SpawnMode, List<GameObject>, bool) " +
                                        "was not found: Valheim's API moved. Restless will not reset anything until it resolves.");
                    _spawnMissingLogged = true;
                }
                return false;
            }
            _spawn = AccessTools.MethodDelegate<SpawnLocationFn>(m);
            Plugin.Log.LogInfo("Resolved ZoneSystem.SpawnLocation.");
            return true;
        }

        /// <summary>The placed location in this zone with its proxy, or null when the zone has none or is not generated.</summary>
        internal static Site FindSite(Vector2s zone)
        {
            var zs = ZoneSystem.instance;
            if (zs == null || !zs.m_locationInstances.TryGetValue(zone, out var inst) || !inst.m_placed || inst.m_location == null)
                return null;
            int hash = inst.m_location.m_prefab.Name.GetStableHashCode();
            Scratch.Clear();
            ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(0, 0, classic: true), Scratch);
            ZDO best = null;
            long bestStamp = long.MinValue;
            foreach (var zdo in Scratch)
            {
                if (zdo.GetPrefab() != ProxyHash || zdo.GetInt(ZDOVars.s_location) != hash) continue;
                // Right after a reset the old proxy can still be here for a frame (destroys go out on ZDOMan's next
                // send): the newest stamp is the live one.
                long stamp = zdo.GetLong(StampHash);
                if (best == null || stamp > bestStamp) { best = zdo; bestStamp = stamp; }
            }
            Scratch.Clear();
            return best == null ? null : new Site { Location = inst.m_location, Proxy = best, Zone = zone };
        }

        internal static long GetStamp(Site site) => site.Proxy.GetLong(StampHash);

        internal static void SetStamp(ZDO proxy, double now)
        {
            if (!proxy.IsOwner()) proxy.SetOwner(ZDOMan.GetSessionID());
            proxy.Set(StampHash, Rules.Stamp(now));
        }

        /// <summary>
        /// Does Restless reset this location? Interior dungeons always (unless excluded); anything else only when it is
        /// on the Include list. Returns null when the prefab could not be read (retried next time); <paramref name="why"/>
        /// says which rule decided, for the status command.
        /// </summary>
        internal static bool? Eligible(ZoneSystem.ZoneLocation loc, out string why)
        {
            string name = loc.m_prefab.Name;
            bool known = Shapes.ContainsKey(name.GetStableHashCode());
            var shape = ShapeOf(loc);
            if (shape == null) { why = "its prefab could not be read"; return null; }

            bool result;
            if (Plugin.IsExcluded(name)) { why = "it is on the Exclude list"; result = false; }
            else if (shape.Shape == Shape.Interior) { why = "it is a dungeon"; result = true; }
            else if (Plugin.IsIncluded(name)) { why = "it is on the Include list"; result = true; }
            else { why = "it has no dungeon and is not on the Include list"; result = false; }

            // Logged once per kind of location, the first time one is seen: what it is, and whether it resets here.
            // (The lists are read live, so an Include or Exclude edit later can change the answer; `restless` says
            // the current one.)
            if (!known) Plugin.Log.LogInfo($"{name}: {Describe(shape.Shape)}, {(result ? "resets" : "does not reset")} ({why}).");
            return result;
        }

        private static string Describe(Shape s) => s == Shape.Interior ? "dungeon"
            : s == Shape.GroundGenerator ? "outdoor, generated" : "outdoor";

        internal static string PrefabName(int hash)
        {
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
            return go != null ? go.name : hash.ToString();
        }

        /// <summary>Verbose: the footprint and the location's own prefabs, for the reset log.</summary>
        internal static string FootprintText(Site site)
        {
            var shape = ShapeOf(site.Location);
            if (shape == null) return "no shape";
            var names = new List<string>();
            foreach (var h in shape.Prefabs) if (names.Count < 30) names.Add(PrefabName(h));
            return $"radius {Radius(site):0} m (exterior {site.Location.m_exteriorRadius:0}, reach {shape.Reach:0}, high reach {shape.HighReach:0}), " +
                   $"{shape.Shape}, {shape.Prefabs.Count} own prefab(s): {string.Join(", ", names.ToArray())}";
        }

        /// <summary>Horizontal radius of the location's above-ground footprint.</summary>
        internal static float Radius(Site site)
        {
            var shape = ShapeOf(site.Location);
            float reach = shape != null ? shape.Reach : 0f;
            return Mathf.Max(Mathf.Max(site.Location.m_exteriorRadius, reach), 8f) + 1f;
        }

        /// <summary>Is this world point inside the location: its circle, its ground generator's box, or its dungeon?</summary>
        internal static bool Contains(Site site, Vector3 p, float margin)
        {
            Vector3 c = site.Pos;
            var shape = ShapeOf(site.Location);
            if (p.y > c.y + InteriorAbove)
            {
                if (shape == null) return false;
                if (shape.Shape == Shape.Interior) return InBox(site, shape, p, margin);
                if (shape.HighReach < 0f) return false;
                // Held inside the location's own zone, like InBox, so it never reaches a neighbour's interior.
                Vector3 zc = ZoneSystem.GetZonePos(site.Zone);
                float half = ZoneSystem.c_ZoneSize * 0.5f + margin;
                if (Mathf.Abs(p.x - zc.x) > half || Mathf.Abs(p.z - zc.z) > half) return false;
                float hx = p.x - c.x, hz = p.z - c.z, hr = shape.HighReach + margin;
                return hx * hx + hz * hz <= hr * hr;
            }
            float dx = p.x - c.x, dz = p.z - c.z;
            float r = Radius(site) + margin;
            if (dx * dx + dz * dz <= r * r) return true;
            return shape != null && shape.Shape == Shape.GroundGenerator && InBox(site, shape, p, margin);
        }

        /// <summary>
        /// The generator's box, centred on the zone as DungeonGenerator.Generate centres it, and held inside this zone
        /// (plus the margin) so a big box can never reach a neighbour's location.
        /// </summary>
        private static bool InBox(Site site, LocationShape shape, Vector3 p, float margin)
        {
            Vector3 zc = ZoneSystem.GetZonePos(site.Zone);
            float hx = Mathf.Min(shape.DungeonSize.x * 0.5f, ZoneSystem.c_ZoneSize * 0.5f) + margin;
            float hz = Mathf.Min(shape.DungeonSize.z * 0.5f, ZoneSystem.c_ZoneSize * 0.5f) + margin;
            return Mathf.Abs(p.x - zc.x) <= hx && Mathf.Abs(p.z - zc.z) <= hz;
        }

        internal sealed class Result
        {
            /// <summary>Retry: try again soon. Waiting: only the terrain was not built yet, which is routine, not a fault.</summary>
            public bool Done, Retry, Waiting;
            public string Why = "";
            public int Deleted, Kept, Spawned;
            public readonly Dictionary<int, int> KeptByPrefab = new Dictionary<int, int>();

            /// <summary>The most-kept prefabs, for the verbose log: "Pickable_Barley x12, ...".</summary>
            public string KeptSummary(int top)
            {
                var list = new List<KeyValuePair<int, int>>(KeptByPrefab);
                list.Sort((a, b) => b.Value.CompareTo(a.Value));
                var parts = new List<string>();
                for (int i = 0; i < list.Count && i < top; i++)
                {
                    var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(list[i].Key) : null;
                    parts.Add($"{(go != null ? go.name : list[i].Key.ToString())} x{list[i].Value}");
                }
                return parts.Count == 0 ? "nothing" : string.Join(", ", parts.ToArray());
            }

            /// <summary>Verbose only: what was wiped, and what lay just outside the footprint.</summary>
            public readonly Dictionary<string, int> Removed = new Dictionary<string, int>();
            public readonly List<string> Outside = new List<string>();
            public void Near(string s) { if (Outside.Count < 40) Outside.Add(s); }

            public static string Summary(Dictionary<string, int> d, int top)
            {
                var list = new List<KeyValuePair<string, int>>(d);
                list.Sort((a, b) => b.Value.CompareTo(a.Value));
                var parts = new List<string>();
                for (int i = 0; i < list.Count && i < top; i++) parts.Add($"{list[i].Key} x{list[i].Value}");
                return parts.Count == 0 ? "nothing" : string.Join(", ", parts.ToArray());
            }

            public void Keep(int prefab)
            {
                Kept++;
                KeptByPrefab.TryGetValue(prefab, out var n);
                KeptByPrefab[prefab] = n + 1;
            }
        }

        /// <summary>Reset one location now. The caller has already decided it is safe (nobody standing in it).</summary>
        internal static Result Reset(Site site, double now)
        {
            var r = new Result();
            if (!TryResolve()) { r.Retry = true; r.Why = "ZoneSystem.SpawnLocation is unavailable (see the error above)"; return r; }
            var shape = ShapeOf(site.Location);
            if (shape == null) { r.Retry = true; r.Why = "its prefab could not be loaded"; return r; }

            Vector3 pos = site.Pos;
            Quaternion rot = site.Proxy.GetRotation();
            int seed = site.Proxy.GetInt(ZDOVars.s_seed);

            // 0. Terrain. When vanilla first generates a zone, the zone's terrain is loaded, and the generator drops every
            // camp room onto it (DungeonGenerator calls ZoneSystem.GetGroundData, a raycast). A dedicated server has no
            // terrain for a zone it is not generating, so the raycast misses, every hut lands at the centre's height, and
            // a slope buries half of them (seen in testing). So the zone's terrain is loaded for the rebuild,
            // the way SpawnZone loads it, and dropped after. Checked BEFORE anything is wiped.
            GameObject terrain = null;
            if (!ZoneSystem.instance.IsZoneLoaded(site.Zone))
            {
                var zonePrefab = ZoneSystem.instance.m_zonePrefab;
                var hm = zonePrefab != null ? zonePrefab.GetComponentInChildren<Heightmap>() : null;
                if (hm == null) { r.Retry = true; r.Why = "the zone prefab has no Heightmap"; return r; }
                Vector3 zonePos = ZoneSystem.GetZonePos(site.Zone);
                // Asks the builder to start on it if it has not; true once it is ready (usually a frame or two later).
                if (!HeightmapBuilder.instance.IsTerrainReady(zonePos, hm.m_width, hm.m_scale, hm.IsDistantLod, WorldGenerator.instance))
                {
                    r.Retry = true; r.Waiting = true; r.Why = "its terrain is still being built";
                    return r;
                }
                terrain = UnityEngine.Object.Instantiate(zonePrefab, zonePos, Quaternion.identity);
            }
            try { return Rebuild(site, shape, r, pos, rot, seed, now); }
            finally { if (terrain != null) UnityEngine.Object.Destroy(terrain); }
        }

        private static Result Rebuild(Site site, LocationShape shape, Result r, Vector3 pos, Quaternion rot, int seed, double now)
        {
            var vegetation = Vegetation();

            // 1. Sort everything in the 3x3 zones around it into wipe / keep.
            var wipe = new List<ZDO>();
            var all = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(site.Zone, new SimulationDistance(1, 0, classic: true), all);
            bool playerBuilt = false;
            foreach (var zdo in all)
            {
                if (zdo == site.Proxy) { wipe.Add(zdo); continue; }
                Vector3 p = zdo.GetPosition();
                if (!Contains(site, p, 0f))
                {
                    // Diagnostics: what lies just outside the footprint, so a too-small footprint shows up in one run.
                    if (Plugin.VerboseLogging.Value)
                    {
                        float flat = new Vector2(p.x - pos.x, p.z - pos.z).magnitude;
                        if (flat < Radius(site) + 40f) r.Near($"{PrefabName(zdo.GetPrefab())} {flat:0}m dy{p.y - pos.y:0}");
                    }
                    continue;
                }
                bool interior = p.y > pos.y + InteriorAbove;
                int prefab = zdo.GetPrefab();

                // A prefab the location (or one of its rooms) places is its own, even when the same prefab also grows
                // wild or can be dropped: a fuling camp's barley is barley. Sparing those let every reset leave the old
                // copies behind (in testing, a camp's kept count went 20, 44, 65 over three resets).
                bool own = shape.Prefabs.Contains(prefab);

                if (prefab == ProxyHash || prefab == PlayerHash || prefab == TombstoneHash || prefab == TerrainHash
                    || prefab == _zoneCtrlHash || (!own && vegetation.Contains(prefab))) { r.Keep(prefab); continue; }
                var kind = KindOf(prefab);
                if ((kind & PrefabKind.Unknown) != 0) { r.Keep(prefab); continue; }
                if (zdo.GetLong(ZDOVars.s_creator) != 0)
                {
                    if ((kind & PrefabKind.Piece) != 0) playerBuilt = true;
                    r.Keep(prefab);
                    continue;
                }
                if (zdo.GetBool(ZDOVars.s_tamed)) { r.Keep(prefab); continue; }
                if (!own && (kind & PrefabKind.Item) != 0 && Plugin.KeepDroppedItems.Value) { r.Keep(prefab); continue; }

                // Inside a dungeon, or a ground generator's box, everything not protected above belongs to it. Elsewhere:
                // what the location places, the creatures standing in it, and any structure nobody built. A structure
                // (a piece, or anything that can be broken) with no creator inside a location is the location's, even
                // when its prefab is not in the location's own list: the charred fortress gains a ballista
                // (piece_Charred_Balista) per rebuild from something other than its own children, and its broken walls
                // leave `_frac` rubble. Player pieces carry a creator and natural rocks are vegetation, both handled above.
                // High up, a location with no generator (a troll cave) owns only its own prefabs. Its interior can sit
                // beside a neighbouring dungeon's, and wiping everything there took a burial chamber's door and skeleton
                // spawner in testing. Its creatures go with their spawners (step 1b).
                if (interior && shape.Shape != Shape.Interior)
                {
                    if (own) wipe.Add(zdo); else r.Keep(prefab);
                    continue;
                }
                bool generated = interior || (shape.Shape == Shape.GroundGenerator && InBox(site, shape, p, 0f));
                bool structure = (kind & PrefabKind.Structure) != 0;
                if (generated || own || structure || (kind & PrefabKind.Character) != 0) wipe.Add(zdo);
                else r.Keep(prefab);
            }

            if (playerBuilt && Plugin.SkipIfPlayerBuilt.Value)
            {
                r.Why = "players have built there (SkipIfPlayerBuilt)";
                return r;
            }

            // 1b. The creatures the location's spawners made, wherever they stand now. A troll that followed a player out
            // of its cave is outside the footprint, so the sweep above never sees it; the rebuilt spawner has no link to
            // it and spawns another, so every reset added a troll (seen in testing). Each CreatureSpawner keeps that link
            // on its own ZDO (ConnectionType.Spawned). A tamed or player-made creature is still never touched.
            var seen = new HashSet<ZDOID>();
            foreach (var zdo in wipe) seen.Add(zdo.m_uid);
            int linked = wipe.Count;
            for (int i = 0; i < linked; i++)
            {
                ZDOID spawned = wipe[i].GetConnectionZDOID(ZDOExtraData.ConnectionType.Spawned);
                if (spawned.IsNone() || !seen.Add(spawned)) continue;
                var creature = ZDOMan.instance.GetZDO(spawned);
                if (creature == null) continue;
                int cp = creature.GetPrefab();
                if (cp == PlayerHash || creature.GetBool(ZDOVars.s_tamed) || creature.GetLong(ZDOVars.s_creator) != 0) continue;
                if ((KindOf(cp) & PrefabKind.Character) == 0) continue;
                wipe.Add(creature);
            }

            // 2. Wipe. The server takes ownership first: DestroyZDO is a no-op for a ZDO it does not own.
            long me = ZDOMan.GetSessionID();
            foreach (var zdo in wipe)
            {
                zdo.SetOwner(me);
                ZDOMan.instance.DestroyZDO(zdo);
            }
            r.Deleted = wipe.Count;
            if (Plugin.VerboseLogging.Value)
                foreach (var zdo in wipe)
                {
                    string n = PrefabName(zdo.GetPrefab());
                    r.Removed.TryGetValue(n, out var c);
                    r.Removed[n] = c + 1;
                }

            // 3. Respawn through vanilla, in ghost mode, then stamp the new proxy and throw the GameObjects away.
            var ghosts = new List<GameObject>();
            try
            {
                _spawn(ZoneSystem.instance, site.Location, seed, pos, rot, ZoneSystem.SpawnMode.Ghost, ghosts, false);
            }
            finally
            {
                foreach (var go in ghosts)
                {
                    if (go == null) continue;
                    try
                    {
                        if (go.GetComponent<LocationProxy>() != null)
                        {
                            var zdo = go.GetComponent<ZNetView>()?.GetZDO();
                            if (zdo != null) SetStamp(zdo, now);
                        }
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"Could not stamp the new proxy of {site.Name}: {e.Message}"); }
                    UnityEngine.Object.Destroy(go);
                }
            }
            r.Spawned = ghosts.Count;
            r.Done = true;
            return r;
        }

        /// <summary>Every prefab the zone generator scatters (trees, rocks, ore, pickable flint...): never a location's.</summary>
        private static HashSet<int> Vegetation()
        {
            if (_vegetation != null) return _vegetation;
            var set = new HashSet<int>();
            var zs = ZoneSystem.instance;
            foreach (var veg in zs.m_vegetation)
                if (veg != null && veg.m_prefab != null) set.Add(Utils.GetPrefabName(veg.m_prefab).GetStableHashCode());
            if (zs.m_zoneCtrlPrefab != null) _zoneCtrlHash = Utils.GetPrefabName(zs.m_zoneCtrlPrefab).GetStableHashCode();
            if (set.Count > 0) _vegetation = set; // empty means ZoneSystem is not set up yet: read again next time
            return set;
        }

        private static LocationShape ShapeOf(ZoneSystem.ZoneLocation loc)
        {
            int key = loc.m_prefab.Name.GetStableHashCode();
            if (Shapes.TryGetValue(key, out var cached)) return cached;
            LocationShape shape = null;
            try
            {
                loc.m_prefab.Load();
                GameObject asset = loc.m_prefab.Asset;
                if (asset != null)
                {
                    shape = new LocationShape();
                    Vector3 root = asset.transform.position;
                    foreach (var nv in asset.GetComponentsInChildren<ZNetView>(true))
                    {
                        shape.Prefabs.Add(Utils.GetPrefabName(nv.gameObject).GetStableHashCode());
                        Vector3 d = nv.transform.position - root;
                        float flat = new Vector2(d.x, d.z).magnitude + 2f;
                        if (d.y < InteriorAbove) shape.Reach = Mathf.Max(shape.Reach, flat);
                        else shape.HighReach = Mathf.Max(shape.HighReach, flat);
                    }
                    var gen = asset.GetComponentInChildren<DungeonGenerator>(true);
                    if (gen != null)
                    {
                        shape.DungeonSize = gen.m_zoneSize;
                        float up = gen.transform.position.y - root.y;
                        shape.Shape = up > InteriorAbove ? Shape.Interior : Shape.GroundGenerator;
                        // A ground camp's huts are rooms the generator picks: learn every room's (and door's) prefabs, so a
                        // reset clears them even when they look like wild growth. Interior dungeons need no list: their
                        // whole box is cleared.
                        if (shape.Shape == Shape.GroundGenerator) AddRoomPrefabs(gen, shape.Prefabs, loc.m_prefab.Name);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not read the shape of {loc.m_prefab.Name}: {e.Message}");
            }
            finally
            {
                try { loc.m_prefab.Release(); } catch { /* never loaded */ }
            }
            if (shape != null) Shapes[key] = shape; // a failure is retried next time
            return shape;
        }

        private static void AddRoomPrefabs(DungeonGenerator gen, HashSet<int> into, string location)
        {
            int rooms = 0, before = into.Count;
            foreach (var door in gen.m_doorTypes)
                if (door != null && door.m_prefab != null)
                    foreach (var nv in door.m_prefab.GetComponentsInChildren<ZNetView>(true))
                        into.Add(Utils.GetPrefabName(nv.gameObject).GetStableHashCode());
            foreach (var room in gen.GetAvailableRoomPrefabs())
            {
                try
                {
                    room.Load();
                    if (room.Asset == null) continue;
                    rooms++;
                    foreach (var nv in room.Asset.GetComponentsInChildren<ZNetView>(true))
                        into.Add(Utils.GetPrefabName(nv.gameObject).GetStableHashCode());
                }
                catch (Exception e) { Plugin.Log.LogWarning($"{location}: could not read a room prefab: {e.Message}"); }
                finally { try { room.Release(); } catch { /* never loaded */ } }
            }
            Plugin.Log.LogInfo($"{location}: read {rooms} room prefab(s), {into.Count - before} prefab(s) its rooms place.");
        }

        private static PrefabKind KindOf(int prefab)
        {
            if (Kinds.TryGetValue(prefab, out var k)) return k;
            GameObject go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            if (go == null) k = PrefabKind.Unknown;
            else
            {
                k = PrefabKind.None;
                if (go.GetComponent<Character>() != null) k |= PrefabKind.Character;
                if (go.GetComponent<ItemDrop>() != null) k |= PrefabKind.Item;
                if (go.GetComponent<Piece>() != null) k |= PrefabKind.Piece;
                if ((k & PrefabKind.Piece) != 0 || go.GetComponent<WearNTear>() != null || go.GetComponent<Destructible>() != null)
                    k |= PrefabKind.Structure;
            }
            Kinds[prefab] = k;
            return k;
        }
    }
}
