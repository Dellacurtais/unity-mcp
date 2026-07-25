using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    /// Shared parsing utilities for converting LLM-supplied JSON arguments into
    /// Unity types. Tolerates both array and object forms ([x,y,z] or {x,y,z}).
    public static class JsonHelpers
    {
        public static bool TryFiniteNumber(JToken token, out float value)
        {
            value = 0f;
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) return false;
            value = (float)token;
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public static bool TryParseFiniteVector3(JToken token, out Vector3 value)
        {
            value = Vector3.zero;
            if (token is JArray array && array.Count == 3
                && TryFiniteNumber(array[0], out var x)
                && TryFiniteNumber(array[1], out var y)
                && TryFiniteNumber(array[2], out var z))
            {
                value = new Vector3(x, y, z);
                return true;
            }
            if (token is JObject obj && obj.Count == 3
                && TryFiniteNumber(obj["x"], out x)
                && TryFiniteNumber(obj["y"], out y)
                && TryFiniteNumber(obj["z"], out z))
            {
                value = new Vector3(x, y, z);
                return true;
            }
            return false;
        }

        public static bool TryParseFiniteRotation(JToken token, out Quaternion value)
        {
            value = Quaternion.identity;
            if (token is JArray array)
            {
                if (array.Count == 3
                    && TryFiniteNumber(array[0], out var eulerX)
                    && TryFiniteNumber(array[1], out var eulerY)
                    && TryFiniteNumber(array[2], out var eulerZ))
                {
                    value = Quaternion.Euler(eulerX, eulerY, eulerZ);
                    return true;
                }
                if (array.Count == 4
                    && TryFiniteNumber(array[0], out var x)
                    && TryFiniteNumber(array[1], out var y)
                    && TryFiniteNumber(array[2], out var z)
                    && TryFiniteNumber(array[3], out var w))
                    return TryNormalizeQuaternion(x, y, z, w, out value);
            }
            if (token is JObject obj)
            {
                if (obj["w"] == null && obj.Count == 3
                    && TryFiniteNumber(obj["x"], out var eulerX)
                    && TryFiniteNumber(obj["y"], out var eulerY)
                    && TryFiniteNumber(obj["z"], out var eulerZ))
                {
                    value = Quaternion.Euler(eulerX, eulerY, eulerZ);
                    return true;
                }
                if (obj.Count == 4
                    && TryFiniteNumber(obj["x"], out var x)
                    && TryFiniteNumber(obj["y"], out var y)
                    && TryFiniteNumber(obj["z"], out var z)
                    && TryFiniteNumber(obj["w"], out var w))
                    return TryNormalizeQuaternion(x, y, z, w, out value);
            }
            return false;
        }

        public static bool TryParseFiniteColor(JToken token, out Color value)
        {
            value = default;
            if (token?.Type == JTokenType.String)
                return ColorUtility.TryParseHtmlString((string)token, out value);
            if (token is JArray array && (array.Count == 3 || array.Count == 4)
                && TryFiniteNumber(array[0], out var r)
                && TryFiniteNumber(array[1], out var g)
                && TryFiniteNumber(array[2], out var b)
                && (array.Count == 3 || TryFiniteNumber(array[3], out _)))
            {
                value = new Color(r, g, b, array.Count == 4 ? (float)array[3] : 1f);
                return true;
            }
            if (token is JObject obj && (obj.Count == 3 || obj.Count == 4)
                && TryFiniteNumber(obj["r"], out r)
                && TryFiniteNumber(obj["g"], out g)
                && TryFiniteNumber(obj["b"], out b)
                && (obj["a"] == null || TryFiniteNumber(obj["a"], out _)))
            {
                value = new Color(r, g, b, obj["a"] != null ? (float)obj["a"] : 1f);
                return true;
            }
            return false;
        }

        private static bool TryNormalizeQuaternion(float x, float y, float z, float w, out Quaternion value)
        {
            value = new Quaternion(x, y, z, w);
            var magnitude = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            if (magnitude <= 1e-12f) { value = Quaternion.identity; return false; }
            value = new Quaternion(x / magnitude, y / magnitude, z / magnitude, w / magnitude);
            return true;
        }

        public static Vector3 ParseVector3(JToken t, Vector3 fallback = default)
        {
            if (t == null || t.Type == JTokenType.Null) return fallback;
            if (t is JArray a && a.Count >= 3)
                return new Vector3((float)a[0], (float)a[1], (float)a[2]);
            if (t is JObject o)
                return new Vector3(
                    (float?)o["x"] ?? fallback.x,
                    (float?)o["y"] ?? fallback.y,
                    (float?)o["z"] ?? fallback.z);
            return fallback;
        }

        public static Quaternion ParseRotation(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return Quaternion.identity;
            if (t is JArray a)
            {
                if (a.Count == 4)
                    return new Quaternion((float)a[0], (float)a[1], (float)a[2], (float)a[3]);
                if (a.Count == 3)
                    return Quaternion.Euler((float)a[0], (float)a[1], (float)a[2]);
            }
            if (t is JObject o)
            {
                if (o["w"] != null)
                    return new Quaternion((float)o["x"], (float)o["y"], (float)o["z"], (float)o["w"]);
                return Quaternion.Euler(
                    (float?)o["x"] ?? 0f,
                    (float?)o["y"] ?? 0f,
                    (float?)o["z"] ?? 0f);
            }
            return Quaternion.identity;
        }

        public static Color ParseColor(JToken t, Color fallback = default)
        {
            if (t == null || t.Type == JTokenType.Null) return fallback == default ? Color.white : fallback;
            if (t.Type == JTokenType.String)
            {
                var s = (string)t;
                if (ColorUtility.TryParseHtmlString(s, out var c)) return c;
                return fallback;
            }
            if (t is JArray a && a.Count >= 3)
            {
                var alpha = a.Count >= 4 ? (float)a[3] : 1f;
                return new Color((float)a[0], (float)a[1], (float)a[2], alpha);
            }
            if (t is JObject o)
            {
                return new Color(
                    (float?)o["r"] ?? 1f,
                    (float?)o["g"] ?? 1f,
                    (float?)o["b"] ?? 1f,
                    (float?)o["a"] ?? 1f);
            }
            return fallback == default ? Color.white : fallback;
        }

        public static GameObject ResolveParent(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Integer)
            {
                var id = (int)t;
                return EditorUtility.InstanceIDToObject(id) as GameObject;
            }
            // not supporting path lookup for now — clients should pass instanceId
            return null;
        }

        public static JObject Vec3(Vector3 v) => new JObject
        {
            ["x"] = v.x, ["y"] = v.y, ["z"] = v.z,
        };
    }
}
