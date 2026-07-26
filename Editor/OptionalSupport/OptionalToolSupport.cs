using System;
using System.Collections.Generic;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Optional
{
    public static class OptionalToolSupport
    {
        public static JObject EmptySchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };

        public static JObject OutputSchema(JObject properties, params string[] required) => new JObject
        {
            ["type"] = "object",
            ["properties"] = properties ?? new JObject(),
            ["required"] = new JArray(required ?? Array.Empty<string>()),
            ["additionalProperties"] = true,
        };

        public static JObject TargetSchema(JObject extra = null, params string[] requiredExtra)
        {
            var properties = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
            };
            if (extra != null)
                foreach (var property in extra.Properties()) properties[property.Name] = property.Value.DeepClone();
            var required = new JArray { "instanceId" };
            if (requiredExtra != null)
                foreach (var name in requiredExtra) required.Add(name);
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false,
            };
        }

        public static JObject CreateSchema(JObject extra = null)
        {
            var properties = new JObject
            {
                ["name"] = new JObject { ["type"] = "string", ["default"] = "Optional Package Object" },
                ["parentInstanceId"] = new JObject { ["type"] = "integer" },
                ["parentTarget"] = Maicon.SceneAnalyzer.Editor.Mcp.Tools.SchemaHelpers.TargetRefSchema,
            };
            if (extra != null)
                foreach (var property in extra.Properties()) properties[property.Name] = property.Value.DeepClone();
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = false,
            };
        }

        public static JObject PropertiesSchema(IReadOnlyDictionary<string, string[]> allowlist)
        {
            var properties = new JObject();
            if (allowlist != null)
                foreach (var name in allowlist.Keys.OrderBy(value => value, StringComparer.Ordinal))
                    properties[name] = new JObject();
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = false,
                ["minProperties"] = 1,
            };
        }

        public static Type ResolveType(params string[] names)
        {
            if (names == null) return null;
            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                var direct = Type.GetType(name, false);
                if (direct != null) return direct;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type type = null;
                    try { type = assembly.GetType(name, false); }
                    catch { }
                    if (type != null) return type;
                }
            }
            return null;
        }

        public static PackageInfo FindPackage(string packageId)
        {
            if (string.IsNullOrEmpty(packageId)) return null;
            try { return PackageInfo.FindForPackageName(packageId); }
            catch { return null; }
        }

        public static JObject PackageJson(string packageId)
        {
            var package = FindPackage(packageId);
            return package != null
                ? new JObject
                {
                    ["name"] = package.name,
                    ["installed"] = true,
                    ["displayName"] = package.displayName,
                    ["version"] = package.version,
                    ["source"] = package.source.ToString(),
                    ["assetPath"] = package.assetPath,
                }
                : new JObject { ["name"] = packageId, ["installed"] = false };
        }

        public static void RegisterModule(string moduleId, string packageId, params IMcpTool[] tools)
        {
            if (Bootstrap.ToolRegistry == null || OptionalModuleRegistry.Contains(moduleId)) return;
            var candidates = (tools ?? Array.Empty<IMcpTool>()).Where(tool => tool != null).ToArray();
            Bootstrap.ToolRegistry.RegisterRange(candidates);
            var registered = candidates.Select(tool => tool.Name).ToList();
            var package = FindPackage(packageId);
            OptionalModuleRegistry.Register(
                moduleId,
                packageId,
                package?.version,
                tools?.FirstOrDefault(tool => tool != null)?.GetType().Assembly.GetName().Name,
                registered);
            Bootstrap.RecordEntry($"[{moduleId}] registered {registered.Count} optional tools");
        }

        public static JObject TypeContext(string packageId, IReadOnlyDictionary<string, string[]> typeGroups)
        {
            var groups = new JObject();
            foreach (var pair in typeGroups ?? new Dictionary<string, string[]>())
            {
                var type = ResolveType(pair.Value);
                groups[pair.Key] = new JObject
                {
                    ["available"] = type != null,
                    ["type"] = type?.FullName,
                    ["assembly"] = type?.Assembly.GetName().Name,
                    ["loadedCount"] = type != null && typeof(Component).IsAssignableFrom(type)
                        ? FindSceneComponents(type).Length
                        : 0,
                };
            }
            return new JObject { ["package"] = PackageJson(packageId), ["types"] = groups };
        }

        public static Component[] FindSceneComponents(Type type)
        {
            if (type == null || !typeof(Component).IsAssignableFrom(type)) return Array.Empty<Component>();
            return Resources.FindObjectsOfTypeAll(type)
                .OfType<Component>()
                .Where(IsLoadedSceneComponent)
                .OrderBy(component => component.gameObject.scene.path, StringComparer.Ordinal)
                .ThenBy(component => ToolTargetRef.BuildHierarchyPath(component.transform), StringComparer.Ordinal)
                .ToArray();
        }

        private static bool IsLoadedSceneComponent(Component component)
        {
            if (component == null || EditorUtility.IsPersistent(component)) return false;
            var scene = component.gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded) return false;
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).handle == scene.handle) return true;
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            return prefabStage != null && prefabStage.scene.handle == scene.handle;
        }

        public static JObject ComponentJson(Component component, int maxProperties = 128)
        {
            if (component == null) return null;
            return new JObject
            {
                ["target"] = ToolTargetRef.Capture(component)?.ToJson(),
                ["gameObject"] = ToolTargetRef.Capture(component.gameObject)?.ToJson(),
                ["componentType"] = component.GetType().FullName,
                ["enabled"] = component is Behaviour behaviour ? behaviour.enabled : (JToken)null,
                ["properties"] = ReadSerialized(component, maxProperties),
            };
        }

        public static JObject AssetJson(UnityEngine.Object asset)
        {
            if (asset == null) return null;
            var path = AssetDatabase.GetAssetPath(asset);
            return new JObject
            {
                ["target"] = ToolTargetRef.Capture(asset)?.ToJson(),
                ["name"] = asset.name,
                ["type"] = asset.GetType().FullName,
                ["assetPath"] = path,
                ["guid"] = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path),
            };
        }

        public static JObject ReadSerialized(UnityEngine.Object target, int maxProperties = 128)
        {
            var result = new JObject();
            if (target == null) return result;
            var serialized = new SerializedObject(target);
            var iterator = serialized.GetIterator();
            bool enterChildren = true;
            int count = 0;
            while (count < Math.Max(1, maxProperties) && iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.name == "m_Script") continue;
                try
                {
                    result[iterator.propertyPath] = SerializedValueCodec.Read(iterator,
                        new SerializedValueCodec.ReadOptions { MaxArrayItems = 32, MaxDepth = 4 });
                }
                catch (Exception exception)
                {
                    result[iterator.propertyPath] = new JObject
                    {
                        ["unsupported"] = true,
                        ["propertyType"] = iterator.propertyType.ToString(),
                        ["error"] = exception.Message,
                    };
                }
                count++;
            }
            if (iterator.NextVisible(false)) result["_truncated"] = true;
            return result;
        }

        public static GameObject ResolveGameObject(JObject arguments, out ToolResult error)
        {
            error = null;
            var gameObject = ToolTargetResolver.ResolveGameObject(arguments, out var message);
            if (gameObject == null) error = ToolResult.Error("TARGET_NOT_FOUND", message);
            return gameObject;
        }

        public static GameObject ResolveOptionalGameObject(
            JToken targetToken,
            int? instanceId,
            string argumentName,
            out ToolResult error)
        {
            error = null;
            if (targetToken == null && (!instanceId.HasValue || instanceId.Value == 0)) return null;
            var targetRef = targetToken != null
                ? ToolTargetRef.FromJson(targetToken)
                : new ToolTargetRef { InstanceId = instanceId ?? 0 };
            var resolved = ToolTargetResolver.Resolve(targetRef, out var message);
            var gameObject = resolved as GameObject ?? (resolved as Component)?.gameObject;
            if (gameObject == null)
                error = ToolResult.Error("TARGET_NOT_FOUND", $"{argumentName}: {message ?? "expected a GameObject target"}");
            return gameObject;
        }

        public static Component ResolveComponent(JObject arguments, string[] typeNames, out ToolResult error)
        {
            error = null;
            var gameObject = ResolveGameObject(arguments, out error);
            if (gameObject == null) return null;
            var available = new List<Type>();
            foreach (var name in typeNames ?? Array.Empty<string>())
            {
                var type = ResolveType(name);
                if (type == null || !typeof(Component).IsAssignableFrom(type) || available.Contains(type)) continue;
                available.Add(type);
                var component = gameObject.GetComponent(type);
                if (component != null) return component;
            }
            if (available.Count == 0)
            {
                error = ToolResult.Error("OPTIONAL_TYPE_UNAVAILABLE", string.Join(" | ", typeNames ?? Array.Empty<string>()));
                return null;
            }
            error = ToolResult.Error("COMPONENT_NOT_FOUND",
                $"{gameObject.name} does not have any of: {string.Join(", ", available.Select(type => type.FullName))}");
            return null;
        }

        public static ToolResult ListComponents(string packageId, params string[][] typeGroups)
        {
            var items = new JArray();
            var unavailable = new JArray();
            foreach (var names in typeGroups ?? Array.Empty<string[]>())
            {
                var type = ResolveType(names);
                if (type == null)
                {
                    unavailable.Add(new JArray(names ?? Array.Empty<string>()));
                    continue;
                }
                foreach (var component in FindSceneComponents(type)) items.Add(ComponentJson(component));
            }
            return ToolResult.Ok($"{items.Count} component(s)", new JObject
            {
                ["package"] = PackageJson(packageId),
                ["components"] = items,
                ["unavailableTypes"] = unavailable,
            });
        }

        public static ToolResult CreateComponentObject(
            JObject arguments,
            string undoName,
            string[] primaryTypeNames,
            params string[][] additionalTypeNames)
        {
            var primaryType = ResolveType(primaryTypeNames);
            if (primaryType == null || !typeof(Component).IsAssignableFrom(primaryType))
                return ToolResult.Error("OPTIONAL_TYPE_UNAVAILABLE", string.Join(" | ", primaryTypeNames ?? Array.Empty<string>()));
            var additional = new List<Type>();
            foreach (var names in additionalTypeNames ?? Array.Empty<string[]>())
            {
                var type = ResolveType(names);
                if (type == null || !typeof(Component).IsAssignableFrom(type))
                    return ToolResult.Error("OPTIONAL_TYPE_UNAVAILABLE", string.Join(" | ", names ?? Array.Empty<string>()));
                if (type != primaryType && !additional.Contains(type)) additional.Add(type);
            }
            var parent = ResolveOptionalGameObject(
                arguments["parentTarget"],
                (int?)arguments["parentInstanceId"],
                "parent",
                out var parentError);
            if (parentError != null) return parentError;
            var gameObject = new GameObject((string)arguments["name"] ?? primaryType.Name);
            Component primary;
            try
            {
                using (var mutation = new MutationScope(undoName))
                {
                    mutation.RegisterCreated(gameObject);
                    if (parent != null)
                    {
                        SceneManager.MoveGameObjectToScene(gameObject, parent.scene);
                        mutation.SetParent(gameObject.transform, parent.transform);
                    }
                    foreach (var type in additional)
                        if (gameObject.GetComponent(type) == null) mutation.AddComponent(gameObject, type);
                    primary = mutation.AddComponent(gameObject, primaryType);
                    mutation.Complete();
                }
                return ToolResult.Ok($"created {primaryType.Name}", new JObject
                {
                    ["target"] = ToolTargetRef.Capture(primary)?.ToJson(),
                    ["gameObject"] = ToolTargetRef.Capture(gameObject)?.ToJson(),
                    ["component"] = ComponentJson(primary),
                });
            }
            catch (Exception exception)
            {
                if (gameObject != null) UnityEngine.Object.DestroyImmediate(gameObject);
                return ToolResult.Error("OPTIONAL_CREATE_FAILED", exception.Message);
            }
        }

        public static ToolResult AddComponent(
            JObject arguments,
            string undoName,
            string[] componentTypeNames,
            params string[][] requiredTypeNames)
        {
            var gameObject = ResolveGameObject(arguments, out var error);
            if (gameObject == null) return error;
            var componentType = ResolveType(componentTypeNames);
            if (componentType == null || !typeof(Component).IsAssignableFrom(componentType))
                return ToolResult.Error("OPTIONAL_TYPE_UNAVAILABLE", string.Join(" | ", componentTypeNames ?? Array.Empty<string>()));
            var requiredTypes = new List<Type>();
            foreach (var names in requiredTypeNames ?? Array.Empty<string[]>())
            {
                var required = ResolveType(names);
                if (required == null || !typeof(Component).IsAssignableFrom(required))
                    return ToolResult.Error("OPTIONAL_TYPE_UNAVAILABLE", string.Join(" | ", names ?? Array.Empty<string>()));
                requiredTypes.Add(required);
            }
            Component component;
            using (var mutation = new MutationScope(undoName))
            {
                foreach (var required in requiredTypes)
                    if (gameObject.GetComponent(required) == null) mutation.AddComponent(gameObject, required);
                component = gameObject.GetComponent(componentType);
                if (component == null) component = mutation.AddComponent(gameObject, componentType);
                else mutation.Touch(component);
                mutation.Complete();
            }
            return ToolResult.Ok($"configured {componentType.Name}", new JObject
            {
                ["target"] = ToolTargetRef.Capture(component)?.ToJson(),
                ["component"] = ComponentJson(component),
            });
        }

        public static ToolResult ConfigureComponent(
            JObject arguments,
            string undoName,
            string[] componentTypeNames,
            IReadOnlyDictionary<string, string[]> allowlist)
        {
            var component = ResolveComponent(arguments, componentTypeNames, out var error);
            if (component == null) return error;
            var values = arguments["properties"] as JObject;
            if (values == null || values.Count == 0) return ToolResult.Error("BAD_ARGS", "properties must not be empty");
            var serialized = new SerializedObject(component);
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var input in values.Properties())
            {
                if (allowlist == null || !allowlist.TryGetValue(input.Name, out var candidates))
                    return ToolResult.Error("PROPERTY_NOT_ALLOWED", input.Name);
                SerializedProperty property = null;
                foreach (var candidate in candidates ?? Array.Empty<string>())
                {
                    property = serialized.FindProperty(candidate);
                    if (property != null) break;
                }
                if (property == null)
                    return ToolResult.Error("PROPERTY_UNAVAILABLE", $"{input.Name} is not serialized by {component.GetType().FullName} in this package version");
                paths[input.Name] = property.propertyPath;
            }

            var changed = new JObject();
            try
            {
                using (var mutation = new MutationScope(undoName))
                {
                    mutation.Record(component);
                    serialized.Update();
                    foreach (var input in values.Properties())
                    {
                        var property = serialized.FindProperty(paths[input.Name]);
                        SerializedValueCodec.Apply(property, input.Value);
                        changed[input.Name] = input.Value.DeepClone();
                    }
                    mutation.Apply(serialized, component);
                    mutation.Complete();
                }
            }
            catch (Exception exception)
            {
                return ToolResult.Error("OPTIONAL_PROPERTY_FAILED", exception.Message);
            }
            return ToolResult.Ok($"updated {changed.Count} property value(s)", new JObject
            {
                ["target"] = ToolTargetRef.Capture(component)?.ToJson(),
                ["changed"] = changed,
                ["component"] = ComponentJson(component),
            });
        }

        public static ToolResult ValidateComponents(
            string packageId,
            IReadOnlyDictionary<string, string[]> requiredTypes,
            params Tuple<string, string>[] requiredPairs)
        {
            var issues = new JArray();
            var counts = new JObject();
            var resolved = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (var pair in requiredTypes ?? new Dictionary<string, string[]>())
            {
                var type = ResolveType(pair.Value);
                resolved[pair.Key] = type;
                if (type == null)
                {
                    counts[pair.Key] = 0;
                    issues.Add(new JObject
                    {
                        ["severity"] = "error",
                        ["code"] = "OPTIONAL_TYPE_UNAVAILABLE",
                        ["typeGroup"] = pair.Key,
                        ["candidates"] = new JArray(pair.Value ?? Array.Empty<string>()),
                    });
                }
                else if (typeof(Component).IsAssignableFrom(type)) counts[pair.Key] = FindSceneComponents(type).Length;
                else counts[pair.Key] = 0;
            }
            foreach (var relation in requiredPairs ?? Array.Empty<Tuple<string, string>>())
            {
                if (!resolved.TryGetValue(relation.Item1, out var ownerType) || ownerType == null
                    || !resolved.TryGetValue(relation.Item2, out var requiredType) || requiredType == null) continue;
                foreach (var owner in FindSceneComponents(ownerType))
                    if (owner.GetComponent(requiredType) == null)
                        issues.Add(new JObject
                        {
                            ["severity"] = "error",
                            ["code"] = "REQUIRED_COMPONENT_MISSING",
                            ["owner"] = ToolTargetRef.Capture(owner)?.ToJson(),
                            ["requiredType"] = requiredType.FullName,
                        });
            }
            return ToolResult.Ok($"{issues.Count} issue(s)", new JObject
            {
                ["valid"] = !issues.Any(token => string.Equals((string)token["severity"], "error", StringComparison.Ordinal)),
                ["package"] = PackageJson(packageId),
                ["counts"] = counts,
                ["issues"] = issues,
            });
        }
    }

    public sealed class OptionalContextTool : IMcpTool
    {
        private readonly string _name;
        private readonly string _description;
        private readonly string _packageId;
        private readonly IReadOnlyDictionary<string, string[]> _typeGroups;

        public OptionalContextTool(string name, string description, string packageId, IReadOnlyDictionary<string, string[]> typeGroups)
        {
            _name = name;
            _description = description;
            _packageId = packageId;
            _typeGroups = typeGroups;
        }

        public string Name => _name;
        public string Description => _description;
        public JObject InputSchema => OptionalToolSupport.EmptySchema();
        public bool IsWrite => false;
        public JObject OutputSchema => OptionalToolSupport.OutputSchema(new JObject
        {
            ["package"] = new JObject { ["type"] = "object" },
            ["types"] = new JObject { ["type"] = "object" },
        }, "package", "types");
        public ToolResult Execute(JObject arguments) => ToolResult.Ok(_description, OptionalToolSupport.TypeContext(_packageId, _typeGroups));
    }

    public sealed class OptionalListComponentsTool : IMcpTool
    {
        private readonly string _name;
        private readonly string _description;
        private readonly string _packageId;
        private readonly string[][] _typeGroups;

        public OptionalListComponentsTool(string name, string description, string packageId, params string[][] typeGroups)
        {
            _name = name;
            _description = description;
            _packageId = packageId;
            _typeGroups = typeGroups;
        }

        public string Name => _name;
        public string Description => _description;
        public JObject InputSchema => OptionalToolSupport.EmptySchema();
        public bool IsWrite => false;
        public JObject OutputSchema => OptionalToolSupport.OutputSchema(new JObject
        {
            ["package"] = new JObject { ["type"] = "object" },
            ["components"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            ["unavailableTypes"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "array" } },
        }, "package", "components", "unavailableTypes");
        public ToolResult Execute(JObject arguments) => OptionalToolSupport.ListComponents(_packageId, _typeGroups);
    }

    public sealed class OptionalCreateComponentTool : IMcpTool
    {
        private readonly string _name;
        private readonly string _description;
        private readonly string[] _primary;
        private readonly string[][] _additional;

        public OptionalCreateComponentTool(string name, string description, string[] primary, params string[][] additional)
        {
            _name = name;
            _description = description;
            _primary = primary;
            _additional = additional;
        }

        public string Name => _name;
        public string Description => _description;
        public JObject InputSchema => OptionalToolSupport.CreateSchema();
        public bool IsWrite => true;
        public ToolResult Execute(JObject arguments) => OptionalToolSupport.CreateComponentObject(arguments, _description, _primary, _additional);
    }

    public sealed class OptionalAddComponentTool : IMcpTool
    {
        private readonly string _name;
        private readonly string _description;
        private readonly string[] _component;
        private readonly string[][] _required;

        public OptionalAddComponentTool(string name, string description, string[] component, params string[][] required)
        {
            _name = name;
            _description = description;
            _component = component;
            _required = required;
        }

        public string Name => _name;
        public string Description => _description;
        public JObject InputSchema => OptionalToolSupport.TargetSchema();
        public bool IsWrite => true;
        public ToolResult Execute(JObject arguments) => OptionalToolSupport.AddComponent(arguments, _description, _component, _required);
    }

    public sealed class OptionalConfigureComponentTool : IMcpTool
    {
        private readonly string _name;
        private readonly string _description;
        private readonly string[] _component;
        private readonly IReadOnlyDictionary<string, string[]> _allowlist;

        public OptionalConfigureComponentTool(
            string name,
            string description,
            string[] component,
            IReadOnlyDictionary<string, string[]> allowlist)
        {
            _name = name;
            _description = description;
            _component = component;
            _allowlist = allowlist;
        }

        public string Name => _name;
        public string Description => _description;
        public JObject InputSchema => OptionalToolSupport.TargetSchema(new JObject
        {
            ["properties"] = OptionalToolSupport.PropertiesSchema(_allowlist),
        }, "properties");
        public bool IsWrite => true;
        public JObject OutputSchema => OptionalToolSupport.OutputSchema(new JObject
        {
            ["target"] = new JObject { ["type"] = new JArray { "object", "null" } },
            ["changed"] = new JObject { ["type"] = "object" },
            ["component"] = new JObject { ["type"] = "object" },
        }, "target", "changed", "component");
        public ToolResult Execute(JObject arguments) => OptionalToolSupport.ConfigureComponent(arguments, _description, _component, _allowlist);
    }

    public sealed class OptionalValidateTool : IMcpTool
    {
        private readonly string _name;
        private readonly string _description;
        private readonly string _packageId;
        private readonly IReadOnlyDictionary<string, string[]> _types;
        private readonly Tuple<string, string>[] _pairs;

        public OptionalValidateTool(
            string name,
            string description,
            string packageId,
            IReadOnlyDictionary<string, string[]> types,
            params Tuple<string, string>[] pairs)
        {
            _name = name;
            _description = description;
            _packageId = packageId;
            _types = types;
            _pairs = pairs;
        }

        public string Name => _name;
        public string Description => _description;
        public JObject InputSchema => OptionalToolSupport.EmptySchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject arguments) => OptionalToolSupport.ValidateComponents(_packageId, _types, _pairs);
    }
}
