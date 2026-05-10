using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    /// Generic property editor. Resolves the component on a GameObject by type
    /// name, then sets the named SerializedProperty using SerializedObject so
    /// changes participate in the Undo system and respect [SerializeField]
    /// semantics.
    public static class ComponentEditor
    {
        public static OpResult RemoveComponent(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var typeName = (string)args["componentType"];
            if (string.IsNullOrEmpty(typeName)) return OpResult.Fail("INVALID_ARG: componentType is required");

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"REF_BROKEN: GameObject not found: instanceId={id}");

            var type = ResolveComponentType(typeName);
            if (type == null) return OpResult.Fail($"INVALID_ARG: unknown component type '{typeName}'");

            // Cannot remove Transform — Unity refuses.
            if (type == typeof(Transform)) return OpResult.Fail("UNSUPPORTED_OP: cannot remove Transform");

            var component = go.GetComponent(type);
            if (component == null) return OpResult.Fail($"MISSING_COMPONENT: {typeName} not found on '{go.name}'");

            // Optional `nth` arg to disambiguate when multiple of the same type are attached.
            var nth = (int?)args["nth"];
            if (nth.HasValue && nth.Value > 0)
            {
                var all = go.GetComponents(type);
                if (nth.Value >= all.Length) return OpResult.Fail($"INVALID_ARG: nth={nth.Value} out of range (only {all.Length} of type {typeName})");
                component = all[nth.Value];
            }

            Undo.DestroyObjectImmediate(component);
            return OpResult.Success(id, $"removed {type.Name}");
        }

        public static OpResult SetEnabled(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var typeName = (string)args["componentType"];
            if (string.IsNullOrEmpty(typeName)) return OpResult.Fail("INVALID_ARG: componentType is required");
            var enabled = (bool?)args["enabled"] ?? true;

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"REF_BROKEN: GameObject not found: instanceId={id}");

            var type = ResolveComponentType(typeName);
            if (type == null) return OpResult.Fail($"INVALID_ARG: unknown component type '{typeName}'");

            var component = go.GetComponent(type);
            if (component == null) return OpResult.Fail($"MISSING_COMPONENT: {typeName} not found on '{go.name}'");

            // Behaviour.enabled covers MonoBehaviour + most renderers/colliders
            // (Renderer.enabled / Collider.enabled exist but inherit differently).
            if (component is Behaviour b)
            {
                Undo.RecordObject(b, enabled ? "Enable Component" : "Disable Component");
                b.enabled = enabled;
                return OpResult.Success(id, $"{type.Name}.enabled = {enabled}");
            }
            if (component is Renderer r)
            {
                Undo.RecordObject(r, enabled ? "Enable Renderer" : "Disable Renderer");
                r.enabled = enabled;
                return OpResult.Success(id, $"{type.Name}.enabled = {enabled}");
            }
            if (component is Collider c)
            {
                Undo.RecordObject(c, enabled ? "Enable Collider" : "Disable Collider");
                c.enabled = enabled;
                return OpResult.Success(id, $"{type.Name}.enabled = {enabled}");
            }
            return OpResult.Fail($"UNSUPPORTED_OP: {type.Name} has no 'enabled' field (not a Behaviour/Renderer/Collider)");
        }

        public static OpResult AddComponent(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var typeName = (string)args["componentType"];
            if (string.IsNullOrEmpty(typeName)) return OpResult.Fail("INVALID_ARG: componentType is required");

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"REF_BROKEN: GameObject not found: instanceId={id}");

            var type = ResolveComponentType(typeName);
            if (type == null)
            {
                // Common cause: the agent just created a new MonoBehaviour script via
                // file write and Unity hasn't compiled it yet. Make this actionable.
                var hint = "";
                if (EditorApplication.isCompiling)
                    hint = "\nUnity is compiling RIGHT NOW — wait for the bottom-right spinner to finish, then retry.";
                else
                    hint = "\nIf you JUST created this script via the filesystem: " +
                           "(1) call `refresh_assets` (with wait=true), " +
                           "(2) check the Console for compile errors, " +
                           "(3) verify the class name in the .cs file matches '" + typeName + "', " +
                           "(4) retry. " +
                           "If you didn't create it, use `list_component_types nameContains=\"" + typeName + "\"` to find the real name.";
                return OpResult.Fail($"INVALID_ARG: unknown component type '{typeName}'{hint}");
            }

            var added = Undo.AddComponent(go, type);
            return OpResult.Success(added.GetInstanceID(), $"added {type.Name}");
        }

        public static OpResult SetProperty(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var typeName = (string)args["componentType"];
            var path = (string)args["propertyPath"];
            if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(path))
                return OpResult.Fail("INVALID_ARG: componentType and propertyPath are required");

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"REF_BROKEN: GameObject not found: instanceId={id}");

            var type = ResolveComponentType(typeName);
            if (type == null) return OpResult.Fail($"INVALID_ARG: unknown component type '{typeName}'");

            var component = go.GetComponent(type);
            if (component == null) return OpResult.Fail($"MISSING_COMPONENT: {typeName} not found on GameObject");

            var so = new SerializedObject(component);
            var prop = so.FindProperty(path);
            if (prop == null) return OpResult.Fail($"INVALID_ARG: property '{path}' not found on {typeName}");

            var value = args["value"];
            try { ApplyValue(prop, value); }
            catch (Exception e) { return OpResult.Fail($"INVALID_ARG: failed to apply value: {e.Message}"); }

            so.ApplyModifiedProperties();
            return OpResult.Success(id, $"set {typeName}.{path}");
        }

        public static Type ResolveComponentType(string name)
            => ComponentTypeInspector.ResolveType(name);

        public static void ApplyValue(SerializedProperty prop, JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return;

            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Character:
                    // LayerMask serializes as int (bitmask). Accept int directly,
                    // OR an array of layer-name strings → resolve to bitmask via
                    // LayerMask.GetMask, OR a single layer-name string.
                    if (value is JArray layerArr)
                    {
                        var names = new System.Collections.Generic.List<string>();
                        foreach (var n in layerArr) if (n.Type == JTokenType.String) names.Add((string)n);
                        prop.intValue = names.Count > 0 ? UnityEngine.LayerMask.GetMask(names.ToArray()) : (int)value;
                    }
                    else if (value.Type == JTokenType.String)
                    {
                        var idx = UnityEngine.LayerMask.NameToLayer((string)value);
                        prop.intValue = idx >= 0 ? (1 << idx) : 0;
                    }
                    else
                    {
                        prop.intValue = (int)value;
                    }
                    break;
                case SerializedPropertyType.Boolean:
                    prop.boolValue = (bool)value;
                    break;
                case SerializedPropertyType.Float:
                    prop.floatValue = (float)value;
                    break;
                case SerializedPropertyType.String:
                    prop.stringValue = (string)value;
                    break;
                case SerializedPropertyType.Color:
                    prop.colorValue = JsonHelpers.ParseColor(value);
                    break;
                case SerializedPropertyType.Vector2:
                    var v2 = JsonHelpers.ParseVector3(value);
                    prop.vector2Value = new Vector2(v2.x, v2.y);
                    break;
                case SerializedPropertyType.Vector3:
                    prop.vector3Value = JsonHelpers.ParseVector3(value);
                    break;
                case SerializedPropertyType.Vector4:
                {
                    if (value is JArray a4 && a4.Count >= 4)
                        prop.vector4Value = new Vector4((float)a4[0], (float)a4[1], (float)a4[2], (float)a4[3]);
                    else { var v3 = JsonHelpers.ParseVector3(value); prop.vector4Value = new Vector4(v3.x, v3.y, v3.z, 0f); }
                    break;
                }
                case SerializedPropertyType.Vector2Int:
                {
                    var v = JsonHelpers.ParseVector3(value);
                    prop.vector2IntValue = new Vector2Int(Mathf.RoundToInt(v.x), Mathf.RoundToInt(v.y));
                    break;
                }
                case SerializedPropertyType.Vector3Int:
                {
                    var v = JsonHelpers.ParseVector3(value);
                    prop.vector3IntValue = new Vector3Int(Mathf.RoundToInt(v.x), Mathf.RoundToInt(v.y), Mathf.RoundToInt(v.z));
                    break;
                }
                case SerializedPropertyType.Rect:
                    prop.rectValue = ParseRect(value);
                    break;
                case SerializedPropertyType.Bounds:
                    prop.boundsValue = ParseBounds(value);
                    break;
                case SerializedPropertyType.Quaternion:
                    prop.quaternionValue = JsonHelpers.ParseRotation(value);
                    break;
                case SerializedPropertyType.Enum:
                    if (value.Type == JTokenType.Integer) prop.enumValueIndex = (int)value;
                    else
                    {
                        var s = (string)value;
                        var idx = System.Array.IndexOf(prop.enumNames, s);
                        if (idx >= 0) prop.enumValueIndex = idx;
                    }
                    break;
                case SerializedPropertyType.ObjectReference:
                    if (value.Type == JTokenType.Integer)
                        prop.objectReferenceValue = EditorUtility.InstanceIDToObject((int)value);
                    else if (value.Type == JTokenType.String)
                    {
                        var path = AssetDatabase.GUIDToAssetPath((string)value);
                        if (string.IsNullOrEmpty(path)) path = (string)value;
                        prop.objectReferenceValue = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    }
                    break;
                case SerializedPropertyType.AnimationCurve:
                    prop.animationCurveValue = ParseAnimationCurve(value);
                    break;
                case SerializedPropertyType.Gradient:
                    prop.gradientValue = ParseGradient(value);
                    break;
                default:
                    if ((prop.type ?? "").Contains("UnityEvent"))
                        throw new InvalidOperationException(
                            "UNSUPPORTED_TYPE: UnityEvent serialization not supported via set_property; needs a dedicated handler.");
                    throw new InvalidOperationException($"UNSUPPORTED_TYPE: {prop.propertyType}");
            }
        }

        private static AnimationCurve ParseAnimationCurve(JToken token)
        {
            var keys = token["keys"] as JArray;
            if (keys == null) throw new InvalidOperationException("INVALID_ARG: AnimationCurve requires 'keys' array");
            var frames = new Keyframe[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                var k = keys[i] as JObject;
                if (k == null) throw new InvalidOperationException($"INVALID_ARG: AnimationCurve keys[{i}] must be an object");
                var t = (float)(k["t"] ?? k["time"] ?? (JToken)0f);
                var v = (float)(k["v"] ?? k["value"] ?? (JToken)0f);
                var inT = (float?)(k["inT"] ?? k["inTangent"]) ?? 0f;
                var outT = (float?)(k["outT"] ?? k["outTangent"]) ?? 0f;
                frames[i] = new Keyframe(t, v, inT, outT);
            }
            var curve = new AnimationCurve(frames);
            if (token["preWrap"] != null && Enum.TryParse((string)token["preWrap"], true, out WrapMode pre))
                curve.preWrapMode = pre;
            if (token["postWrap"] != null && Enum.TryParse((string)token["postWrap"], true, out WrapMode post))
                curve.postWrapMode = post;
            return curve;
        }

        private static Gradient ParseGradient(JToken token)
        {
            var colorKeys = token["colorKeys"] as JArray;
            var alphaKeys = token["alphaKeys"] as JArray;
            if (colorKeys == null || alphaKeys == null)
                throw new InvalidOperationException("INVALID_ARG: Gradient requires 'colorKeys' and 'alphaKeys' arrays");

            var ck = new GradientColorKey[colorKeys.Count];
            for (int i = 0; i < colorKeys.Count; i++)
            {
                var k = colorKeys[i] as JObject;
                if (k == null) throw new InvalidOperationException($"INVALID_ARG: Gradient colorKeys[{i}] must be an object");
                var color = JsonHelpers.ParseColor(k["color"] ?? (JToken)k);
                var t = (float)(k["t"] ?? k["time"] ?? (JToken)0f);
                ck[i] = new GradientColorKey(color, t);
            }

            var ak = new GradientAlphaKey[alphaKeys.Count];
            for (int i = 0; i < alphaKeys.Count; i++)
            {
                var k = alphaKeys[i] as JObject;
                if (k == null) throw new InvalidOperationException($"INVALID_ARG: Gradient alphaKeys[{i}] must be an object");
                var a = (float)(k["a"] ?? k["alpha"] ?? (JToken)1f);
                var t = (float)(k["t"] ?? k["time"] ?? (JToken)0f);
                ak[i] = new GradientAlphaKey(a, t);
            }

            var grad = new Gradient();
            grad.SetKeys(ck, ak);
            if (token["mode"] != null && Enum.TryParse((string)token["mode"], true, out GradientMode gm))
                grad.mode = gm;
            return grad;
        }

        private static Bounds ParseBounds(JToken token)
        {
            var center = JsonHelpers.ParseVector3(token["center"]);
            // Bounds(center, size) takes the full size, not half-size. Accept
            // either 'extents' (half) or 'size' (full).
            if (token["extents"] != null)
            {
                var ext = JsonHelpers.ParseVector3(token["extents"]);
                return new Bounds(center, ext * 2f);
            }
            var size = JsonHelpers.ParseVector3(token["size"]);
            return new Bounds(center, size);
        }

        private static Rect ParseRect(JToken token)
        {
            if (token is JObject obj)
            {
                var x = (float?)obj["x"] ?? 0f;
                var y = (float?)obj["y"] ?? 0f;
                var w = (float?)(obj["w"] ?? obj["width"]) ?? 0f;
                var h = (float?)(obj["h"] ?? obj["height"]) ?? 0f;
                return new Rect(x, y, w, h);
            }
            if (token is JArray arr && arr.Count >= 4)
                return new Rect((float)arr[0], (float)arr[1], (float)arr[2], (float)arr[3]);
            throw new InvalidOperationException("INVALID_ARG: Rect requires { x, y, w/width, h/height } or [x, y, w, h]");
        }
    }
}
