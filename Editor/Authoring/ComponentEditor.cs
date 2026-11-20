using System;
using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public static class ComponentEditor
    {
        public static OpResult AddComponent(JObject args)
        {
            args ??= new JObject();
            var instanceId = (int?)args["instanceId"] ?? 0;
            var typeName = ((string)args["componentType"] ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(typeName)) return OpResult.Fail("INVALID_ARG: componentType is required");
            if (!SceneAuthoringSupport.TryResolveEditableGameObject(instanceId, out var gameObject, out var error))
                return OpResult.Fail(error);

            var type = ResolveComponentType(typeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type) || type.IsAbstract || type.ContainsGenericParameters)
                return OpResult.Fail($"INVALID_ARG: unknown or non-instantiable component type '{typeName}'");
            if (type == typeof(Transform) || type == typeof(RectTransform))
                return OpResult.Fail($"UNSUPPORTED_OP: cannot add {type.Name} to an existing GameObject");
            if (type.IsDefined(typeof(DisallowMultipleComponent), true) && gameObject.GetComponent(type) != null)
                return OpResult.Fail($"INVALID_ARG: {type.Name} disallows multiple components and already exists on '{gameObject.name}'");

            using (var mutation = new MutationScope($"Add {type.Name}"))
            {
                Component component;
                try { component = mutation.AddComponent(gameObject, type); }
                catch (Exception exception) { return OpResult.Fail($"ADD_COMPONENT_FAILED: {exception.Message}"); }
                if (component == null) return OpResult.Fail($"ADD_COMPONENT_FAILED: Unity could not add {type.Name}");
                mutation.Complete();
                return OpResult.Success(component.GetMcpInstanceId(), $"added {type.Name}");
            }
        }

        public static OpResult RemoveComponent(JObject args)
        {
            args ??= new JObject();
            var instanceId = (int?)args["instanceId"] ?? 0;
            var typeName = ((string)args["componentType"] ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(typeName)) return OpResult.Fail("INVALID_ARG: componentType is required");
            if (!SceneAuthoringSupport.TryResolveEditableGameObject(instanceId, out var gameObject, out var error))
                return OpResult.Fail(error);

            var type = ResolveComponentType(typeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
                return OpResult.Fail($"INVALID_ARG: unknown component type '{typeName}'");
            if (typeof(Transform).IsAssignableFrom(type))
                return OpResult.Fail("UNSUPPORTED_OP: cannot remove Transform or RectTransform");
            if (!TryResolveComponent(gameObject, type, args["nth"], out var component, out error))
                return OpResult.Fail(error);

            using (var mutation = new MutationScope($"Remove {type.Name}"))
            {
                mutation.Destroy(component);
                mutation.Complete();
            }
            return OpResult.Success(instanceId, $"removed {type.Name}");
        }

        public static OpResult SetEnabled(JObject args)
        {
            args ??= new JObject();
            var instanceId = (int?)args["instanceId"] ?? 0;
            var typeName = ((string)args["componentType"] ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(typeName)) return OpResult.Fail("INVALID_ARG: componentType is required");
            if (!SceneAuthoringSupport.TryResolveEditableGameObject(instanceId, out var gameObject, out var error))
                return OpResult.Fail(error);

            var type = ResolveComponentType(typeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
                return OpResult.Fail($"INVALID_ARG: unknown component type '{typeName}'");
            if (!TryResolveComponent(gameObject, type, args["nth"], out var component, out error))
                return OpResult.Fail(error);
            var enabled = (bool?)args["enabled"] ?? true;

            UnityEngine.Object target;
            Action apply;
            bool current;
            if (component is Behaviour behaviour)
            {
                target = behaviour;
                current = behaviour.enabled;
                apply = () => behaviour.enabled = enabled;
            }
            else if (component is Renderer renderer)
            {
                target = renderer;
                current = renderer.enabled;
                apply = () => renderer.enabled = enabled;
            }
            else if (component is Collider collider)
            {
                target = collider;
                current = collider.enabled;
                apply = () => collider.enabled = enabled;
            }
            else
            {
                return OpResult.Fail($"UNSUPPORTED_OP: {type.Name} has no supported enabled property");
            }

            if (current == enabled) return OpResult.Success(instanceId, $"{type.Name}.enabled already {enabled}");
            using (var mutation = new MutationScope(enabled ? "Enable Component" : "Disable Component"))
            {
                mutation.Record(target, enabled ? "Enable Component" : "Disable Component");
                apply();
                mutation.Complete();
            }
            return OpResult.Success(instanceId, $"{type.Name}.enabled = {enabled}");
        }

        public static OpResult SetProperty(JObject args)
        {
            args ??= new JObject();
            var instanceId = (int?)args["instanceId"] ?? 0;
            var typeName = ((string)args["componentType"] ?? string.Empty).Trim();
            var propertyPath = ((string)args["propertyPath"] ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(propertyPath))
                return OpResult.Fail("INVALID_ARG: componentType and propertyPath are required");
            if (!SceneAuthoringSupport.TryResolveEditableGameObject(instanceId, out var gameObject, out var error))
                return OpResult.Fail(error);

            var type = ResolveComponentType(typeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
                return OpResult.Fail($"INVALID_ARG: unknown component type '{typeName}'");
            if (!TryResolveComponent(gameObject, type, args["nth"], out var component, out error))
                return OpResult.Fail(error);

            var serializedObject = new SerializedObject(component);
            serializedObject.UpdateIfRequiredOrScript();
            var property = serializedObject.FindProperty(propertyPath);
            if (property == null) return OpResult.Fail($"INVALID_ARG: property '{propertyPath}' not found on {type.Name}");
            if (!property.editable) return OpResult.Fail($"UNSUPPORTED_OP: property '{propertyPath}' is read-only");

            try { ApplyValue(property, args["value"]); }
            catch (Exception exception) { return OpResult.Fail($"INVALID_ARG: failed to apply value: {exception.Message}"); }

            using (var mutation = new MutationScope($"Set {type.Name}.{propertyPath}"))
            {
                mutation.Record(component, $"Set {type.Name}.{propertyPath}");
                if (!mutation.Apply(serializedObject, component))
                    return OpResult.Success(instanceId, $"{type.Name}.{propertyPath} unchanged");
                mutation.Complete();
            }
            return OpResult.Success(instanceId, $"set {type.Name}.{propertyPath}");
        }

        public static Type ResolveComponentType(string name)
            => ComponentTypeInspector.ResolveType(name);

        public static void ApplyValue(SerializedProperty property, JToken value)
            => SerializedValueCodec.Apply(property, value);

        private static bool TryResolveComponent(
            GameObject gameObject, Type type, JToken nthToken, out Component component, out string error)
        {
            component = null;
            error = null;
            var nth = nthToken != null ? (int?)nthToken : 0;
            if (!nth.HasValue || nth.Value < 0)
            {
                error = "INVALID_ARG: nth must be a non-negative integer";
                return false;
            }
            var components = gameObject.GetComponents(type);
            if (nth.Value >= components.Length)
            {
                error = components.Length == 0
                    ? $"MISSING_COMPONENT: {type.Name} not found on '{gameObject.name}'"
                    : $"INVALID_ARG: nth={nth.Value} out of range; '{gameObject.name}' has {components.Length} {type.Name} component(s)";
                return false;
            }
            component = components[nth.Value];
            return true;
        }
    }
}
