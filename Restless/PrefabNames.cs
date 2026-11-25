using System;
using System.Reflection;
using SoftReferenceableAssets;
using UnityEngine;

namespace RavenIron.Restless
{
    /// <summary>
    /// A location's prefab name, whatever the running Valheim calls it. Valheim 1.0.30 (network version 42)
    /// renamed SoftReference&lt;T&gt;.Name to .name; code compiled against the old property threw
    /// MissingMethodException on every check (Storm10, 2026-11-25). The getter is found by reflection once,
    /// under either spelling, so one build runs on both sides of the rename.
    /// </summary>
    internal static class PrefabNames
    {
        private static MethodInfo _getter;
        private static bool _warned;

        public static string Of(SoftReference<GameObject> prefab)
        {
            if (_getter == null)
            {
                var t = typeof(SoftReference<GameObject>);
                _getter = (t.GetProperty("name", BindingFlags.Instance | BindingFlags.Public)
                           ?? t.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public))?.GetGetMethod();
                if (_getter == null)
                {
                    // Keep trying on later calls; say once that the API moved.
                    if (!_warned) { _warned = true; Plugin.Log.LogError("SoftReference<GameObject> has neither a 'name' nor a 'Name' property; Valheim's API moved. Locations cannot be named."); }
                    return "";
                }
            }
            return _getter.Invoke(prefab, null) as string ?? "";
        }
    }
}
