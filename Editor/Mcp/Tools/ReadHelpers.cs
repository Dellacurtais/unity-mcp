using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools
{
    /// Serialises Component instances into JSON via SerializedObject. Top-level
    /// fields are dumped with their value; struct/object children are summarised
    /// (no deep recursion) so payloads stay bounded for LLM consumption.
    internal static class ReadHelpers
    {
        public const int DefaultMaxArrayItems = 16;

        public static JObject SerializeComponent(Component c, int maxArrayItems = DefaultMaxArrayItems)
        {
            var root = new JObject
            {
                ["componentType"] = c.GetType().FullName,
                ["instanceId"] = c.GetInstanceID(),
                ["enabled"] = (c is Behaviour b) ? b.enabled : (JToken)true,
            };

            var so = new SerializedObject(c);
            var it = so.GetIterator();
            var enterChildren = true;
            var props = new JObject();
            while (it.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (it.name == "m_Script") continue;
                try { props[it.name] = SerializeProperty(it, maxArrayItems); }
                catch { props[it.name] = "[unsupported]"; }
            }
            root["properties"] = props;
            return root;
        }

        public static JToken SerializeProperty(SerializedProperty p, int maxArrayItems)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                    return p.intValue;
                case SerializedPropertyType.Boolean:
                    return p.boolValue;
                case SerializedPropertyType.Float:
                    return p.floatValue;
                case SerializedPropertyType.String:
                    return p.stringValue;
                case SerializedPropertyType.Color:
                {
                    var c = p.colorValue;
                    return new JArray { c.r, c.g, c.b, c.a };
                }
                case SerializedPropertyType.Vector2:
                {
                    var v = p.vector2Value;
                    return new JArray { v.x, v.y };
                }
                case SerializedPropertyType.Vector3:
                {
                    var v = p.vector3Value;
                    return new JArray { v.x, v.y, v.z };
                }
                case SerializedPropertyType.Vector4:
                {
                    var v = p.vector4Value;
                    return new JArray { v.x, v.y, v.z, v.w };
                }
                case SerializedPropertyType.Quaternion:
                {
                    var q = p.quaternionValue;
                    return new JArray { q.x, q.y, q.z, q.w };
                }
                case SerializedPropertyType.ObjectReference:
                {
                    var obj = p.objectReferenceValue;
                    if (obj == null)
                    {
                        return new JObject
                        {
                            ["null"] = true,
                            ["broken"] = p.objectReferenceInstanceIDValue != 0,
                        };
                    }
                    var assetPath = AssetDatabase.GetAssetPath(obj);
                    return new JObject
                    {
                        ["instanceId"] = obj.GetInstanceID(),
                        ["name"] = obj.name,
                        ["type"] = obj.GetType().Name,
                        ["assetPath"] = string.IsNullOrEmpty(assetPath) ? null : assetPath,
                    };
                }
                case SerializedPropertyType.Enum:
                    return (p.enumValueIndex >= 0 && p.enumValueIndex < p.enumNames.Length)
                        ? p.enumNames[p.enumValueIndex]
                        : (JToken)p.enumValueIndex;
                case SerializedPropertyType.Generic:
                    if (p.isArray)
                    {
                        var arr = new JArray();
                        var n = Math.Min(p.arraySize, maxArrayItems);
                        for (int i = 0; i < n; i++)
                            arr.Add(SerializeProperty(p.GetArrayElementAtIndex(i), maxArrayItems));
                        if (p.arraySize > n) arr.Add($"[+{p.arraySize - n} more truncated]");
                        return arr;
                    }
                    return $"[struct: {p.type}]";
                case SerializedPropertyType.AnimationCurve:
                    return $"[AnimationCurve {p.animationCurveValue?.length} keys]";
                case SerializedPropertyType.Bounds:
                {
                    var b = p.boundsValue;
                    return new JObject { ["center"] = JsonOf(b.center), ["size"] = JsonOf(b.size) };
                }
                case SerializedPropertyType.Rect:
                {
                    var r = p.rectValue;
                    return new JArray { r.x, r.y, r.width, r.height };
                }
                default:
                    return $"[{p.propertyType}]";
            }
        }

        private static JArray JsonOf(Vector3 v) => new JArray { v.x, v.y, v.z };
    }
}
