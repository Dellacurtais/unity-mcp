using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Core
{
    /// <summary>
    /// Adapts Unity object identity to the existing MCP integer-handle contract.
    /// On EntityId-based Editors, handles are opaque package-local aliases, never
    /// truncated native IDs or hash codes. Reacquire handles after a domain reload;
    /// use ToolTargetRef/globalObjectId for durable references. Main thread only.
    /// </summary>
    public static class UnityObjectIds
    {
        public static T[] FindAll<T>(FindObjectsInactive inactive) where T : UnityEngine.Object
        {
#if UNITY_6000_6_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<T>(inactive);
#else
            return UnityEngine.Object.FindObjectsByType<T>(inactive, FindObjectsSortMode.None);
#endif
        }

        public static UnityEngine.Object[] FindAll(Type type, FindObjectsInactive inactive)
        {
#if UNITY_6000_6_OR_NEWER
            return UnityEngine.Object.FindObjectsByType(type, inactive);
#else
            return UnityEngine.Object.FindObjectsByType(type, inactive, FindObjectsSortMode.None);
#endif
        }

#if UNITY_6000_2_OR_NEWER
        private const string CounterKey = "Maicon.SceneAnalyzer.NextObjectHandle";
        private static readonly Dictionary<EntityId, int> Handles = new Dictionary<EntityId, int>();
        private static readonly Dictionary<int, EntityId> Entities = new Dictionary<int, EntityId>();
#endif

        public static int GetMcpInstanceId(this UnityEngine.Object target)
        {
            if (target == null) return 0;
#if UNITY_6000_2_OR_NEWER
            var entity = target.GetEntityId();
            if (Handles.TryGetValue(entity, out var handle)) return handle;
            // SessionState survives assembly reloads, so expired handles cannot
            // silently identify a new object after a script recompile.
            handle = checked(SessionState.GetInt(CounterKey, 0) + 1);
            SessionState.SetInt(CounterKey, handle);
            Handles.Add(entity, handle);
            Entities.Add(handle, entity);
            return handle;
#else
            return target.GetInstanceID();
#endif
        }

        public static UnityEngine.Object Resolve(int handle)
        {
            if (handle == 0) return null;
#if UNITY_6000_2_OR_NEWER
            return Entities.TryGetValue(handle, out var entity)
                ? EditorUtility.EntityIdToObject(entity)
                : null;
#else
            return EditorUtility.InstanceIDToObject(handle);
#endif
        }

        public static bool HasObjectReference(SerializedProperty property)
        {
            if (property == null) throw new ArgumentNullException(nameof(property));
#if UNITY_6000_2_OR_NEWER
            return property.objectReferenceEntityIdValue != EntityId.None;
#else
            return property.objectReferenceInstanceIDValue != 0;
#endif
        }

        public static bool References(SerializedProperty property, UnityEngine.Object target)
        {
            if (property == null) throw new ArgumentNullException(nameof(property));
#if UNITY_6000_2_OR_NEWER
            return property.objectReferenceEntityIdValue == (target != null ? target.GetEntityId() : EntityId.None);
#else
            return property.objectReferenceInstanceIDValue == (target != null ? target.GetInstanceID() : 0);
#endif
        }
    }
}
