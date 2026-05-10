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
            if (string.IsNullOrEmpty(typeName)) return OpResult.Fail("componentType is required");

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");

            var type = ResolveComponentType(typeName);
            if (type == null) return OpResult.Fail($"unknown component type '{typeName}'");

            // Cannot remove Transform — Unity refuses.
            if (type == typeof(Transform)) return OpResult.Fail("cannot remove Transform");

            var component = go.GetComponent(type);
            if (component == null) return OpResult.Fail($"component {typeName} not found on '{go.name}'");

            // Optional `nth` arg to disambiguate when multiple of the same type are attached.
            var nth = (int?)args["nth"];
            if (nth.HasValue && nth.Value > 0)
            {
                var all = go.GetComponents(type);
                if (nth.Value >= all.Length) return OpResult.Fail($"nth={nth.Value} out of range (only {all.Length} of type {typeName})");
                component = all[nth.Value];
            }

            Undo.DestroyObjectImmediate(component);
            return OpResult.Success(id, $"removed {type.Name}");
        }

        public static OpResult SetEnabled(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var typeName = (string)args["componentType"];
            if (string.IsNullOrEmpty(typeName)) return OpResult.Fail("componentType is required");
            var enabled = (bool?)args["enabled"] ?? true;

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");

            var type = ResolveComponentType(typeName);
            if (type == null) return OpResult.Fail($"unknown component type '{typeName}'");

            var component = go.GetComponent(type);
            if (component == null) return OpResult.Fail($"component {typeName} not found on '{go.name}'");

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
            return OpResult.Fail($"{type.Name} has no 'enabled' field (not a Behaviour/Renderer/Collider)");
        }

        public static OpResult AddComponent(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var typeName = (string)args["componentType"];
            if (string.IsNullOrEmpty(typeName)) return OpResult.Fail("componentType is required");

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");

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
                return OpResult.Fail($"unknown component type '{typeName}'{hint}");
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
                return OpResult.Fail("componentType and propertyPath are required");

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");

            var type = ResolveComponentType(typeName);
            if (type == null) return OpResult.Fail($"unknown component type '{typeName}'");

            var component = go.GetComponent(type);
            if (component == null) return OpResult.Fail($"component {typeName} not found on GameObject");

            var so = new SerializedObject(component);
            var prop = so.FindProperty(path);
            if (prop == null) return OpResult.Fail($"property '{path}' not found on {typeName}");

            var value = args["value"];
            try { ApplyValue(prop, value); }
            catch (Exception e) { return OpResult.Fail($"failed to apply value: {e.Message}"); }

            so.ApplyModifiedProperties();
            return OpResult.Success(id, $"set {typeName}.{path}");
        }

        private static Type ResolveComponentType(string name)
        {
            // Try exact / fast paths first.
            var t = Type.GetType(name);
            if (t != null) return t;
            // Walk loaded assemblies as fallback (matches simple names too).
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(name);
                if (t != null) return t;
                foreach (var candidate in asm.GetTypes())
                {
                    if (candidate.Name == name && typeof(Component).IsAssignableFrom(candidate))
                        return candidate;
                }
            }
            return null;
        }

        private static void ApplyValue(SerializedProperty prop, JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return;

            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    prop.intValue = (int)value;
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
                default:
                    throw new InvalidOperationException($"unsupported property type {prop.propertyType}");
            }
        }
    }
}
