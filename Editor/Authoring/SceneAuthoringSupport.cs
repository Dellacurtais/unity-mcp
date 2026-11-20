using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public static class SceneAuthoringSupport
    {
        public static JObject TargetResultSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["target"] = SchemaHelpers.TargetRefSchema,
                ["data"] = new JObject(),
            },
            ["required"] = new JArray { "instanceId", "target" },
            ["additionalProperties"] = false,
        };

        public static JObject DeletedResultSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["name"] = new JObject { ["type"] = "string" },
                ["deleted"] = new JObject { ["type"] = "boolean" },
                ["descendantCount"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
            },
            ["required"] = new JArray { "instanceId", "name", "deleted", "descendantCount" },
            ["additionalProperties"] = false,
        };

        public static JObject TargetResult(int instanceId, JToken data = null)
        {
            var target = UnityObjectIds.Resolve(instanceId);
            var payload = new JObject
            {
                ["instanceId"] = instanceId,
                ["target"] = ToolTargetRef.Capture(target)?.ToJson(),
            };
            if (data != null) payload["data"] = data;
            return payload;
        }

        public static bool TryResolveEditableGameObject(int instanceId, out GameObject gameObject, out string error)
        {
            gameObject = null;
            error = null;
            if (instanceId == 0)
            {
                error = "INVALID_ARG: instanceId must resolve to a live GameObject";
                return false;
            }

            gameObject = UnityObjectIds.Resolve(instanceId) as GameObject;
            if (gameObject == null)
            {
                error = $"REF_BROKEN: GameObject not found: instanceId={instanceId}";
                return false;
            }

            if (PrefabStageUtility.GetPrefabStage(gameObject) != null) return true;
            if (EditorUtility.IsPersistent(gameObject) || PrefabUtility.IsPartOfPrefabAsset(gameObject))
            {
                error = "PERSISTENT_PREFAB_ASSET: prefab assets are not scene-edit targets; instantiate the prefab or open it in Prefab Mode";
                gameObject = null;
                return false;
            }

            if (!gameObject.scene.IsValid() || !gameObject.scene.isLoaded)
            {
                error = "INVALID_SCENE_TARGET: target must belong to a loaded Scene or the current Prefab Stage";
                gameObject = null;
                return false;
            }
            return true;
        }

        public static bool TryResolveParent(JToken token, out GameObject parent, out string error)
        {
            parent = null;
            error = null;
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token.Type != JTokenType.Integer)
            {
                error = "INVALID_ARG: parent must be a GameObject instanceId";
                return false;
            }

            var instanceId = (int)token;
            if (instanceId == 0) return true;
            return TryResolveEditableGameObject(instanceId, out parent, out error);
        }

        public static bool ValidateParent(GameObject child, GameObject parent, out string error)
        {
            error = null;
            if (child == null || parent == null) return true;
            if (ReferenceEquals(child, parent) || parent.transform.IsChildOf(child.transform))
            {
                error = "INVALID_HIERARCHY: a GameObject cannot be parented to itself or one of its descendants";
                return false;
            }
            return true;
        }

        public static void CompleteMutation(params UnityEngine.Object[] targets)
        {
            var seen = new HashSet<int>();
            foreach (var target in targets ?? Array.Empty<UnityEngine.Object>())
            {
                if (target == null || !seen.Add(target.GetMcpInstanceId())) continue;
                var gameObject = target as GameObject ?? (target as Component)?.gameObject;
                if (gameObject != null && gameObject.scene.IsValid())
                    EditorSceneManager.MarkSceneDirty(gameObject.scene);
                if (PrefabUtility.IsPartOfPrefabInstance(target) && !PrefabUtility.IsPartOfPrefabAsset(target))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(target);
            }
        }

        public static int DescendantCount(GameObject gameObject)
            => gameObject == null ? 0 : Math.Max(0, gameObject.GetComponentsInChildren<Transform>(true).Length - 1);
    }
}
