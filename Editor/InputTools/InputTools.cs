using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Optional;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Maicon.SceneAnalyzer.Editor.InputTools
{
    internal static class InputToolSupport
    {
        public static JObject EmptySchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };

        public static JObject AssetSchema(JObject extra = null, params string[] requiredExtra)
        {
            var properties = new JObject { ["assetPath"] = new JObject { ["type"] = "string" } };
            if (extra != null)
                foreach (var property in extra.Properties()) properties[property.Name] = property.Value.DeepClone();
            var required = new JArray { "assetPath" };
            if (requiredExtra != null)
                foreach (var propertyName in requiredExtra) required.Add(propertyName);
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false,
            };
        }

        public static bool TryPath(string value, bool mustExist, out string path, out string absolute, out string error)
        {
            path = (value ?? string.Empty).Replace('\\', '/').Trim();
            absolute = null;
            error = null;
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || !path.EndsWith(".inputactions", StringComparison.OrdinalIgnoreCase)
                || path.Contains("../") || path.EndsWith("/..", StringComparison.Ordinal))
            {
                error = "assetPath must be an Assets/*.inputactions path";
                return false;
            }
            var root = Path.GetFullPath(Bootstrap.UnityProjectRoot);
            absolute = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!absolute.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                error = "assetPath escapes the project root";
                return false;
            }
            if (mustExist && !File.Exists(absolute))
            {
                error = $"InputActionAsset source not found: {path}";
                return false;
            }
            return true;
        }

        public static JObject AssetJson(InputActionAsset asset)
        {
            if (asset == null) return null;
            var maps = new JArray(asset.actionMaps.Select(map => new JObject
            {
                ["id"] = map.id.ToString(),
                ["name"] = map.name,
                ["enabled"] = map.enabled,
                ["actions"] = new JArray(map.actions.Select(action => new JObject
                {
                    ["id"] = action.id.ToString(),
                    ["name"] = action.name,
                    ["type"] = action.type.ToString(),
                    ["expectedControlType"] = action.expectedControlType,
                    ["interactions"] = action.interactions,
                    ["processors"] = action.processors,
                    ["bindings"] = new JArray(action.bindings.Select(binding => new JObject
                    {
                        ["id"] = binding.id.ToString(),
                        ["name"] = binding.name,
                        ["path"] = binding.path,
                        ["effectivePath"] = binding.effectivePath,
                        ["interactions"] = binding.interactions,
                        ["processors"] = binding.processors,
                        ["groups"] = binding.groups,
                        ["isComposite"] = binding.isComposite,
                        ["isPartOfComposite"] = binding.isPartOfComposite,
                    })),
                })),
            }));
            var schemes = new JArray(asset.controlSchemes.Select(scheme => new JObject
            {
                ["name"] = scheme.name,
                ["bindingGroup"] = scheme.bindingGroup,
                ["devices"] = new JArray(scheme.deviceRequirements.Select(requirement => new JObject
                {
                    ["controlPath"] = requirement.controlPath,
                    ["optional"] = requirement.isOptional,
                    ["or"] = requirement.isOR,
                })),
            }));
            return new JObject
            {
                ["asset"] = new JObject
                {
                    ["name"] = asset.name,
                    ["assetPath"] = AssetDatabase.GetAssetPath(asset),
                    ["target"] = ToolTargetRef.Capture(asset)?.ToJson(),
                },
                ["maps"] = maps,
                ["controlSchemes"] = schemes,
            };
        }

        public static ToolResult MutateSource(string requestedPath, string operation, Func<InputActionAsset, JObject> mutation)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return ToolResult.Error("EDIT_MODE_REQUIRED", "InputActionAsset source edits are only persistent in Edit Mode");
            if (!TryPath(requestedPath, true, out var path, out var absolute, out var pathError))
                return ToolResult.Error("INPUT_ASSET_NOT_FOUND", pathError);
            var original = File.ReadAllText(absolute, Encoding.UTF8);
            InputActionAsset editable = null;
            try
            {
                editable = InputActionAsset.FromJson(original);
                editable.name = Path.GetFileNameWithoutExtension(path);
                var details = mutation(editable) ?? new JObject();
                var next = editable.ToJson();
                File.WriteAllText(absolute, next, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var imported = AssetDatabase.LoadAssetAtPath<InputActionAsset>(path);
                if (imported == null) throw new InvalidOperationException("Unity did not import the updated InputActionAsset");
                details["asset"] = AssetJson(imported);
                details["sourceChanged"] = !string.Equals(original, next, StringComparison.Ordinal);
                return ToolResult.Ok(operation, details);
            }
            catch (Exception exception)
            {
                try
                {
                    File.WriteAllText(absolute, original, new UTF8Encoding(false));
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                }
                catch { }
                return ToolResult.Error("INPUT_ASSET_MUTATION_FAILED", exception.Message);
            }
            finally
            {
                if (editable != null) UnityEngine.Object.DestroyImmediate(editable);
            }
        }

        public static InputActionMap FindMap(InputActionAsset asset, string name)
        {
            var map = asset.FindActionMap(name, false);
            if (map == null) throw new InvalidOperationException($"action map not found: {name}");
            return map;
        }

        public static InputAction FindAction(InputActionAsset asset, string mapName, string actionName)
        {
            var action = FindMap(asset, mapName).FindAction(actionName, false);
            if (action == null) throw new InvalidOperationException($"action not found: {mapName}/{actionName}");
            return action;
        }
    }

    public sealed class GetInputSystemContextTool : IMcpTool
    {
        public string Name => "get_input_system_context";
        public string Description => "Reports Input System package version, connected devices, action assets and loaded PlayerInput components.";
        public JObject InputSchema => InputToolSupport.EmptySchema();
        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var package = PackageInfo.FindForAssembly(typeof(InputActionAsset).Assembly);
            var assets = AssetDatabase.FindAssets("t:InputActionAsset")
                .Select(AssetDatabase.GUIDToAssetPath).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            var players = UnityEngine.Object.FindObjectsByType<PlayerInput>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            return ToolResult.Ok("Input System context", new JObject
            {
                ["package"] = package != null ? new JObject
                {
                    ["name"] = package.name,
                    ["version"] = package.version,
                    ["source"] = package.source.ToString(),
                } : null,
                ["devices"] = new JArray(InputSystem.devices.Select(device => new JObject
                {
                    ["deviceId"] = device.deviceId,
                    ["displayName"] = device.displayName,
                    ["layout"] = device.layout,
                    ["enabled"] = device.enabled,
                    ["description"] = new JObject
                    {
                        ["interfaceName"] = device.description.interfaceName,
                        ["deviceClass"] = device.description.deviceClass,
                        ["manufacturer"] = device.description.manufacturer,
                        ["product"] = device.description.product,
                        ["serial"] = device.description.serial,
                        ["version"] = device.description.version,
                        ["capabilities"] = device.description.capabilities,
                    },
                })),
                ["actionAssets"] = new JArray(assets.Select(path => new JObject
                {
                    ["assetPath"] = path,
                    ["guid"] = AssetDatabase.AssetPathToGUID(path),
                })),
                ["playerInputs"] = new JArray(players.Select(player => new JObject
                {
                    ["target"] = ToolTargetRef.Capture(player)?.ToJson(),
                    ["actions"] = player.actions != null ? AssetDatabase.GetAssetPath(player.actions) : null,
                    ["defaultActionMap"] = player.defaultActionMap,
                    ["defaultControlScheme"] = player.defaultControlScheme,
                    ["notificationBehavior"] = player.notificationBehavior.ToString(),
                })),
            });
        }
    }

    public sealed class ReadInputActionAssetTool : IMcpTool
    {
        public string Name => "read_input_action_asset";
        public string Description => "Reads maps, actions, bindings and control schemes from one InputActionAsset.";
        public JObject InputSchema => InputToolSupport.AssetSchema();
        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>((string)args["assetPath"]);
            if (asset == null) return ToolResult.Error("INPUT_ASSET_NOT_FOUND", (string)args["assetPath"]);
            return ToolResult.Ok($"InputActionAsset '{asset.name}'", InputToolSupport.AssetJson(asset));
        }
    }

    public sealed class CreateInputActionAssetTool : IMcpTool
    {
        public string Name => "create_input_action_asset";
        public string Description => "Creates a new .inputactions source asset with an optional initial action map.";
        public JObject InputSchema => InputToolSupport.AssetSchema(new JObject
        {
            ["initialMap"] = new JObject { ["type"] = "string" },
        });
        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return ToolResult.Error("EDIT_MODE_REQUIRED", "InputActionAsset creation is only supported in Edit Mode");
            if (!InputToolSupport.TryPath((string)args["assetPath"], false, out var path, out var absolute, out var error))
                return ToolResult.Error("BAD_ARGS", error);
            if (File.Exists(absolute) || AssetDatabase.LoadMainAssetAtPath(path) != null)
                return ToolResult.Error("ASSET_EXISTS", path);
            var folder = Path.GetDirectoryName(absolute);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            var asset = ScriptableObject.CreateInstance<InputActionAsset>();
            try
            {
                asset.name = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace((string)args["initialMap"])) asset.AddActionMap((string)args["initialMap"]);
                File.WriteAllText(absolute, asset.ToJson(), new UTF8Encoding(false));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var imported = AssetDatabase.LoadAssetAtPath<InputActionAsset>(path);
                if (imported == null)
                {
                    File.Delete(absolute);
                    AssetDatabase.Refresh();
                    return ToolResult.Error("INPUT_ASSET_IMPORT_FAILED", path);
                }
                return ToolResult.Ok($"created '{path}'", InputToolSupport.AssetJson(imported));
            }
            catch (Exception exception)
            {
                try { if (File.Exists(absolute)) File.Delete(absolute); } catch { }
                return ToolResult.Error("CREATE_INPUT_ASSET_FAILED", exception.Message);
            }
            finally { UnityEngine.Object.DestroyImmediate(asset); }
        }
    }

    public sealed class AddInputActionMapTool : IMcpTool
    {
        public string Name => "add_input_action_map";
        public string Description => "Adds a uniquely named action map to an InputActionAsset source file.";
        public JObject InputSchema => InputToolSupport.AssetSchema(new JObject
        {
            ["mapName"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
        }, "mapName");
        public bool IsWrite => true;

        public ToolResult Execute(JObject args) => InputToolSupport.MutateSource((string)args["assetPath"],
            $"added map '{(string)args["mapName"]}'", asset =>
            {
                if (asset.FindActionMap((string)args["mapName"], false) != null)
                    throw new InvalidOperationException("action map already exists");
                var map = asset.AddActionMap((string)args["mapName"]);
                return new JObject { ["mapId"] = map.id.ToString() };
            });
    }

    public sealed class AddInputActionTool : IMcpTool
    {
        public string Name => "add_input_action";
        public string Description => "Adds an action to a disabled map with optional first binding and expected control layout.";
        public JObject InputSchema => InputToolSupport.AssetSchema(new JObject
        {
            ["mapName"] = new JObject { ["type"] = "string" },
            ["actionName"] = new JObject { ["type"] = "string" },
            ["actionType"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Value", "Button", "PassThrough" }, ["default"] = "Value" },
            ["expectedControlType"] = new JObject { ["type"] = "string" },
            ["bindingPath"] = new JObject { ["type"] = "string" },
            ["interactions"] = new JObject { ["type"] = "string" },
            ["processors"] = new JObject { ["type"] = "string" },
            ["groups"] = new JObject { ["type"] = "string" },
        }, "mapName", "actionName");
        public bool IsWrite => true;

        public ToolResult Execute(JObject args) => InputToolSupport.MutateSource((string)args["assetPath"],
            $"added action '{(string)args["mapName"]}/{(string)args["actionName"]}'", asset =>
            {
                var map = InputToolSupport.FindMap(asset, (string)args["mapName"]);
                if (map.FindAction((string)args["actionName"], false) != null)
                    throw new InvalidOperationException("action already exists");
                if (!Enum.TryParse((string)args["actionType"] ?? "Value", true, out InputActionType actionType))
                    throw new InvalidOperationException("unknown actionType");
                var action = map.AddAction(
                    (string)args["actionName"], actionType, (string)args["bindingPath"],
                    (string)args["interactions"], (string)args["processors"], (string)args["groups"],
                    (string)args["expectedControlType"]);
                return new JObject { ["actionId"] = action.id.ToString() };
            });
    }

    public sealed class AddInputBindingTool : IMcpTool
    {
        public string Name => "add_input_binding";
        public string Description => "Adds one control-path binding to an existing Input Action.";
        public JObject InputSchema => InputToolSupport.AssetSchema(new JObject
        {
            ["mapName"] = new JObject { ["type"] = "string" },
            ["actionName"] = new JObject { ["type"] = "string" },
            ["bindingPath"] = new JObject { ["type"] = "string" },
            ["interactions"] = new JObject { ["type"] = "string" },
            ["processors"] = new JObject { ["type"] = "string" },
            ["groups"] = new JObject { ["type"] = "string" },
        }, "mapName", "actionName", "bindingPath");
        public bool IsWrite => true;

        public ToolResult Execute(JObject args) => InputToolSupport.MutateSource((string)args["assetPath"],
            $"added binding '{(string)args["bindingPath"]}'", asset =>
            {
                var action = InputToolSupport.FindAction(asset, (string)args["mapName"], (string)args["actionName"]);
                var syntax = action.AddBinding((string)args["bindingPath"]);
                if (args["interactions"] != null) syntax.WithInteractions((string)args["interactions"]);
                if (args["processors"] != null) syntax.WithProcessors((string)args["processors"]);
                if (args["groups"] != null) syntax.WithGroups((string)args["groups"]);
                return new JObject { ["bindingCount"] = action.bindings.Count };
            });
    }

    public sealed class AddInputControlSchemeTool : IMcpTool
    {
        public string Name => "add_input_control_scheme";
        public string Description => "Adds a control scheme with required and optional device control paths.";
        public JObject InputSchema => InputToolSupport.AssetSchema(new JObject
        {
            ["schemeName"] = new JObject { ["type"] = "string" },
            ["bindingGroup"] = new JObject { ["type"] = "string" },
            ["requiredDevices"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["maxItems"] = 32 },
            ["optionalDevices"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["maxItems"] = 32 },
        }, "schemeName");
        public bool IsWrite => true;

        public ToolResult Execute(JObject args) => InputToolSupport.MutateSource((string)args["assetPath"],
            $"added control scheme '{(string)args["schemeName"]}'", asset =>
            {
                if (asset.controlSchemes.Any(scheme => string.Equals(scheme.name, (string)args["schemeName"], StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("control scheme already exists");
                var syntax = asset.AddControlScheme((string)args["schemeName"]);
                if (!string.IsNullOrWhiteSpace((string)args["bindingGroup"])) syntax = syntax.WithBindingGroup((string)args["bindingGroup"]);
                foreach (var path in ((JArray)args["requiredDevices"] ?? new JArray()).Values<string>()) syntax = syntax.WithRequiredDevice(path);
                foreach (var path in ((JArray)args["optionalDevices"] ?? new JArray()).Values<string>()) syntax = syntax.WithOptionalDevice(path);
                return new JObject { ["controlSchemeCount"] = asset.controlSchemes.Count };
            });
    }

    public sealed class ConfigurePlayerInputTool : IMcpTool
    {
        public string Name => "configure_player_input";
        public string Description => "Adds/configures PlayerInput on a GameObject using an existing InputActionAsset.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["assetPath"] = new JObject { ["type"] = "string" },
                ["defaultActionMap"] = new JObject { ["type"] = "string" },
                ["defaultControlScheme"] = new JObject { ["type"] = "string" },
                ["notificationBehavior"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray { "SendMessages", "BroadcastMessages", "InvokeUnityEvents", "InvokeCSharpEvents" },
                    ["default"] = "SendMessages",
                },
                ["neverAutoSwitchControlSchemes"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["required"] = new JArray { "instanceId", "assetPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return ToolResult.Error("EDIT_MODE_REQUIRED", "PlayerInput authoring is only persistent in Edit Mode");
            var gameObject = ToolTargetResolver.ResolveGameObject(args, out var resolveError);
            if (gameObject == null) return ToolResult.Error("TARGET_NOT_FOUND", resolveError);
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>((string)args["assetPath"]);
            if (asset == null) return ToolResult.Error("INPUT_ASSET_NOT_FOUND", (string)args["assetPath"]);
            if (!string.IsNullOrEmpty((string)args["defaultActionMap"])
                && asset.FindActionMap((string)args["defaultActionMap"], false) == null)
                return ToolResult.Error("ACTION_MAP_NOT_FOUND", (string)args["defaultActionMap"]);
            if (!string.IsNullOrEmpty((string)args["defaultControlScheme"])
                && !asset.controlSchemes.Any(scheme => string.Equals(scheme.name, (string)args["defaultControlScheme"], StringComparison.OrdinalIgnoreCase)))
                return ToolResult.Error("CONTROL_SCHEME_NOT_FOUND", (string)args["defaultControlScheme"]);
            if (!Enum.TryParse((string)args["notificationBehavior"] ?? "SendMessages", true, out PlayerNotifications notifications))
                return ToolResult.Error("BAD_NOTIFICATION_BEHAVIOR", (string)args["notificationBehavior"]);

            var player = gameObject.GetComponent<PlayerInput>();
            if (player == null) player = Undo.AddComponent<PlayerInput>(gameObject);
            using (var mutation = new MutationScope("Configure PlayerInput"))
            {
                mutation.Record(player);
                player.actions = asset;
                player.defaultActionMap = (string)args["defaultActionMap"];
                player.defaultControlScheme = (string)args["defaultControlScheme"];
                player.notificationBehavior = notifications;
                player.neverAutoSwitchControlSchemes = (bool?)args["neverAutoSwitchControlSchemes"] ?? false;
                mutation.Complete();
            }
            return ToolResult.Ok("configured PlayerInput", new JObject
            {
                ["target"] = ToolTargetRef.Capture(player)?.ToJson(),
                ["actions"] = AssetDatabase.GetAssetPath(player.actions),
                ["defaultActionMap"] = player.defaultActionMap,
                ["defaultControlScheme"] = player.defaultControlScheme,
                ["notificationBehavior"] = player.notificationBehavior.ToString(),
            });
        }
    }

    public sealed class ValidateInputSetupTool : IMcpTool
    {
        public string Name => "validate_input_setup";
        public string Description => "Validates InputActionAssets and PlayerInput references without changing the project.";
        public JObject InputSchema => InputToolSupport.EmptySchema();
        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var issues = new JArray();
            var assetPaths = AssetDatabase.FindAssets("t:InputActionAsset").Select(AssetDatabase.GUIDToAssetPath).ToArray();
            foreach (var path in assetPaths)
            {
                var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(path);
                if (asset == null)
                {
                    issues.Add(new JObject { ["severity"] = "error", ["code"] = "INPUT_ASSET_IMPORT_FAILED", ["assetPath"] = path });
                    continue;
                }
                if (asset.actionMaps.Count == 0)
                    issues.Add(new JObject { ["severity"] = "warning", ["code"] = "NO_ACTION_MAPS", ["assetPath"] = path });
                foreach (var map in asset.actionMaps)
                {
                    if (map.actions.Count == 0)
                        issues.Add(new JObject { ["severity"] = "warning", ["code"] = "EMPTY_ACTION_MAP", ["assetPath"] = path, ["map"] = map.name });
                    foreach (var action in map.actions)
                        if (action.bindings.Count == 0)
                            issues.Add(new JObject { ["severity"] = "warning", ["code"] = "ACTION_WITHOUT_BINDING", ["assetPath"] = path, ["action"] = map.name + "/" + action.name });
                }
            }
            foreach (var player in UnityEngine.Object.FindObjectsByType<PlayerInput>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (player.actions == null)
                    issues.Add(new JObject { ["severity"] = "error", ["code"] = "PLAYER_INPUT_WITHOUT_ACTIONS", ["target"] = ToolTargetRef.Capture(player)?.ToJson() });
                else if (!string.IsNullOrEmpty(player.defaultActionMap) && player.actions.FindActionMap(player.defaultActionMap, false) == null)
                    issues.Add(new JObject { ["severity"] = "error", ["code"] = "DEFAULT_MAP_MISSING", ["target"] = ToolTargetRef.Capture(player)?.ToJson(), ["map"] = player.defaultActionMap });
            }

            var eventSystemType = OptionalToolSupport.ResolveType("UnityEngine.EventSystems.EventSystem");
            var inputSystemUiModuleType = OptionalToolSupport.ResolveType("UnityEngine.InputSystem.UI.InputSystemUIInputModule");
            var legacyInputModuleType = OptionalToolSupport.ResolveType("UnityEngine.EventSystems.StandaloneInputModule");
            var eventSystems = OptionalToolSupport.FindSceneComponents(eventSystemType);
            var inputSystemUiModules = OptionalToolSupport.FindSceneComponents(inputSystemUiModuleType);
            var legacyInputModules = OptionalToolSupport.FindSceneComponents(legacyInputModuleType);
            foreach (var legacyInputModule in legacyInputModules)
                issues.Add(new JObject
                {
                    ["severity"] = "warning",
                    ["code"] = "LEGACY_INPUT_MODULE_WITH_INPUT_SYSTEM",
                    ["target"] = ToolTargetRef.Capture(legacyInputModule)?.ToJson(),
                });
            if (eventSystems.Length > 0 && inputSystemUiModules.Length == 0)
                issues.Add(new JObject
                {
                    ["severity"] = "error",
                    ["code"] = "INPUT_SYSTEM_UI_MODULE_MISSING",
                    ["eventSystemCount"] = eventSystems.Length,
                });

            return ToolResult.Ok($"{issues.Count} input issue(s)", new JObject
            {
                ["valid"] = !issues.Any(token => string.Equals((string)token["severity"], "error", StringComparison.Ordinal)),
                ["issues"] = issues,
                ["assetCount"] = assetPaths.Length,
                ["eventSystemCount"] = eventSystems.Length,
                ["inputSystemUiModuleCount"] = inputSystemUiModules.Length,
                ["legacyInputModuleCount"] = legacyInputModules.Length,
            });
        }
    }
}
