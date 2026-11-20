using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace RavenIron.Restless
{
    /// <summary>
    /// Admin console commands, F5:
    ///   restless              the location you are at: whether it resets, and when
    ///   restless 200          every location within 200 m: whether each resets, and when
    ///   restless reset        reset the nearest location that can reset (within 40 m of its edge)
    ///   restless reset 200    reset every one within 200 m
    ///   restless reload       re-read the server's .cfg now (it also re-reads itself within 3 s of being saved)
    /// The client forwards the verb, the radius and its position; the server checks the adminlist itself.
    /// With no mod on the client, the same commands go through vanilla's `kick` in chat (see Patch_Kick):
    ///   /kick restless   /kick restless-200   /kick restless-reset   /kick restless-reset-200   /kick restless-reload
    /// Resets are queued and run one every half second, so a big radius never stalls the server, and the asker
    /// gets one summary when the batch is done.
    /// </summary>
    internal static class Commands
    {
        private const string CmdRpc = "Restless_Cmd";
        private const string ReplyRpc = "Restless_Reply";
        private const float NearestRange = 40f;
        private const int MaxListed = 25;
        private const float PumpSeconds = 0.5f;
        private const int MaxTerrainWaits = 40; // 20 s of a zone's terrain not being ready

        internal static void Register(ZRoutedRpc rpc)
        {
            // From a client with the mod (its F5 `restless`): answers go back over Restless_Reply.
            rpc.Register<string, Vector3, float>(CmdRpc, (sender, verb, pos, radius) =>
            {
                _chatSenders.Remove(sender);
                OnCommand(sender, verb, pos, radius);
            });
            _chatSenders.Clear();
            rpc.Register<string>(ReplyRpc, OnReply);
            Batches.Clear();
        }

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
        private static class Patch_InitTerminal
        {
            private static void Postfix()
            {
                new Terminal.ConsoleCommand("restless",
                    "Restless (admins only): restless [radius] = what resets here or within radius m | restless reset [radius] = reset the nearest, or all within radius m | restless reload = re-read the server's config now",
                    (Terminal.ConsoleEvent)(args =>
                    {
                        if (ZRoutedRpc.instance == null || Player.m_localPlayer == null)
                        {
                            args.Context.AddString("Restless: join a world first.");
                            return;
                        }
                        if (args.Args.Length == 2 && string.Equals(args.Args[1].Trim(), "reload", StringComparison.OrdinalIgnoreCase))
                        {
                            ZRoutedRpc.instance.InvokeRoutedRPC(CmdRpc, "reload", Player.m_localPlayer.transform.position, 0f);
                            return;
                        }
                        if (!Rules.ParseCommand(args.Args, out bool reset, out float radius, out string error))
                        {
                            args.Context.AddString("Restless: " + error);
                            return;
                        }
                        // The target-less overload routes to the server. (GetServerPeerID is private in the real assembly.)
                        ZRoutedRpc.instance.InvokeRoutedRPC(CmdRpc, reset ? "reset" : "status", Player.m_localPlayer.transform.position, radius);
                    }));
            }
        }

        private static void OnReply(long sender, string text)
        {
            Plugin.Log.LogInfo(text);
            if (Console.instance != null) Console.instance.Print(text);
        }

        private static void Reply(long to, string text)
        {
            if (_chatSenders.Contains(to))
            {
                // A player without the mod: vanilla's own RemotePrint (the F5 console, as `kick` itself answers) and its
                // ShowMessage (top left) for the first line, so it is seen from the chat box too.
                var peer = ZNet.instance != null ? ZNet.instance.GetPeer(to) : null;
                if (peer != null) peer.m_rpc.Invoke("RemotePrint", text);
                int nl = text.IndexOf('\n');
                ZRoutedRpc.instance?.InvokeRoutedRPC(to, "ShowMessage", (int)MessageHud.MessageType.TopLeft,
                    nl < 0 ? text : text.Substring(0, nl) + " (more in F5)");
                return;
            }
            if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(to, ReplyRpc, text);
        }

        // ---- chat commands for players without the mod --------------------------------------------------------------

        /// <summary>Senders whose last command came through chat, so their answers go out through vanilla RPCs.</summary>
        private static readonly HashSet<long> _chatSenders = new HashSet<long>();

        /// <summary>
        /// A vanilla client cannot send the server any text a mod could read, except through its admin commands. `kick`
        /// carries one word (args[1]) to ZNet.RPC_Kick on the server, and is allowed in chat. So `/kick restless-reset-200`
        /// reaches us with no client mod; any other word is left to vanilla. The server checks the admin list itself.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), "RPC_Kick")]
        private static class Patch_Kick
        {
            [HarmonyPriority(Priority.Low)]
            private static bool Prefix(ZRpc rpc, string user, bool __runOriginal)
            {
                if (!__runOriginal) return false;
                if (!Rules.ParseChatWord(user, out string verb, out float radius, out string error)) return true;
                try
                {
                    ZNetPeer peer = null;
                    foreach (var p in ZNet.instance.GetPeers()) if (p.m_rpc == rpc) { peer = p; break; }
                    if (peer == null) return false;
                    _chatSenders.Add(peer.m_uid);
                    Plugin.Log.LogInfo($"{peer.m_playerName} sent `{user}` through chat.");
                    if (error != null) { Reply(peer.m_uid, "Restless: " + error); return false; }
                    var zdo = ZDOMan.instance?.GetZDO(peer.m_characterID);
                    Vector3 pos = zdo != null ? zdo.GetPosition() : peer.m_refPos;
                    OnCommand(peer.m_uid, verb, pos, radius);
                    // `/kick restless reset 200` arrives as just "restless" (kick sends one word): show how to join them.
                    if (string.Equals(user.Trim(), "restless", StringComparison.OrdinalIgnoreCase))
                        peer.m_rpc.Invoke("RemotePrint", "Restless: " + Rules.ChatUsage + " (join the words with -)");
                }
                catch (Exception e) { Plugin.Log.LogError($"chat command `{user}` failed: {e}"); }
                return false; // ours: never let vanilla try to kick a player called "restless"
            }
        }

        private static void OnCommand(long sender, string verb, Vector3 pos, float radius)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            string reply;
            try { reply = Handle(sender, verb, pos, radius); }
            catch (Exception e) { reply = "Restless: that failed on the server: " + e.Message; Plugin.Log.LogError(e); }
            if (reply != null) Reply(sender, reply);
        }

        private static bool IsAdmin(long sender)
        {
            // ZRoutedRpc's own id is private; ZNet sets it to the ZDOMan session id.
            if (sender == ZDOMan.GetSessionID()) return !ZNet.instance.IsDedicated(); // the host or single player
            var peer = ZNet.instance.GetPeer(sender);
            return peer != null && ZNet.instance.IsAdmin(peer.m_socket.GetHostName());
        }

        private static string Handle(long sender, string verb, Vector3 pos, float radius)
        {
            if (!IsAdmin(sender)) return "Restless: admins only.";
            if (verb == "reload") return "Restless: " + ConfigWatch.ReloadFile("restless reload");
            radius = Mathf.Clamp(radius, 0f, Rules.MaxCommandRadius);
            bool reset = verb == "reset";

            if (radius <= 0f)
            {
                var site = Nearest(pos, eligibleOnly: reset);
                if (site == null)
                    return reset ? $"Restless: no location that resets within {NearestRange:0} m of you."
                                 : $"Restless: no generated location within {NearestRange:0} m of you.";
                if (!reset) return "Restless: " + StatusLine(site);
                Enqueue(sender, new List<Resetter.Site> { site }, single: true);
                return null; // the batch answers when it has run
            }

            var sites = Within(pos, radius);
            if (!reset)
            {
                if (sites.Count == 0) return $"Restless: no generated location within {radius:0} m.";
                var sb = new StringBuilder($"Restless: {sites.Count} location(s) within {radius:0} m:");
                int shown = 0;
                foreach (var s in sites)
                {
                    if (shown++ == MaxListed) { sb.Append($"\n  ...and {sites.Count - MaxListed} more."); break; }
                    sb.Append("\n  ").Append(StatusLine(s));
                }
                return sb.ToString();
            }

            var eligible = sites.FindAll(s => Resetter.Eligible(s.Location, out _) == true);
            if (eligible.Count == 0) return $"Restless: nothing within {radius:0} m resets.";
            Enqueue(sender, eligible, single: false);
            return $"Restless: resetting {eligible.Count} location(s) within {radius:0} m, one every {PumpSeconds:0.#} s. A summary follows.";
        }

        private static string StatusLine(Resetter.Site site)
        {
            string where = $"{site.Name} in zone {site.Zone}";
            bool? eligible = Resetter.Eligible(site.Location, out string why);
            if (eligible == null) return $"{where}: its prefab could not be read.";
            if (eligible == false) return $"{where} never resets: {why}.";
            long stamp = Resetter.GetStamp(site);
            if (stamp == Rules.Unstamped) return $"{where} resets ({why}); not seen yet, its count starts when a player comes near.";
            double since = Rules.DaysSince(stamp, Plugin.Now, Plugin.DayLength);
            double left = Math.Max(0, Plugin.ResetAfterDays.Value - since);
            return $"{where} resets ({why}): last reset or first seen {since:0.0} days ago, due in {left:0.0} days.";
        }

        // ---- the reset queue ------------------------------------------------------------------------------------------

        private sealed class Batch
        {
            public long Sender;
            public bool Single;
            public readonly Queue<Resetter.Site> Sites = new Queue<Resetter.Site>();
            public int Reset, Skipped, Waits;
            public readonly List<string> Notes = new List<string>();
        }

        private static readonly Queue<Batch> Batches = new Queue<Batch>();
        private static float _nextPump;

        private static void Enqueue(long sender, List<Resetter.Site> sites, bool single)
        {
            var b = new Batch { Sender = sender, Single = single };
            foreach (var s in sites) b.Sites.Enqueue(s);
            Batches.Enqueue(b);
        }

        /// <summary>Called from Plugin.Update: at most one reset every PumpSeconds.</summary>
        internal static void Pump()
        {
            if (Batches.Count == 0 || Time.time < _nextPump) return;
            _nextPump = Time.time + PumpSeconds;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) { Batches.Clear(); return; }

            var b = Batches.Peek();
            if (b.Sites.Count == 0) { Finish(Batches.Dequeue()); return; }

            var queued = b.Sites.Peek();
            // Look the site up again: the world may have moved on since the command (another reset, a new proxy).
            var site = Resetter.FindSite(queued.Zone);
            string where = $"{queued.Name} in zone {queued.Zone}";
            if (site == null) { b.Sites.Dequeue(); Skip(b, $"{where}: no longer found"); return; }

            foreach (var pz in ZNet.instance.GetAllCharacterZDOS())
                if (Resetter.Contains(site, pz.GetPosition(), 2f))
                {
                    b.Sites.Dequeue();
                    Skip(b, $"{where}: {pz.GetString(ZDOVars.s_playerName, "a player")} is inside it");
                    return;
                }

            var r = Resetter.Reset(site, Plugin.Now);
            if (r.Waiting && b.Waits++ < MaxTerrainWaits) return; // same site again next pump
            b.Sites.Dequeue();
            b.Waits = 0;
            Plugin.Report(r, site, "admin");
            if (r.Done)
            {
                b.Reset++;
                if (b.Single) b.Notes.Add($"reset {where}: removed {r.Deleted}, kept {r.Kept}, respawned {r.Spawned}");
            }
            else Skip(b, $"{where}: {r.Why}");
        }

        private static void Skip(Batch b, string note)
        {
            b.Skipped++;
            b.Notes.Add((b.Single ? "did not reset " : "skipped ") + note);
        }

        private static void Finish(Batch b)
        {
            if (b.Single)
            {
                Reply(b.Sender, "Restless: " + (b.Notes.Count > 0 ? b.Notes[0] : "nothing happened") + ".");
                return;
            }
            var sb = new StringBuilder($"Restless: done. Reset {b.Reset}, skipped {b.Skipped}.");
            int shown = 0;
            foreach (var n in b.Notes)
            {
                if (shown++ == MaxListed) { sb.Append($"\n  ...and {b.Notes.Count - MaxListed} more."); break; }
                sb.Append("\n  ").Append(n);
            }
            Reply(b.Sender, sb.ToString());
        }

        // ---- finding locations ----------------------------------------------------------------------------------------

        /// <summary>
        /// The location closest to the point, within 40 m of its edge, or the dungeon you are standing in. With
        /// eligibleOnly, locations that never reset are passed over, so a neighbouring ruin cannot win.
        /// </summary>
        private static Resetter.Site Nearest(Vector3 pos, bool eligibleOnly)
        {
            Vector2s c = ZoneSystem.GetZone(pos);
            Resetter.Site best = null;
            float bestD = float.MaxValue;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                var site = Resetter.FindSite(new Vector2s((short)(c.x + dx), (short)(c.y + dy)));
                if (site == null) continue;
                if (eligibleOnly && Resetter.Eligible(site.Location, out _) != true) continue;
                if (pos.y > site.Pos.y + Resetter.InteriorAbove)
                {
                    if (Resetter.Contains(site, pos, 2f)) return site;
                    continue;
                }
                Vector3 s = site.Pos;
                float d = new Vector2(pos.x - s.x, pos.z - s.z).magnitude - Resetter.Radius(site);
                if (d <= NearestRange && d < bestD) { best = site; bestD = d; }
            }
            return best;
        }

        /// <summary>Every generated location whose centre is within the radius, nearest first.</summary>
        private static List<Resetter.Site> Within(Vector3 pos, float radius)
        {
            var found = new List<KeyValuePair<float, Resetter.Site>>();
            float r2 = radius * radius;
            foreach (var kv in ZoneSystem.instance.m_locationInstances)
            {
                if (!kv.Value.m_placed) continue;
                Vector3 p = kv.Value.m_position;
                float d2 = (p.x - pos.x) * (p.x - pos.x) + (p.z - pos.z) * (p.z - pos.z);
                if (d2 > r2) continue;
                var site = Resetter.FindSite(kv.Key);
                if (site != null) found.Add(new KeyValuePair<float, Resetter.Site>(d2, site));
            }
            found.Sort((a, b) => a.Key.CompareTo(b.Key));
            return found.ConvertAll(kv => kv.Value);
        }
    }
}
