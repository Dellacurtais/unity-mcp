using Maicon.SceneAnalyzer.Editor.Core;
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public static class CameraFactory
    {
        private sealed class Values
        {
            public float? FieldOfView;
            public float? NearClipPlane;
            public float? FarClipPlane;
            public float? Depth;
            public bool? Orthographic;
            public float? OrthographicSize;
            public CameraClearFlags? ClearFlags;
            public Color? BackgroundColor;
            public int? CullingMask;
        }

        public static OpResult Create(JObject args)
        {
            args ??= new JObject();
            if (!SceneAuthoringSupport.TryResolveParent(args["parent"], out var parent, out var error))
                return OpResult.Fail(error);
            if (!PrimitiveFactory.TryOptionalVector(args, "position", Vector3.zero, out var position, out error)
                || !PrimitiveFactory.TryOptionalRotation(args, "rotation", out var rotation, out error)
                || !TryReadValues(args, 60f, 0.3f, 1000f, out var values, out error))
                return OpResult.Fail(error);

            var name = ((string)args["name"] ?? "Camera").Trim();
            if (string.IsNullOrEmpty(name)) return OpResult.Fail("INVALID_ARG: name must not be blank");
            var isMain = (bool?)args["isMain"] ?? false;

            using (var mutation = new MutationScope("Create Camera"))
            {
                var gameObject = new GameObject(name);
                mutation.RegisterCreated(gameObject, "Create Camera");
                var camera = gameObject.AddComponent<Camera>();
                ApplyValues(camera, values);
                if (parent != null) mutation.SetParent(gameObject.transform, parent.transform, "Parent");
                gameObject.transform.localPosition = position;
                gameObject.transform.localRotation = rotation;

                if (isMain)
                {
                    foreach (var existing in UnityObjectIds.FindAll<GameObject>(
                                 FindObjectsInactive.Include))
                    {
                        if (existing == gameObject || existing.scene != gameObject.scene || !existing.CompareTag("MainCamera"))
                            continue;
                        mutation.Record(existing, "Replace Main Camera");
                        existing.tag = "Untagged";
                    }
                    gameObject.tag = "MainCamera";
                }

                mutation.Touch(camera);
                mutation.Touch(gameObject.transform);
                mutation.Complete();
                return OpResult.Success(gameObject.GetMcpInstanceId(), $"created camera '{name}'");
            }
        }

        public static OpResult SetProps(JObject args)
        {
            args ??= new JObject();
            var instanceId = (int?)args["instanceId"] ?? 0;
            if (!SceneAuthoringSupport.TryResolveEditableGameObject(instanceId, out var gameObject, out var error))
                return OpResult.Fail(error);
            var camera = gameObject.GetComponent<Camera>();
            if (camera == null) return OpResult.Fail("MISSING_COMPONENT: GameObject has no Camera component");
            if (!TryReadValues(args, camera.fieldOfView, camera.nearClipPlane, camera.farClipPlane, out var values, out error))
                return OpResult.Fail(error);

            using (var mutation = new MutationScope("Set Camera Props"))
            {
                mutation.Record(camera, "Set Camera Props");
                ApplyValues(camera, values);
                mutation.Complete();
            }
            return OpResult.Success(instanceId, "camera props updated");
        }

        private static bool TryReadValues(
            JObject args, float currentFieldOfView, float currentNear, float currentFar,
            out Values values, out string error)
        {
            values = new Values();
            error = null;
            if (!TryOptionalFinite(args, "fieldOfView", out values.FieldOfView, out error)
                || !TryOptionalFinite(args, "nearClipPlane", out values.NearClipPlane, out error)
                || !TryOptionalFinite(args, "farClipPlane", out values.FarClipPlane, out error)
                || !TryOptionalFinite(args, "depth", out values.Depth, out error)
                || !TryOptionalFinite(args, "orthographicSize", out values.OrthographicSize, out error))
                return false;

            var fieldOfView = values.FieldOfView ?? currentFieldOfView;
            var near = values.NearClipPlane ?? currentNear;
            var far = values.FarClipPlane ?? currentFar;
            if (fieldOfView <= 0f || fieldOfView >= 180f)
            {
                error = "INVALID_ARG: fieldOfView must be finite and between 0 and 180 degrees";
                return false;
            }
            if (near <= 0f || far <= near)
            {
                error = "INVALID_ARG: clipping planes require 0 < nearClipPlane < farClipPlane";
                return false;
            }
            if (values.OrthographicSize.HasValue && values.OrthographicSize.Value <= 0f)
            {
                error = "INVALID_ARG: orthographicSize must be greater than zero";
                return false;
            }

            values.Orthographic = (bool?)args["orthographic"];
            values.CullingMask = (int?)args["cullingMask"];
            if (args["clearFlags"] != null)
            {
                var text = ((string)args["clearFlags"] ?? string.Empty).Trim();
                if (!Enum.TryParse(text, true, out CameraClearFlags flags) || !Enum.IsDefined(typeof(CameraClearFlags), flags))
                {
                    error = $"INVALID_ARG: unknown CameraClearFlags value '{text}'";
                    return false;
                }
                values.ClearFlags = flags;
            }
            if (args["backgroundColor"] != null)
            {
                if (!JsonHelpers.TryParseFiniteColor(args["backgroundColor"], out var color))
                {
                    error = "INVALID_ARG: backgroundColor must contain finite RGB[A] values or a valid HTML color";
                    return false;
                }
                values.BackgroundColor = color;
            }
            return true;
        }

        private static bool TryOptionalFinite(JObject args, string property, out float? value, out string error)
        {
            value = null;
            error = null;
            if (args[property] == null) return true;
            if (!JsonHelpers.TryFiniteNumber(args[property], out var number))
            {
                error = $"INVALID_ARG: {property} must be a finite number";
                return false;
            }
            value = number;
            return true;
        }

        private static void ApplyValues(Camera camera, Values values)
        {
            if (values.FieldOfView.HasValue) camera.fieldOfView = values.FieldOfView.Value;
            if (values.NearClipPlane.HasValue) camera.nearClipPlane = values.NearClipPlane.Value;
            if (values.FarClipPlane.HasValue) camera.farClipPlane = values.FarClipPlane.Value;
            if (values.Depth.HasValue) camera.depth = values.Depth.Value;
            if (values.Orthographic.HasValue) camera.orthographic = values.Orthographic.Value;
            if (values.OrthographicSize.HasValue) camera.orthographicSize = values.OrthographicSize.Value;
            if (values.ClearFlags.HasValue) camera.clearFlags = values.ClearFlags.Value;
            if (values.BackgroundColor.HasValue) camera.backgroundColor = values.BackgroundColor.Value;
            if (values.CullingMask.HasValue) camera.cullingMask = values.CullingMask.Value;
        }
    }
}
