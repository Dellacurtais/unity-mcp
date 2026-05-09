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
