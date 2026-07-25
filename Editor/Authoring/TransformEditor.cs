using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public static class TransformEditor
    {
        public static OpResult SetTransform(JObject args)
        {
            args ??= new JObject();
            var instanceId = (int?)args["instanceId"] ?? 0;
            if (!SceneAuthoringSupport.TryResolveEditableGameObject(instanceId, out var gameObject, out var error))
                return OpResult.Fail(error);

            var space = ((string)args["space"] ?? "World").Trim();
            var world = string.Equals(space, "World", System.StringComparison.OrdinalIgnoreCase);
            if (!world && !string.Equals(space, "Local", System.StringComparison.OrdinalIgnoreCase))
                return OpResult.Fail("INVALID_ARG: space must be World or Local");

            var hasPosition = args["position"] != null;
            var hasRotation = args["rotation"] != null;
            var hasScale = args["scale"] != null;
            if (!hasPosition && !hasRotation && !hasScale)
                return OpResult.Fail("INVALID_ARG: provide position, rotation, or scale");
            if (!PrimitiveFactory.TryOptionalVector(args, "position", Vector3.zero, out var position, out error)
                || !PrimitiveFactory.TryOptionalRotation(args, "rotation", out var rotation, out error)
                || !PrimitiveFactory.TryOptionalVector(args, "scale", Vector3.one, out var scale, out error))
                return OpResult.Fail(error);

            using (var mutation = new MutationScope("Set Transform"))
            {
                mutation.Record(gameObject.transform, "Set Transform");
                if (hasPosition)
                {
                    if (world) gameObject.transform.position = position;
                    else gameObject.transform.localPosition = position;
                }
                if (hasRotation)
                {
                    if (world) gameObject.transform.rotation = rotation;
                    else gameObject.transform.localRotation = rotation;
                }
                if (hasScale) gameObject.transform.localScale = scale;
                mutation.Complete();
            }
            return OpResult.Success(instanceId, "transform updated");
        }

        public static OpResult Parent(JObject args)
        {
            args ??= new JObject();
            var instanceId = (int?)args["instanceId"] ?? 0;
            if (!SceneAuthoringSupport.TryResolveEditableGameObject(instanceId, out var gameObject, out var error))
                return OpResult.Fail(error);
            if (!SceneAuthoringSupport.TryResolveParent(args["newParentId"], out var parent, out error))
                return OpResult.Fail(error);
            if (!SceneAuthoringSupport.ValidateParent(gameObject, parent, out error))
                return OpResult.Fail(error);
            if (parent != null && parent.scene != gameObject.scene)
                return OpResult.Fail("INVALID_HIERARCHY: child and parent must belong to the same Scene or Prefab Stage");

            var worldPositionStays = (bool?)args["worldPositionStays"] ?? true;
            var localPosition = gameObject.transform.localPosition;
            var localRotation = gameObject.transform.localRotation;
            var localScale = gameObject.transform.localScale;
            using (var mutation = new MutationScope("Reparent"))
            {
                mutation.Record(gameObject.transform, "Reparent");
                mutation.SetParent(gameObject.transform, parent != null ? parent.transform : null, "Reparent");
                if (!worldPositionStays)
                {
                    gameObject.transform.localPosition = localPosition;
                    gameObject.transform.localRotation = localRotation;
                    gameObject.transform.localScale = localScale;
                }
                mutation.Complete();
            }
            return OpResult.Success(instanceId, "reparented");
        }
    }
}
