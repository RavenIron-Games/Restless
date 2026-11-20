using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;

namespace RavenIron.Restless
{
    /// <summary>
    /// The .cfg reloads itself: edit it on disk and the change is live within a few seconds, no restart. Ported from
    /// Tarn's ConfigSync (the file half only: every Restless setting is read on the server, so nothing is sent to players).
    /// The file's write time is checked from Plugin.Update, no watcher thread; a change is re-read with ConfigFile.Reload,
    /// whose SetSerializedValue updates each entry in place (read out of libs\BepInEx.dll, 5.4). Every setting is read
    /// through its ConfigEntry each time it is used, so a new value takes effect on the next check. `restless reload`
    /// does the same on demand.
    /// </summary>
    internal static class ConfigWatch
    {
        private const float FileCheckEvery = 3f;

        private static ConfigFile _file;
        private static readonly List<ConfigEntryBase> _entries = new List<ConfigEntryBase>();
        private static float _sinceFileCheck;
        private static DateTime _fileTime;

        internal static void Init(ConfigFile file, params ConfigEntryBase[] entries)
        {
            _file = file;
            _entries.AddRange(entries);
            _fileTime = FileTime();
        }

        internal static void Tick(float dt)
        {
            if (_file == null) return;
            _sinceFileCheck += dt;
            if (_sinceFileCheck < FileCheckEvery) return;
            _sinceFileCheck = 0f;
            var t = FileTime();
            if (t != _fileTime)
            {
                _fileTime = t;
                ReloadFile("its file changed", quietIfSame: true);
            }
        }

        /// <summary>Re-read the .cfg. Returns a line saying what changed, for the log and the `restless reload` reply.</summary>
        internal static string ReloadFile(string why, bool quietIfSame = false)
        {
            if (_file == null) return "no config file";
            try
            {
                var before = Snapshot();
                _file.Reload();
                _fileTime = FileTime();
                var changed = new List<string>();
                foreach (var e in _entries)
                    if (before[e] != e.GetSerializedValue()) changed.Add($"{e.Definition.Key} {before[e]} -> {e.GetSerializedValue()}");
                string line = changed.Count > 0 ? $"config re-read ({why}): {string.Join(", ", changed.ToArray())}"
                                                : $"config re-read ({why}): nothing changed";
                // A save that changed nothing (a config manager, an editor touching the file) is not worth a log line.
                if (changed.Count > 0 || !quietIfSame) Plugin.Log.LogInfo(line);
                return line;
            }
            catch (Exception ex)
            {
                // A file caught half-written by an editor: the stamp moves again when the save finishes, and we re-read then.
                string line = $"re-reading {_file.ConfigFilePath} failed: {ex.Message}";
                Plugin.Log.LogWarning("config: " + line);
                return line;
            }
        }

        private static Dictionary<ConfigEntryBase, string> Snapshot()
        {
            var d = new Dictionary<ConfigEntryBase, string>();
            foreach (var e in _entries) d[e] = e.GetSerializedValue();
            return d;
        }

        private static DateTime FileTime()
        {
            try { return _file != null && File.Exists(_file.ConfigFilePath) ? File.GetLastWriteTimeUtc(_file.ConfigFilePath) : default; }
            catch { return default; }
        }
    }
}
