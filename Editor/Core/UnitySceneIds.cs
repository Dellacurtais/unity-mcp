using System.Collections.Generic;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Core
{
    /// <summary>Session-local MCP aliases for full-width native scene handles.</summary>
    public static class UnitySceneIds
    {
#if UNITY_6000_5_OR_NEWER
        private const string CounterKey = "Maicon.SceneAnalyzer.NextSceneHandle";
        private static readonly Dictionary<SceneHandle, int> Handles = new Dictionary<SceneHandle, int>();
#endif

        public static int GetMcpSceneHandle(this Scene scene)
        {
#if UNITY_6000_5_OR_NEWER
            var native = scene.handle;
            if (native == SceneHandle.None) return 0;
            if (Handles.TryGetValue(native, out var handle)) return handle;
            handle = checked(SessionState.GetInt(CounterKey, 0) + 1);
            SessionState.SetInt(CounterKey, handle);
            Handles.Add(native, handle);
            return handle;
#else
            return scene.handle;
#endif
        }
    }
}
