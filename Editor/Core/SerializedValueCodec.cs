using Maicon.SceneAnalyzer.Editor.Core;
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Core
{
    public static class SerializedValueCodec
    {
        public sealed class ReadOptions
        {
            public int MaxDepth = 4;
            public int ArrayOffset;
            public int MaxArrayItems = 32;
        }

        public static JToken Read(SerializedProperty property, ReadOptions options = null)
        {
            if (property == null) return JValue.CreateNull();
            options = options ?? new ReadOptions();
            return ReadInternal(property.Copy(), options, 0);
        }

        public static void Apply(SerializedProperty property, JToken value)
        {
            if (property == null) throw new ArgumentNullException(nameof(property));
            if (value == null || value.Type == JTokenType.Null)
            {
                if (property.propertyType == SerializedPropertyType.ObjectReference) property.objectReferenceValue = null;
                else if (property.propertyType == SerializedPropertyType.String) property.stringValue = null;
                return;
            }

            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Character:
                    property.intValue = ReadInteger(value, property.propertyType == SerializedPropertyType.LayerMask);
                    return;
                case SerializedPropertyType.Boolean: property.boolValue = (bool)value; return;
                case SerializedPropertyType.Float: property.doubleValue = (double)value; return;
                case SerializedPropertyType.String: property.stringValue = (string)value; return;
                case SerializedPropertyType.Color: property.colorValue = ReadColor(value); return;
                case SerializedPropertyType.Vector2:
                { var v = ReadVector(value, 2); property.vector2Value = new Vector2(v[0], v[1]); return; }
                case SerializedPropertyType.Vector3:
                { var v = ReadVector(value, 3); property.vector3Value = new Vector3(v[0], v[1], v[2]); return; }
                case SerializedPropertyType.Vector4:
                { var v = ReadVector(value, 4); property.vector4Value = new Vector4(v[0], v[1], v[2], v[3]); return; }
                case SerializedPropertyType.Vector2Int:
                { var v = ReadVector(value, 2); property.vector2IntValue = new Vector2Int(Mathf.RoundToInt(v[0]), Mathf.RoundToInt(v[1])); return; }
                case SerializedPropertyType.Vector3Int:
                { var v = ReadVector(value, 3); property.vector3IntValue = new Vector3Int(Mathf.RoundToInt(v[0]), Mathf.RoundToInt(v[1]), Mathf.RoundToInt(v[2])); return; }
                case SerializedPropertyType.Quaternion:
                { var v = ReadVector(value, 4); property.quaternionValue = new Quaternion(v[0], v[1], v[2], v[3]); return; }
                case SerializedPropertyType.Rect:
                { var v = ReadVector(value, 4, "x", "y", "width", "height"); property.rectValue = new Rect(v[0], v[1], v[2], v[3]); return; }
                case SerializedPropertyType.RectInt:
                { var v = ReadVector(value, 4, "x", "y", "width", "height"); property.rectIntValue = new RectInt(Mathf.RoundToInt(v[0]), Mathf.RoundToInt(v[1]), Mathf.RoundToInt(v[2]), Mathf.RoundToInt(v[3])); return; }
                case SerializedPropertyType.Bounds:
                {
                    var obj = value as JObject ?? throw new InvalidOperationException("Bounds requires {center,size}.");
                    property.boundsValue = new Bounds(ReadVector3(obj["center"]), ReadVector3(obj["size"])); return;
                }
                case SerializedPropertyType.BoundsInt:
                {
                    var obj = value as JObject ?? throw new InvalidOperationException("BoundsInt requires {position,size}.");
                    var p = ReadVector(obj["position"], 3); var s = ReadVector(obj["size"], 3);
                    property.boundsIntValue = new BoundsInt(
                        new Vector3Int(Mathf.RoundToInt(p[0]), Mathf.RoundToInt(p[1]), Mathf.RoundToInt(p[2])),
                        new Vector3Int(Mathf.RoundToInt(s[0]), Mathf.RoundToInt(s[1]), Mathf.RoundToInt(s[2]))); return;
                }
                case SerializedPropertyType.Enum:
                    ApplyEnum(property, value); return;
                case SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = ResolveObjectReference(value); return;
                case SerializedPropertyType.AnimationCurve:
                    property.animationCurveValue = ReadCurve(value); return;
                case SerializedPropertyType.Gradient:
                    property.gradientValue = ReadGradient(value); return;
                case SerializedPropertyType.Generic:
                    ApplyGeneric(property, value); return;
                default:
                    throw new InvalidOperationException($"UNSUPPORTED_TYPE: {property.propertyType}");
            }
        }

        private static JToken ReadInternal(SerializedProperty p, ReadOptions options, int depth)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Character: return p.longValue;
                case SerializedPropertyType.Boolean: return p.boolValue;
                case SerializedPropertyType.Float: return p.doubleValue;
                case SerializedPropertyType.String: return p.stringValue == null ? JValue.CreateNull() : new JValue(p.stringValue);
                case SerializedPropertyType.Color: return ColorJson(p.colorValue);
                case SerializedPropertyType.Vector2: return new JArray(p.vector2Value.x, p.vector2Value.y);
                case SerializedPropertyType.Vector3: return VectorJson(p.vector3Value);
                case SerializedPropertyType.Vector4: return new JArray(p.vector4Value.x, p.vector4Value.y, p.vector4Value.z, p.vector4Value.w);
                case SerializedPropertyType.Vector2Int: return new JArray(p.vector2IntValue.x, p.vector2IntValue.y);
                case SerializedPropertyType.Vector3Int: return new JArray(p.vector3IntValue.x, p.vector3IntValue.y, p.vector3IntValue.z);
                case SerializedPropertyType.Quaternion: return new JArray(p.quaternionValue.x, p.quaternionValue.y, p.quaternionValue.z, p.quaternionValue.w);
                case SerializedPropertyType.Enum:
                    return new JObject { ["name"] = p.enumValueIndex >= 0 && p.enumValueIndex < p.enumDisplayNames.Length ? p.enumDisplayNames[p.enumValueIndex] : null, ["index"] = p.enumValueIndex, ["options"] = new JArray(p.enumDisplayNames) };
                case SerializedPropertyType.ObjectReference:
                    if (p.objectReferenceValue == null) return new JObject { ["null"] = true, ["broken"] = UnityObjectIds.HasObjectReference(p) };
                    return new JObject { ["target"] = ToolTargetRef.Capture(p.objectReferenceValue)?.ToJson(), ["name"] = p.objectReferenceValue.name, ["type"] = p.objectReferenceValue.GetType().FullName };
                case SerializedPropertyType.Rect: return new JObject { ["x"] = p.rectValue.x, ["y"] = p.rectValue.y, ["width"] = p.rectValue.width, ["height"] = p.rectValue.height };
                case SerializedPropertyType.RectInt: return new JObject { ["x"] = p.rectIntValue.x, ["y"] = p.rectIntValue.y, ["width"] = p.rectIntValue.width, ["height"] = p.rectIntValue.height };
                case SerializedPropertyType.Bounds: return new JObject { ["center"] = VectorJson(p.boundsValue.center), ["size"] = VectorJson(p.boundsValue.size) };
                case SerializedPropertyType.BoundsInt: return new JObject { ["position"] = new JArray(p.boundsIntValue.position.x, p.boundsIntValue.position.y, p.boundsIntValue.position.z), ["size"] = new JArray(p.boundsIntValue.size.x, p.boundsIntValue.size.y, p.boundsIntValue.size.z) };
                case SerializedPropertyType.AnimationCurve: return CurveJson(p.animationCurveValue);
                case SerializedPropertyType.Gradient: return GradientJson(p.gradientValue);
                case SerializedPropertyType.Generic: return ReadGeneric(p, options, depth);
                default: return new JObject { ["unsupported"] = true, ["propertyType"] = p.propertyType.ToString(), ["unityType"] = p.type };
            }
        }

        private static JToken ReadGeneric(SerializedProperty p, ReadOptions options, int depth)
        {
            if (p.isArray)
            {
                int offset = Math.Max(0, options.ArrayOffset);
                int count = Math.Min(Math.Max(0, p.arraySize - offset), Math.Max(0, options.MaxArrayItems));
                var items = new JArray();
                for (int i = 0; i < count; i++) items.Add(ReadInternal(p.GetArrayElementAtIndex(offset + i), options, depth + 1));
                return new JObject { ["items"] = items, ["offset"] = offset, ["count"] = count, ["total"] = p.arraySize, ["truncated"] = offset + count < p.arraySize };
            }
            if (depth >= options.MaxDepth) return new JObject { ["truncated"] = true, ["type"] = p.type, ["reason"] = "maxDepth" };

            var fields = new JObject();
            var iterator = p.Copy();
            var end = iterator.GetEndProperty();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, end))
            {
                enterChildren = false;
                fields[iterator.name] = ReadInternal(iterator, options, depth + 1);
            }
            return new JObject { ["type"] = p.type, ["fields"] = fields };
        }

        private static void ApplyGeneric(SerializedProperty property, JToken value)
        {
            if (property.isArray)
            {
                var items = value as JArray ?? (value as JObject)?["items"] as JArray;
                if (items == null) throw new InvalidOperationException("Array property requires an array or {items:[...]}.");
                property.arraySize = items.Count;
                for (int i = 0; i < items.Count; i++) Apply(property.GetArrayElementAtIndex(i), items[i]);
                return;
            }
            var fields = value as JObject;
            if (fields?["fields"] is JObject nested) fields = nested;
            if (fields == null) throw new InvalidOperationException("Struct property requires an object.");
            foreach (var field in fields.Properties())
            {
                var child = property.FindPropertyRelative(field.Name);
                if (child != null) Apply(child, field.Value);
            }
        }

        private static UnityEngine.Object ResolveObjectReference(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return null;
            var targetToken = (value as JObject)?["target"] ?? value;
            var targetRef = ToolTargetRef.FromJson(targetToken);
            var resolved = ToolTargetResolver.Resolve(targetRef, out var error);
            if (resolved == null && error != null) throw new InvalidOperationException(error);
            return resolved;
        }

        private static void ApplyEnum(SerializedProperty property, JToken value)
        {
            if (value.Type == JTokenType.Integer) { property.enumValueIndex = (int)value; return; }
            var name = value.Type == JTokenType.String ? (string)value : (string)value["name"];
            var index = Array.IndexOf(property.enumNames, name);
            if (index < 0) index = Array.IndexOf(property.enumDisplayNames, name);
            if (index < 0) throw new InvalidOperationException($"Unknown enum value '{name}'.");
            property.enumValueIndex = index;
        }

        private static int ReadInteger(JToken value, bool layerMask)
        {
            if (!layerMask || value.Type == JTokenType.Integer) return (int)value;
            if (value.Type == JTokenType.String) return LayerMask.GetMask((string)value);
            if (value is JArray layers) return LayerMask.GetMask(layers.Values<string>().ToArray());
            return (int)value;
        }

        private static float[] ReadVector(JToken value, int count, params string[] names)
        {
            var result = new float[count];
            if (value is JArray array)
            {
                if (array.Count < count) throw new InvalidOperationException($"Expected {count} vector values.");
                for (int i = 0; i < count; i++) result[i] = (float)array[i];
                return result;
            }
            var obj = value as JObject ?? throw new InvalidOperationException("Vector requires an array or object.");
            var defaults = new[] { "x", "y", "z", "w" };
            for (int i = 0; i < count; i++) result[i] = (float?)(obj[names.Length > i ? names[i] : defaults[i]]) ?? 0f;
            return result;
        }

        private static Vector3 ReadVector3(JToken value) { var v = ReadVector(value, 3); return new Vector3(v[0], v[1], v[2]); }
        private static Color ReadColor(JToken value) { var v = ReadVector(value, 4, "r", "g", "b", "a"); return new Color(v[0], v[1], v[2], value is JObject o && o["a"] == null ? 1f : v[3]); }
        private static JArray VectorJson(Vector3 v) => new JArray(v.x, v.y, v.z);
        private static JObject ColorJson(Color c) => new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };

        private static AnimationCurve ReadCurve(JToken value)
        {
            var keys = (value as JObject)?["keys"] as JArray ?? value as JArray ?? throw new InvalidOperationException("AnimationCurve requires keys.");
            var frames = new Keyframe[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                var key = keys[i] as JObject ?? throw new InvalidOperationException("Curve key must be an object.");
                frames[i] = new Keyframe((float?)(key["time"] ?? key["t"]) ?? 0f, (float?)(key["value"] ?? key["v"]) ?? 0f, (float?)(key["inTangent"] ?? key["inT"]) ?? 0f, (float?)(key["outTangent"] ?? key["outT"]) ?? 0f);
            }
            return new AnimationCurve(frames);
        }

        private static JObject CurveJson(AnimationCurve curve)
        {
            var keys = new JArray();
            if (curve != null) foreach (var key in curve.keys) keys.Add(new JObject { ["time"] = key.time, ["value"] = key.value, ["inTangent"] = key.inTangent, ["outTangent"] = key.outTangent });
            return new JObject { ["keys"] = keys, ["preWrap"] = curve?.preWrapMode.ToString(), ["postWrap"] = curve?.postWrapMode.ToString() };
        }

        private static Gradient ReadGradient(JToken value)
        {
            var obj = value as JObject ?? throw new InvalidOperationException("Gradient requires an object.");
            var colors = obj["colorKeys"] as JArray ?? new JArray();
            var alphas = obj["alphaKeys"] as JArray ?? new JArray();
            var colorKeys = new GradientColorKey[colors.Count];
            var alphaKeys = new GradientAlphaKey[alphas.Count];
            for (int i = 0; i < colors.Count; i++) colorKeys[i] = new GradientColorKey(ReadColor(colors[i]["color"] ?? colors[i]), (float?)(colors[i]["time"] ?? colors[i]["t"]) ?? 0f);
            for (int i = 0; i < alphas.Count; i++) alphaKeys[i] = new GradientAlphaKey((float?)(alphas[i]["alpha"] ?? alphas[i]["a"]) ?? 1f, (float?)(alphas[i]["time"] ?? alphas[i]["t"]) ?? 0f);
            var gradient = new Gradient(); gradient.SetKeys(colorKeys, alphaKeys); return gradient;
        }

        private static JObject GradientJson(Gradient gradient)
        {
            var colors = new JArray(); var alphas = new JArray();
            if (gradient != null)
            {
                foreach (var key in gradient.colorKeys) colors.Add(new JObject { ["color"] = ColorJson(key.color), ["time"] = key.time });
                foreach (var key in gradient.alphaKeys) alphas.Add(new JObject { ["alpha"] = key.alpha, ["time"] = key.time });
            }
            return new JObject { ["colorKeys"] = colors, ["alphaKeys"] = alphas };
        }
    }
}
