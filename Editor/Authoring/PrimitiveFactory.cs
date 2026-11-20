using Maicon.SceneAnalyzer.Editor.Core;
using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public static class PrimitiveFactory
    {
        public static OpResult Create(JObject args)
        {
            args ??= new JObject();
            var kindText = ((string)args["kind"] ?? "Cube").Trim();
            if (!Enum.TryParse(kindText, true, out PrimitiveType kind) || !Enum.IsDefined(typeof(PrimitiveType), kind))
                return OpResult.Fail($"INVALID_ARG: unknown primitive kind '{kindText}'");
            if (!SceneAuthoringSupport.TryResolveParent(args["parent"], out var parent, out var error))
                return OpResult.Fail(error);
            if (!TryOptionalVector(args, "position", Vector3.zero, out var position, out error)
                || !TryOptionalRotation(args, "rotation", out var rotation, out error)
                || !TryOptionalVector(args, "scale", Vector3.one, out var scale, out error))
                return OpResult.Fail(error);

            using (var mutation = new MutationScope($"Create {kind}"))
            {
                var gameObject = GameObject.CreatePrimitive(kind);
                mutation.RegisterCreated(gameObject, $"Create {kind}");
                var name = ((string)args["name"])?.Trim();
                if (!string.IsNullOrEmpty(name)) gameObject.name = name;
                if (parent != null) mutation.SetParent(gameObject.transform, parent.transform, "Parent");
                gameObject.transform.localPosition = position;
                gameObject.transform.localRotation = rotation;
                gameObject.transform.localScale = scale;
                mutation.Complete();
                return OpResult.Success(gameObject.GetMcpInstanceId(), $"created {kind} '{gameObject.name}'");
            }
        }

        internal static bool TryOptionalVector(
            JObject args, string property, Vector3 fallback, out Vector3 value, out string error)
        {
            value = fallback;
            error = null;
            if (args[property] == null) return true;
            if (JsonHelpers.TryParseFiniteVector3(args[property], out value)) return true;
            error = $"INVALID_ARG: {property} must be exactly three finite numbers";
            return false;
        }

        internal static bool TryOptionalRotation(
            JObject args, string property, out Quaternion value, out string error)
        {
            value = Quaternion.identity;
            error = null;
            if (args[property] == null) return true;
            if (JsonHelpers.TryParseFiniteRotation(args[property], out value)) return true;
            error = $"INVALID_ARG: {property} must be finite Euler [x,y,z] or a non-zero quaternion [x,y,z,w]";
            return false;
        }
    }
}
