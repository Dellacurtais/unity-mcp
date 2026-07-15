using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public sealed class AssetEditingScope : IDisposable
    {
        private readonly HashSet<string> _paths = new HashSet<string>(StringComparer.Ordinal);
        private bool _stopped;

        public AssetEditingScope(IEnumerable<string> paths = null)
        {
            if (paths != null)
                foreach (var path in paths) if (!string.IsNullOrEmpty(path)) _paths.Add(path);
            foreach (var path in _paths)
            {
                if (!AssetDatabase.IsOpenForEdit(path, out var message, StatusQueryOptions.ForceUpdate))
                    throw new InvalidOperationException($"Asset '{path}' is not editable: {message}");
            }
            AssetDatabase.StartAssetEditing();
        }

        public void Touch(string path)
        {
            if (!string.IsNullOrEmpty(path)) _paths.Add(path);
        }

        public JObject ToJson() => new JObject { ["assets"] = new JArray(_paths), ["editing"] = !_stopped };

        public void Dispose()
        {
            if (_stopped) return;
            _stopped = true;
            AssetDatabase.StopAssetEditing();
        }
    }
}
