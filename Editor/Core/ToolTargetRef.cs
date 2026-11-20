using Maicon.SceneAnalyzer.Editor.Core;
using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Core
{
    [Serializable]
    public sealed class ToolTargetRef
    {
        // A fresh domain distinguishes live-instance provenance from durable IDs.
        // Unity can reuse GUID/fileID pairs after deleting and recreating a target.
        internal static readonly string CurrentCaptureDomainId = Guid.NewGuid().ToString("N");
        public string Kind;
        public int InstanceId;
        public string CaptureDomainId;
        public string GlobalObjectId;
        public string AssetGuid;
        public long LocalFileId;
        public string AssetPath;
        public string SceneGuid;
        public string ScenePath;
        public int SceneHandle;
        public string HierarchyPath;
        public string ComponentType;
        public int ComponentOrdinal = -1;
        public bool IsPrefabStage;

        public JObject ToJson()
        {
            var json = new JObject
            {
                ["kind"] = Kind,
                ["isPrefabStage"] = IsPrefabStage,
            };
            AddWhenPresent(json, "instanceId", InstanceId != 0 ? (JToken)InstanceId : null);
            AddWhenPresent(json, "captureDomainId", StringToken(CaptureDomainId));
            AddWhenPresent(json, "globalObjectId", StringToken(GlobalObjectId));
            AddWhenPresent(json, "assetGuid", StringToken(AssetGuid));
            AddWhenPresent(json, "localFileId", LocalFileId != 0 ? (JToken)LocalFileId : null);
            AddWhenPresent(json, "assetPath", StringToken(AssetPath));
            AddWhenPresent(json, "sceneGuid", StringToken(SceneGuid));
            AddWhenPresent(json, "scenePath", StringToken(ScenePath));
            AddWhenPresent(json, "sceneHandle", SceneHandle != 0 ? (JToken)SceneHandle : null);
            AddWhenPresent(json, "hierarchyPath", StringToken(HierarchyPath));
            AddWhenPresent(json, "componentType", StringToken(ComponentType));
            AddWhenPresent(json, "componentOrdinal", ComponentOrdinal >= 0 ? (JToken)ComponentOrdinal : null);
            return json;
        }

        public static ToolTargetRef FromJson(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Integer) return new ToolTargetRef { InstanceId = (int)token };
            if (token.Type == JTokenType.String)
            {
                var value = (string)token;
                return value != null && value.StartsWith("GlobalObjectId_V", StringComparison.Ordinal)
                    ? new ToolTargetRef { GlobalObjectId = value }
                    : new ToolTargetRef { AssetPath = value };
            }
            var json = token as JObject;
            if (json == null) return null;
            return new ToolTargetRef
            {
                Kind = (string)json["kind"],
                InstanceId = (int?)json["instanceId"] ?? 0,
                CaptureDomainId = (string)json["captureDomainId"],
                GlobalObjectId = (string)json["globalObjectId"],
                AssetGuid = (string)json["assetGuid"],
                LocalFileId = (long?)json["localFileId"] ?? 0,
                AssetPath = (string)json["assetPath"],
                SceneGuid = (string)json["sceneGuid"],
                ScenePath = (string)json["scenePath"],
                SceneHandle = (int?)json["sceneHandle"] ?? 0,
                HierarchyPath = (string)json["hierarchyPath"],
                ComponentType = (string)json["componentType"],
                ComponentOrdinal = (int?)json["componentOrdinal"] ?? -1,
                IsPrefabStage = (bool?)json["isPrefabStage"] ?? false,
            };
        }

        public static ToolTargetRef Capture(UnityEngine.Object target)
        {
            if (target == null) return null;
            var isPersistentAsset = EditorUtility.IsPersistent(target);
            var result = new ToolTargetRef
            {
                InstanceId = target.GetMcpInstanceId(),
                CaptureDomainId = CurrentCaptureDomainId,
                Kind = isPersistentAsset ? "asset" : target is GameObject ? "gameObject" : target is Component ? "component" : "object",
            };

            try
            {
                var global = UnityEditor.GlobalObjectId.GetGlobalObjectIdSlow(target);
                var serialized = global.ToString();
                if (global.identifierType != 0
                    && global.assetGUID.ToString() != "00000000000000000000000000000000"
                    && UnityEditor.GlobalObjectId.TryParse(serialized, out _))
                    result.GlobalObjectId = serialized;
            }
            catch { }

            var gameObject = target as GameObject ?? (target as Component)?.gameObject;
            if (isPersistentAsset)
            {
                try
                {
                    if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(target, out string guid, out long localId))
                    {
                        result.AssetGuid = guid;
                        result.LocalFileId = localId;
                        result.AssetPath = AssetDatabase.GUIDToAssetPath(guid);
                    }
                }
                catch { }
            }
            else if (gameObject != null)
            {
                var scene = gameObject.scene;
                var prefabStage = PrefabStageUtility.GetPrefabStage(gameObject);
                result.SceneHandle = scene.GetMcpSceneHandle();
                result.IsPrefabStage = prefabStage != null;
                result.ScenePath = prefabStage != null ? prefabStage.assetPath : scene.path;
                result.SceneGuid = string.IsNullOrEmpty(result.ScenePath) ? null : AssetDatabase.AssetPathToGUID(result.ScenePath);
                result.HierarchyPath = BuildHierarchyPath(gameObject.transform);
            }

            if (target is Component component)
            {
                result.ComponentType = component.GetType().AssemblyQualifiedName;
                var sameType = component.gameObject.GetComponents(component.GetType());
                result.ComponentOrdinal = Array.IndexOf(sameType, component);
            }
            return result;
        }

        public static string BuildHierarchyPath(Transform transform)
        {
            if (transform == null) return null;
            var parts = new System.Collections.Generic.Stack<string>();
            for (var current = transform; current != null; current = current.parent) parts.Push(current.name);
            return string.Join("/", parts);
        }

        private static JToken StringToken(string value) => string.IsNullOrEmpty(value) ? null : new JValue(value);

        private static void AddWhenPresent(JObject json, string name, JToken value)
        {
            // TargetRefSchema intentionally models these fields as optional,
            // non-null values. Emitting explicit JSON null made otherwise valid
            // direct tool results fail MCP structured-output validation and
            // caused the external Unity server to be evicted from the live pool.
            if (value != null) json[name] = value;
        }
    }
}
