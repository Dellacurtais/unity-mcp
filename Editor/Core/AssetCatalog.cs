using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Core
{
    /// In-memory index of project assets by type. Built lazily and kept warm by an
    /// AssetPostprocessor (see AssetCatalogPostprocessor). Discovery tools read
    /// from this without scanning disk on every call.
    public sealed class AssetCatalog
    {
        public sealed class AssetEntry
        {
            public string Guid;
            public string Path;
            public string Name;
            public string TypeName;
            public string[] Labels;
        }

        private readonly Dictionary<string, AssetEntry> _byGuid = new();
        private readonly Dictionary<string, List<string>> _byType = new(StringComparer.OrdinalIgnoreCase);
        private bool _built;

        public int Count => _byGuid.Count;
        public bool IsBuilt => _built;

        public void Invalidate() => _built = false;

        public void EnsureBuilt()
        {
            if (_built) return;
            _byGuid.Clear();
            _byType.Clear();

            // Indexed types we surface in list_assets. Add more as needed.
            // Order matters: the dedup check `_byGuid.ContainsKey(g)` keeps the
            // first entry, so SPECIFIC types must come BEFORE generic
            // ScriptableObject — otherwise VolumeProfile / TimelineAsset would
            // be tagged as ScriptableObject and fail typed filters.
            var types = new[]
            {
                "Prefab", "Material", "Texture", "Texture2D", "Mesh",
                "AudioClip", "AnimationClip", "Shader", "Sprite",
                "AnimatorController", "AudioMixer", "TimelineAsset",
                "VolumeProfile", "PhysicMaterial", "AvatarMask",
                "ScriptableObject",
            };
            foreach (var t in types)
            {
                var guids = AssetDatabase.FindAssets($"t:{t}");
                foreach (var g in guids)
                {
                    if (_byGuid.ContainsKey(g)) continue;
                    var path = AssetDatabase.GUIDToAssetPath(g);
                    if (string.IsNullOrEmpty(path)) continue;
                    var entry = new AssetEntry
                    {
                        Guid = g,
                        Path = path,
                        Name = Path.GetFileNameWithoutExtension(path),
                        TypeName = t,
                        Labels = AssetDatabase.GetLabels(new GUID(g)),
                    };
                    _byGuid[g] = entry;
                    if (!_byType.TryGetValue(t, out var list))
                    {
                        list = new List<string>();
                        _byType[t] = list;
                    }
                    list.Add(g);
                }
            }
            _built = true;
        }

        public AssetEntry GetByGuid(string guid) => _byGuid.TryGetValue(guid, out var v) ? v : null;

        public AssetEntry GetByPath(string path)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);
            return string.IsNullOrEmpty(guid) ? null : GetByGuid(guid);
        }

        public IEnumerable<AssetEntry> ListByType(string typeName)
        {
            EnsureBuilt();
            if (!_byType.TryGetValue(typeName, out var list)) yield break;
            foreach (var g in list)
            {
                if (_byGuid.TryGetValue(g, out var e)) yield return e;
            }
        }

        public IEnumerable<AssetEntry> Search(string nameContains, string typeName, string folder)
        {
            EnsureBuilt();
            foreach (var e in _byGuid.Values)
            {
                if (!string.IsNullOrEmpty(typeName) && !string.Equals(e.TypeName, typeName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrEmpty(folder) && !e.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrEmpty(nameContains) && e.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                yield return e;
            }
        }
    }

    internal sealed class AssetCatalogPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            // Any change invalidates the catalog; it'll rebuild on next access.
            // Cheap: catalog rebuild is O(N) over AssetDatabase.FindAssets calls.
            Maicon.SceneAnalyzer.Editor.Bootstrap.AssetCatalog?.Invalidate();
        }
    }
}
