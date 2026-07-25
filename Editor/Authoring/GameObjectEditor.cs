using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public static class GameObjectEditor
    {
        public static OpResult Duplicate(JObject args)
        {
            args ??= new JObject();
            var instanceId = (int?)args["instanceId"] ?? 0;
            if (!SceneAuthoringSupport.TryResolveEditableGameObject(instanceId, out var source, out var error))
                return OpResult.Fail(error);

            GameObject parent;
            if (args["parent"] != null)
            {
                if (!SceneAuthoringSupport.TryResolveParent(args["parent"], out parent, out error))
                    return OpResult.Fail(error);
            }
            else parent = source.transform.parent != null ? source.transform.parent.gameObject : null;
            if (parent != null && parent.scene != source.scene)
                return OpResult.Fail("INVALID_HIERARCHY: duplicate and parent must belong to the same Scene or Prefab Stage");

            var name = args["name"] != null ? ((string)args["name"] ?? string.Empty).Trim() : source.name + " (Copy)";
            if (string.IsNullOrEmpty(name)) return OpResult.Fail("INVALID_ARG: name must not be blank");

            var position = source.transform.localPosition;
            if (args["position"] != null)
            {
                if (!JsonHelpers.TryParseFiniteVector3(args["position"], out position))
                    return OpResult.Fail("INVALID_ARG: position must be exactly three finite numbers");
            }
            else if (args["offset"] != null)
            {
                if (!JsonHelpers.TryParseFiniteVector3(args["offset"], out var offset))
                    return OpResult.Fail("INVALID_ARG: offset must be exactly three finite numbers");
                position += offset;
            }

            var rotation = source.transform.localRotation;
            if (args["rotation"] != null && !JsonHelpers.TryParseFiniteRotation(args["rotation"], out rotation))
                return OpResult.Fail("INVALID_ARG: rotation must be finite Euler [x,y,z] or a non-zero quaternion [x,y,z,w]");
            var scale = source.transform.localScale;
            if (args["scale"] != null && !JsonHelpers.TryParseFiniteVector3(args["scale"], out scale))
                return OpResult.Fail("INVALID_ARG: scale must be exactly three finite numbers");

            using (var mutation = new MutationScope("Duplicate GameObject"))
            {
                var clone = Object.Instantiate(source);
                if (clone == null) return OpResult.Fail("DUPLICATE_FAILED: Unity returned no clone");
                mutation.RegisterCreated(clone, "Duplicate GameObject");
                clone.name = name;
                if (parent != null) mutation.SetParent(clone.transform, parent.transform, "Parent Duplicate");
                mutation.Record(clone.transform, "Set Duplicate Pose");
                clone.transform.localPosition = position;
                clone.transform.localRotation = rotation;
                clone.transform.localScale = scale;
                mutation.Complete();
                return OpResult.Success(clone.GetInstanceID(), $"duplicated '{source.name}' → '{clone.name}'");
            }
        }

        public static OpResult LookAt(JObject args)
        {
            args ??= new JObject();
            var instanceId = (int?)args["instanceId"] ?? 0;
            if (!SceneAuthoringSupport.TryResolveEditableGameObject(instanceId, out var gameObject, out var error))
                return OpResult.Fail(error);

            Vector3 worldTarget;
            if (args["targetInstanceId"] != null)
            {
                var targetId = (int?)args["targetInstanceId"] ?? 0;
                if (!SceneAuthoringSupport.TryResolveEditableGameObject(targetId, out var target, out error))
                    return OpResult.Fail($"TARGET_{error}");
                worldTarget = target.transform.position;
            }
            else if (args["targetPosition"] != null)
            {
                if (!JsonHelpers.TryParseFiniteVector3(args["targetPosition"], out worldTarget))
                    return OpResult.Fail("INVALID_ARG: targetPosition must be exactly three finite numbers");
            }
            else
            {
                return OpResult.Fail("INVALID_ARG: provide targetInstanceId or targetPosition");
            }

            var worldUp = Vector3.up;
            if (args["worldUp"] != null)
            {
                if (!JsonHelpers.TryParseFiniteVector3(args["worldUp"], out worldUp) || worldUp.sqrMagnitude <= 1e-12f)
                    return OpResult.Fail("INVALID_ARG: worldUp must be a finite non-zero Vector3");
            }
            if ((worldTarget - gameObject.transform.position).sqrMagnitude <= 1e-12f)
                return OpResult.Fail("INVALID_ARG: look-at target must differ from the GameObject position");

            using (var mutation = new MutationScope("Look At"))
            {
                mutation.Record(gameObject.transform, "Look At");
                gameObject.transform.LookAt(worldTarget, worldUp);
                if ((bool?)args["flipForCamera"] == true && gameObject.GetComponent<Camera>() == null)
                    gameObject.transform.Rotate(0f, 180f, 0f, Space.Self);
                mutation.Complete();
            }
            return OpResult.Success(instanceId, $"looking at {worldTarget}");
        }

        public static OpResult SetActive(JObject args)
        {
            args ??= new JObject();
            var instanceId = (int?)args["instanceId"] ?? 0;
            if (!SceneAuthoringSupport.TryResolveEditableGameObject(instanceId, out var gameObject, out var error))
                return OpResult.Fail(error);
            var active = (bool?)args["active"] ?? true;
            if (gameObject.activeSelf == active) return OpResult.Success(instanceId, $"active already {active}");
            using (var mutation = new MutationScope(active ? "Activate GameObject" : "Deactivate GameObject"))
            {
                mutation.Record(gameObject, active ? "Activate GameObject" : "Deactivate GameObject");
                gameObject.SetActive(active);
                mutation.Complete();
            }
            return OpResult.Success(instanceId, $"set active = {active}");
        }

        public static OpResult Rename(JObject args)
        {
            args ??= new JObject();
            var instanceId = (int?)args["instanceId"] ?? 0;
            if (!SceneAuthoringSupport.TryResolveEditableGameObject(instanceId, out var gameObject, out var error))
                return OpResult.Fail(error);
            var name = ((string)args["name"] ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name)) return OpResult.Fail("INVALID_ARG: name is required and must not be blank");
            if (gameObject.name == name) return OpResult.Success(instanceId, $"name already '{name}'");
            var oldName = gameObject.name;
            using (var mutation = new MutationScope("Rename GameObject"))
            {
                mutation.Record(gameObject, "Rename GameObject");
                gameObject.name = name;
                mutation.Complete();
            }
            return OpResult.Success(instanceId, $"renamed '{oldName}' → '{name}'");
        }

        public static OpResult SetTag(JObject args)
        {
            args ??= new JObject();
            var instanceId = (int?)args["instanceId"] ?? 0;
            if (!SceneAuthoringSupport.TryResolveEditableGameObject(instanceId, out var gameObject, out var error))
                return OpResult.Fail(error);
            var tag = ((string)args["tag"] ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(tag)) return OpResult.Fail("INVALID_ARG: tag is required and must not be blank");
            if (Array.IndexOf(UnityEditorInternal.InternalEditorUtility.tags, tag) < 0)
                return OpResult.Fail($"INVALID_ARG: tag '{tag}' is not registered in Project Settings > Tags and Layers");
            if (gameObject.CompareTag(tag)) return OpResult.Success(instanceId, $"tag already '{tag}'");

            using (var mutation = new MutationScope("Set Tag"))
            {
                mutation.Record(gameObject, "Set Tag");
                try { gameObject.tag = tag; }
                catch (UnityException exception) { return OpResult.Fail($"INVALID_ARG: {exception.Message}"); }
                mutation.Complete();
            }
            return OpResult.Success(instanceId, $"tag = '{tag}'");
        }

        public static OpResult SetLayer(JObject args)
        {
            args ??= new JObject();
            var instanceId = (int?)args["instanceId"] ?? 0;
            if (!SceneAuthoringSupport.TryResolveEditableGameObject(instanceId, out var gameObject, out var error))
                return OpResult.Fail(error);

            var rawLayer = args["layer"];
            if (rawLayer == null) return OpResult.Fail("INVALID_ARG: layer is required (int 0..31 or registered layer name)");
            int layer;
            if (rawLayer.Type == JTokenType.Integer) layer = (int)rawLayer;
            else if (rawLayer.Type == JTokenType.String)
            {
                var layerName = ((string)rawLayer ?? string.Empty).Trim();
                layer = LayerMask.NameToLayer(layerName);
                if (layer < 0) return OpResult.Fail($"INVALID_ARG: layer name '{layerName}' is not registered");
            }
            else return OpResult.Fail("INVALID_ARG: layer must be an integer or string");
            if (layer < 0 || layer > 31) return OpResult.Fail($"INVALID_ARG: layer index out of range: {layer}");

            var recursive = (bool?)args["recursive"] ?? false;
            var transforms = recursive ? gameObject.GetComponentsInChildren<Transform>(true) : new[] { gameObject.transform };
            using (var mutation = new MutationScope("Set Layer"))
            {
                foreach (var transform in transforms)
                {
                    if (transform.gameObject.layer == layer) continue;
                    mutation.Record(transform.gameObject, "Set Layer");
                    transform.gameObject.layer = layer;
                }
                mutation.Complete();
            }
            return OpResult.Success(instanceId, $"layer = {layer}{(recursive ? " (recursive)" : string.Empty)}");
        }

        public static OpResult Delete(JObject args)
        {
            args ??= new JObject();
            var instanceId = (int?)args["instanceId"] ?? 0;
            if (!SceneAuthoringSupport.TryResolveEditableGameObject(instanceId, out var gameObject, out var error))
                return OpResult.Fail(error);
            var descendantCount = SceneAuthoringSupport.DescendantCount(gameObject);
            var force = (bool?)args["force"] ?? false;
            if (descendantCount > 8 && !force)
                return OpResult.Fail($"REFUSED: refusing to delete '{gameObject.name}' with {descendantCount} descendants without force=true");

            var name = gameObject.name;
            using (var mutation = new MutationScope("Delete GameObject"))
            {
                mutation.Destroy(gameObject);
                mutation.Complete();
            }
            var result = OpResult.Success(instanceId, $"deleted '{name}'");
            result.Data = new JObject { ["name"] = name, ["deleted"] = true, ["descendantCount"] = descendantCount };
            return result;
        }
    }
}
