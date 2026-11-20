using System;
using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public static class LightFactory
    {
        public static readonly string[] SupportedTypeNames =
        {
            "Spot", "Directional", "Point", "Rectangle", "Disc", "Pyramid", "Box", "Tube",
        };

        public static OpResult Create(JObject args)
        {
            if (!TryReadType((string)args["type"] ?? "Directional", out var type, out var typeError))
                return OpResult.Fail(typeError);
            if (!TryReadFloat(args["intensity"], 1f, "intensity", out var intensity, out var intensityError) || intensity < 0f)
                return OpResult.Fail(intensityError ?? "intensity must be non-negative");
            if (!TryReadFloat(args["range"], 10f, "range", out var range, out var rangeError) || range < 0f)
                return OpResult.Fail(rangeError ?? "range must be non-negative");
            if (!TryReadColor(args["color"], out var color, out var colorError))
                return OpResult.Fail(colorError);

            var shadowsName = (string)args["shadows"] ?? "Soft";
            if (!Enum.TryParse(shadowsName, true, out LightShadows shadows) || !Enum.IsDefined(typeof(LightShadows), shadows))
                return OpResult.Fail($"unknown light shadows '{shadowsName}'");

            var hasBakeMode = args["mode"] != null;
            var bakeMode = default(LightmapBakeType);
            if (hasBakeMode)
            {
                var modeName = (string)args["mode"];
                if (!Enum.TryParse(modeName, true, out bakeMode) || !Enum.IsDefined(typeof(LightmapBakeType), bakeMode))
                    return OpResult.Fail($"unknown light mode '{modeName}'");
            }

            var parentToken = args["parent"];
            var parent = JsonHelpers.ResolveParent(parentToken);
            if (parentToken != null && parentToken.Type != JTokenType.Null && parent == null)
                return OpResult.Fail("parent must reference an existing GameObject instanceId");
            if (!TryReadVector3(args["position"], out var position, out var positionError))
                return OpResult.Fail(positionError);
            if (!TryReadRotation(args["rotation"], out var rotation, out var rotationError))
                return OpResult.Fail(rotationError);

            var supportsSpotAngles = type == LightType.Spot || type == LightType.Pyramid;
            var hasSpotAngle = args["spotAngle"] != null;
            var hasInnerSpotAngle = args["innerSpotAngle"] != null;
            if ((hasSpotAngle || hasInnerSpotAngle) && !supportsSpotAngles)
                return OpResult.Fail($"spotAngle and innerSpotAngle do not apply to {type} lights");
            if (!TryReadFloat(args["spotAngle"], 30f, "spotAngle", out var spotAngle, out var spotError) || spotAngle < 1f || spotAngle > 179f)
                return OpResult.Fail(spotError ?? "spotAngle must be between 1 and 179 degrees");
            if (!TryReadFloat(args["innerSpotAngle"], 0f, "innerSpotAngle", out var innerSpotAngle, out var innerSpotError) || innerSpotAngle < 0f || innerSpotAngle > spotAngle)
                return OpResult.Fail(innerSpotError ?? "innerSpotAngle must be between 0 and spotAngle");

            var supportsAreaSize = type == LightType.Rectangle || type == LightType.Disc || type == LightType.Tube;
            var hasAreaSize = args["areaSize"] != null;
            if (hasAreaSize && !supportsAreaSize)
                return OpResult.Fail($"areaSize does not apply to {type} lights");
            if (!TryReadVector2(args["areaSize"], Vector2.one, "areaSize", out var areaSize, out var areaSizeError) || areaSize.x <= 0f || areaSize.y <= 0f)
                return OpResult.Fail(areaSizeError ?? "areaSize components must be greater than zero");

            var requestedName = ((string)args["name"])?.Trim();
            var name = string.IsNullOrEmpty(requestedName) ? $"{type} Light" : requestedName;
            GameObject gameObject = null;
            try
            {
                gameObject = new GameObject(name);
                var light = gameObject.AddComponent<Light>();
                light.type = type;
                light.intensity = intensity;
                light.range = range;
                light.color = color;
                light.shadows = shadows;
                if (hasBakeMode) light.lightmapBakeType = bakeMode;
                if (hasSpotAngle) light.spotAngle = spotAngle;
                if (hasInnerSpotAngle) light.innerSpotAngle = innerSpotAngle;
                if (hasAreaSize) light.areaSize = areaSize;

                if (parent != null) gameObject.transform.SetParent(parent.transform, false);
                gameObject.transform.localPosition = position;
                gameObject.transform.localRotation = rotation;
                Undo.RegisterCreatedObjectUndo(gameObject, $"Create {type} Light");
                EditorSceneManager.MarkSceneDirty(gameObject.scene);

                return new OpResult
                {
                    Ok = true,
                    InstanceID = gameObject.GetMcpInstanceId(),
                    Summary = $"created {type} light intensity={light.intensity} range={light.range}",
                    Data = new JObject
                    {
                        ["instanceId"] = gameObject.GetMcpInstanceId(),
                        ["lightInstanceId"] = light.GetMcpInstanceId(),
                        ["target"] = ToolTargetRef.Capture(gameObject)?.ToJson(),
                        ["lightTarget"] = ToolTargetRef.Capture(light)?.ToJson(),
                        ["name"] = gameObject.name,
                        ["type"] = light.type.ToString(),
                        ["intensity"] = light.intensity,
                        ["range"] = light.range,
                        ["color"] = ColorJson(light.color),
                        ["shadows"] = light.shadows.ToString(),
                        ["mode"] = light.lightmapBakeType.ToString(),
                        ["spotAngle"] = supportsSpotAngles ? light.spotAngle : (JToken)null,
                        ["innerSpotAngle"] = supportsSpotAngles ? light.innerSpotAngle : (JToken)null,
                        ["areaSize"] = supportsAreaSize ? Vector2Json(light.areaSize) : null,
                    },
                };
            }
            catch (Exception exception)
            {
                if (gameObject != null) UnityEngine.Object.DestroyImmediate(gameObject);
                return OpResult.Fail($"could not create {type} light: {exception.GetBaseException().Message}");
            }
        }

        private static bool TryReadType(string requested, out LightType type, out string error)
        {
            requested = requested?.Trim();
            if (string.Equals(requested, "Area", StringComparison.OrdinalIgnoreCase)) requested = "Rectangle";
            if (!Enum.TryParse(requested, true, out type) || !IsSupportedType(type))
            {
                error = $"unknown light type '{requested}'; expected one of [{string.Join(", ", SupportedTypeNames)}]";
                return false;
            }
            error = null;
            return true;
        }

        private static bool IsSupportedType(LightType type)
        {
            return type == LightType.Spot || type == LightType.Directional || type == LightType.Point ||
                   type == LightType.Rectangle || type == LightType.Disc || type == LightType.Pyramid ||
                   type == LightType.Box || type == LightType.Tube;
        }

        private static bool TryReadFloat(JToken token, float fallback, string name, out float value, out string error)
        {
            value = fallback;
            error = null;
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
            {
                error = $"{name} must be a number";
                return false;
            }
            value = (float)token;
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                error = $"{name} must be finite";
                return false;
            }
            return true;
        }

        private static bool TryReadColor(JToken token, out Color color, out string error)
        {
            color = Color.white;
            error = null;
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token.Type == JTokenType.String)
            {
                if (ColorUtility.TryParseHtmlString((string)token, out color)) return true;
                error = $"invalid color '{(string)token}'";
                return false;
            }
            if (token is JArray array && (array.Count == 3 || array.Count == 4))
            {
                if (!TryReadFloat(array[0], 0f, "color.r", out var r, out error) ||
                    !TryReadFloat(array[1], 0f, "color.g", out var g, out error) ||
                    !TryReadFloat(array[2], 0f, "color.b", out var b, out error) ||
                    !TryReadFloat(array.Count == 4 ? array[3] : null, 1f, "color.a", out var a, out error)) return false;
                color = new Color(r, g, b, a);
                return true;
            }
            if (token is JObject obj && obj["r"] != null && obj["g"] != null && obj["b"] != null)
            {
                if (!TryReadFloat(obj["r"], 0f, "color.r", out var r, out error) ||
                    !TryReadFloat(obj["g"], 0f, "color.g", out var g, out error) ||
                    !TryReadFloat(obj["b"], 0f, "color.b", out var b, out error) ||
                    !TryReadFloat(obj["a"], 1f, "color.a", out var a, out error)) return false;
                color = new Color(r, g, b, a);
                return true;
            }
            error = "color must be a hex string, RGB[A] array, or {r,g,b,a} object";
            return false;
        }

        private static bool TryReadVector3(JToken token, out Vector3 value, out string error)
        {
            value = Vector3.zero;
            error = null;
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token is JArray array && array.Count == 3)
            {
                if (!TryReadFloat(array[0], 0f, "position.x", out value.x, out error) ||
                    !TryReadFloat(array[1], 0f, "position.y", out value.y, out error) ||
                    !TryReadFloat(array[2], 0f, "position.z", out value.z, out error)) return false;
                return true;
            }
            if (token is JObject obj)
            {
                if (!TryReadFloat(obj["x"], 0f, "position.x", out value.x, out error) ||
                    !TryReadFloat(obj["y"], 0f, "position.y", out value.y, out error) ||
                    !TryReadFloat(obj["z"], 0f, "position.z", out value.z, out error)) return false;
                return true;
            }
            error = "position must be [x,y,z] or {x,y,z}";
            return false;
        }

        private static bool TryReadRotation(JToken token, out Quaternion value, out string error)
        {
            value = Quaternion.identity;
            error = null;
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token is JArray array && (array.Count == 3 || array.Count == 4))
            {
                if (!TryReadFloat(array[0], 0f, "rotation.x", out var x, out error) ||
                    !TryReadFloat(array[1], 0f, "rotation.y", out var y, out error) ||
                    !TryReadFloat(array[2], 0f, "rotation.z", out var z, out error)) return false;
                if (array.Count == 3) value = Quaternion.Euler(x, y, z);
                else
                {
                    if (!TryReadFloat(array[3], 1f, "rotation.w", out var w, out error)) return false;
                    value = new Quaternion(x, y, z, w).normalized;
                }
                return true;
            }
            if (token is JObject obj)
            {
                if (!TryReadFloat(obj["x"], 0f, "rotation.x", out var x, out error) ||
                    !TryReadFloat(obj["y"], 0f, "rotation.y", out var y, out error) ||
                    !TryReadFloat(obj["z"], 0f, "rotation.z", out var z, out error)) return false;
                if (obj["w"] == null) value = Quaternion.Euler(x, y, z);
                else
                {
                    if (!TryReadFloat(obj["w"], 1f, "rotation.w", out var w, out error)) return false;
                    value = new Quaternion(x, y, z, w).normalized;
                }
                return true;
            }
            error = "rotation must be Euler [x,y,z] or quaternion [x,y,z,w] / {x,y,z,w}";
            return false;
        }

        private static bool TryReadVector2(JToken token, Vector2 fallback, string name, out Vector2 value, out string error)
        {
            value = fallback;
            error = null;
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token is JArray array && array.Count == 2)
            {
                if (!TryReadFloat(array[0], fallback.x, $"{name}.x", out value.x, out error) ||
                    !TryReadFloat(array[1], fallback.y, $"{name}.y", out value.y, out error)) return false;
                return true;
            }
            if (token is JObject obj && obj["x"] != null && obj["y"] != null)
            {
                if (!TryReadFloat(obj["x"], fallback.x, $"{name}.x", out value.x, out error) ||
                    !TryReadFloat(obj["y"], fallback.y, $"{name}.y", out value.y, out error)) return false;
                return true;
            }
            error = $"{name} must be [x,y] or {{x,y}}";
            return false;
        }

        private static JObject ColorJson(Color color) => new JObject
        {
            ["r"] = color.r, ["g"] = color.g, ["b"] = color.b, ["a"] = color.a,
        };

        private static JObject Vector2Json(Vector2 value) => new JObject
        {
            ["x"] = value.x, ["y"] = value.y,
        };
    }
}
