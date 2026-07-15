using System;
using System.IO;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.PrefabTools
{
    internal static class PrefabToolSupport
    {
        public static JObject EmptySchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };

        public static JObject InstanceSchema(bool property = false) => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["componentType"] = new JObject { ["type"] = "string" },
                ["propertyPath"] = new JObject { ["type"] = "string" },
            },
            ["required"] = property
                ? new JArray { "instanceId", "propertyPath" }
                : new JArray { "instanceId" },
            ["additionalProperties"] = false,
        };

        public static GameObject ResolveGameObject(JObject args, out string error)
        {
            error = null;
            var id = (int?)args["instanceId"] ?? 0;
            var target = EditorUtility.InstanceIDToObject(id);
            var gameObject = target as GameObject ?? (target as Component)?.gameObject;
            if (gameObject == null) error = $"GameObject not found: instanceId={id}";
            return gameObject;
        }

        public static GameObject OutermostInstanceRoot(GameObject gameObject, out string error)
        {
            error = null;
            if (gameObject == null) { error = "GameObject is required"; return null; }
            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(gameObject);
            if (root == null) error = $"'{gameObject.name}' is not part of a Prefab instance";
            return root;
        }

        public static UnityEngine.Object ResolvePropertyTarget(GameObject gameObject, string componentType, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(componentType)) return gameObject;
            var type = Maicon.SceneAnalyzer.Editor.Authoring.ComponentEditor.ResolveComponentType(componentType);
            if (type == null) { error = $"component type not found: {componentType}"; return null; }
            var component = gameObject.GetComponent(type);
            if (component == null) error = $"component '{componentType}' not found on '{gameObject.name}'";
            return component;
        }

        public static JObject PrefabAssetJson(GameObject prefab)
        {
            if (prefab == null) return null;
            var path = AssetDatabase.GetAssetPath(prefab);
            return new JObject
            {
                ["name"] = prefab.name,
                ["path"] = path,
                ["guid"] = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path),
                ["target"] = ToolTargetRef.Capture(prefab)?.ToJson(),
                ["assetType"] = PrefabUtility.GetPrefabAssetType(prefab).ToString(),
            };
        }

        public static void EnsureFolder(string assetPath)
        {
            EnsureFolderRecursive(Path.GetDirectoryName(assetPath)?.Replace('\\', '/'));
        }

        private static void EnsureFolderRecursive(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
            var slash = folder.LastIndexOf('/');
            var parent = slash > 0 ? folder.Substring(0, slash) : "Assets";
            var name = slash > 0 ? folder.Substring(slash + 1) : folder;
            EnsureFolderRecursive(parent);
            AssetDatabase.CreateFolder(parent, name);
        }
    }

    public sealed class GetPrefabStageTool : IMcpTool
    {
        public string Name => "get_prefab_stage";
        public string Description => "Returns the current Prefab Mode stage, asset path, root target and dirty state.";
        public JObject InputSchema => PrefabToolSupport.EmptySchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null) return ToolResult.Ok("not in Prefab Mode", new JObject { ["open"] = false });
            return ToolResult.Ok($"Prefab Mode: {stage.assetPath}", new JObject
            {
                ["open"] = true,
                ["assetPath"] = stage.assetPath,
                ["assetGuid"] = AssetDatabase.AssetPathToGUID(stage.assetPath),
                ["sceneHandle"] = stage.scene.handle,
                ["sceneName"] = stage.scene.name,
                ["isDirty"] = stage.scene.isDirty,
                ["root"] = ToolTargetRef.Capture(stage.prefabContentsRoot)?.ToJson(),
            });
        }
    }

    public sealed class OpenPrefabStageTool : IMcpTool
    {
        public string Name => "open_prefab_stage";
        public string Description => "Opens one Prefab asset in Prefab Mode.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["prefabPath"] = new JObject { ["type"] = "string" } },
            ["required"] = new JArray { "prefabPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var path = (string)args["prefabPath"];
            var currentStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (currentStage != null && currentStage.scene.isDirty)
                return ToolResult.Error("PREFAB_STAGE_DIRTY",
                    "current Prefab Stage has unsaved changes; save or revert them before opening another Prefab");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null || PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.NotAPrefab)
                return ToolResult.Error("PREFAB_NOT_FOUND", $"Prefab asset not found: {path}");
            var stage = PrefabStageUtility.OpenPrefab(path);
            return ToolResult.Ok($"opened Prefab Mode for '{path}'", new JObject
            {
                ["asset"] = PrefabToolSupport.PrefabAssetJson(prefab),
                ["sceneHandle"] = stage.scene.handle,
                ["root"] = ToolTargetRef.Capture(stage.prefabContentsRoot)?.ToJson(),
            });
        }
    }

    public sealed class ClosePrefabStageTool : IMcpTool
    {
        public string Name => "close_prefab_stage";
        public string Description => "Returns to the Main Stage. Dirty Prefab stages are rejected to avoid modal prompts or implicit saving.";
        public JObject InputSchema => PrefabToolSupport.EmptySchema();
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null) return ToolResult.Ok("already in Main Stage", new JObject { ["closed"] = false });
            if (stage.scene.isDirty)
                return ToolResult.Error("PREFAB_STAGE_DIRTY",
                    "Prefab Stage has unsaved changes; save or revert them before closing");
            var path = stage.assetPath;
            StageUtility.GoToMainStage();
            return ToolResult.Ok($"closed Prefab Mode for '{path}'", new JObject { ["closed"] = true, ["assetPath"] = path });
        }
    }

    public sealed class ReadPrefabTool : IMcpTool
    {
        public string Name => "read_prefab";
        public string Description => "Reads a Prefab asset hierarchy and component inventory without opening Prefab Mode.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["prefabPath"] = new JObject { ["type"] = "string" },
                ["maxDepth"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 100, ["default"] = 20 },
                ["maxObjects"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 10000, ["default"] = 1000 },
            },
            ["required"] = new JArray { "prefabPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var path = (string)args["prefabPath"];
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) return ToolResult.Error("PREFAB_NOT_FOUND", $"Prefab not found: {path}");
            int remaining = (int?)args["maxObjects"] ?? 1000;
            var hierarchy = ReadNode(prefab.transform, 0, (int?)args["maxDepth"] ?? 20, ref remaining);
            return ToolResult.Ok($"read Prefab '{prefab.name}'", new JObject
            {
                ["asset"] = PrefabToolSupport.PrefabAssetJson(prefab),
                ["hierarchy"] = hierarchy,
                ["truncated"] = remaining <= 0,
            });
        }

        private static JObject ReadNode(Transform transform, int depth, int maxDepth, ref int remaining)
        {
            if (transform == null || remaining-- <= 0) return null;
            var components = new JArray(transform.GetComponents<Component>()
                .Where(component => component != null)
                .Select(component => new JObject
                {
                    ["type"] = component.GetType().FullName,
                    ["target"] = ToolTargetRef.Capture(component)?.ToJson(),
                }));
            var children = new JArray();
            if (depth < maxDepth)
            {
                for (int index = 0; index < transform.childCount && remaining > 0; index++)
                {
                    var child = ReadNode(transform.GetChild(index), depth + 1, maxDepth, ref remaining);
                    if (child != null) children.Add(child);
                }
            }
            return new JObject
            {
                ["name"] = transform.name,
                ["activeSelf"] = transform.gameObject.activeSelf,
                ["target"] = ToolTargetRef.Capture(transform.gameObject)?.ToJson(),
                ["components"] = components,
                ["children"] = children,
            };
        }
    }

    public sealed class CreatePrefabTool : IMcpTool
    {
        public string Name => "create_prefab";
        public string Description => "Creates a Prefab asset from a scene GameObject; optionally connects the source instance.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["prefabPath"] = new JObject { ["type"] = "string" },
                ["connect"] = new JObject { ["type"] = "boolean", ["default"] = true },
            },
            ["required"] = new JArray { "instanceId", "prefabPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var source = PrefabToolSupport.ResolveGameObject(args, out var error);
            if (source == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            var path = (string)args["prefabPath"];
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                return ToolResult.Error("BAD_ARGS", "prefabPath must be an Assets/*.prefab path");
            if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                return ToolResult.Error("ASSET_EXISTS", $"asset already exists: {path}");
            PrefabToolSupport.EnsureFolder(path);
            bool success;
            var prefab = (bool?)args["connect"] ?? true
                ? PrefabUtility.SaveAsPrefabAssetAndConnect(source, path, InteractionMode.AutomatedAction, out success)
                : PrefabUtility.SaveAsPrefabAsset(source, path, out success);
            if (!success || prefab == null) return ToolResult.Error("PREFAB_CREATE_FAILED", $"could not create Prefab at '{path}'");
            Undo.RegisterCreatedObjectUndo(prefab, "Create Prefab Asset");
            return ToolResult.Ok($"created Prefab '{path}'", new JObject
            {
                ["asset"] = PrefabToolSupport.PrefabAssetJson(prefab),
                ["source"] = ToolTargetRef.Capture(source)?.ToJson(),
                ["connected"] = (bool?)args["connect"] ?? true,
            });
        }
    }

    public sealed class CreatePrefabVariantTool : IMcpTool
    {
        public string Name => "create_prefab_variant";
        public string Description => "Creates a Prefab Variant from a source Prefab asset using a temporary instance that is always cleaned up.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["sourcePrefabPath"] = new JObject { ["type"] = "string" },
                ["variantPath"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "sourcePrefabPath", "variantPath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var sourcePath = (string)args["sourcePrefabPath"];
            var variantPath = (string)args["variantPath"];
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (source == null) return ToolResult.Error("PREFAB_NOT_FOUND", $"source Prefab not found: {sourcePath}");
            if (AssetDatabase.LoadMainAssetAtPath(variantPath) != null) return ToolResult.Error("ASSET_EXISTS", $"asset already exists: {variantPath}");
            PrefabToolSupport.EnsureFolder(variantPath);
            GameObject temporary = null;
            try
            {
                temporary = PrefabUtility.InstantiatePrefab(source) as GameObject;
                if (temporary == null) return ToolResult.Error("INSTANTIATE_FAILED", "could not instantiate source Prefab");
                bool success;
                var variant = PrefabUtility.SaveAsPrefabAsset(temporary, variantPath, out success);
                if (!success || variant == null) return ToolResult.Error("VARIANT_CREATE_FAILED", $"could not create variant at '{variantPath}'");
                Undo.RegisterCreatedObjectUndo(variant, "Create Prefab Variant");
                return ToolResult.Ok($"created Prefab Variant '{variantPath}'", new JObject
                {
                    ["sourcePath"] = sourcePath,
                    ["variant"] = PrefabToolSupport.PrefabAssetJson(variant),
                });
            }
            finally
            {
                if (temporary != null) UnityEngine.Object.DestroyImmediate(temporary);
            }
        }
    }

    public sealed class ListPrefabOverridesTool : IMcpTool
    {
        public string Name => "list_prefab_overrides";
        public string Description => "Lists property overrides on the outermost Prefab instance root.";
        public JObject InputSchema => PrefabToolSupport.InstanceSchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var gameObject = PrefabToolSupport.ResolveGameObject(args, out var error);
            var root = PrefabToolSupport.OutermostInstanceRoot(gameObject, out error);
            if (root == null) return ToolResult.Error("NOT_PREFAB_INSTANCE", error);
            var modifications = PrefabUtility.GetPropertyModifications(root) ?? Array.Empty<PropertyModification>();
            var array = new JArray(modifications.Select(modification => new JObject
            {
                ["propertyPath"] = modification.propertyPath,
                ["value"] = modification.value,
                ["objectReference"] = ToolTargetRef.Capture(modification.objectReference)?.ToJson(),
                ["target"] = ToolTargetRef.Capture(modification.target)?.ToJson(),
            }));
            return ToolResult.Ok($"{array.Count} property override(s)", new JObject
            {
                ["instanceRoot"] = ToolTargetRef.Capture(root)?.ToJson(),
                ["prefabAssetPath"] = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root),
                ["overrides"] = array,
            });
        }
    }

    public sealed class ApplyPrefabOverrideTool : IMcpTool
    {
        public string Name => "apply_prefab_override";
        public string Description => "Applies one SerializedProperty override from a Prefab instance to its source asset.";
        public JObject InputSchema => PrefabToolSupport.InstanceSchema(true);
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var gameObject = PrefabToolSupport.ResolveGameObject(args, out var error);
            if (gameObject == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            var root = PrefabToolSupport.OutermostInstanceRoot(gameObject, out error);
            if (root == null) return ToolResult.Error("NOT_PREFAB_INSTANCE", error);
            var target = PrefabToolSupport.ResolvePropertyTarget(gameObject, (string)args["componentType"], out error);
            if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty((string)args["propertyPath"]);
            if (property == null) return ToolResult.Error("PROPERTY_NOT_FOUND", $"property not found: {(string)args["propertyPath"]}");
            var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
            if (!AssetDatabase.IsOpenForEdit(assetPath, out var editMessage, StatusQueryOptions.ForceUpdate))
                return ToolResult.Error("ASSET_NOT_EDITABLE", editMessage);
            PrefabUtility.ApplyPropertyOverride(property, assetPath, InteractionMode.AutomatedAction);
            return ToolResult.Ok($"applied override '{property.propertyPath}'", new JObject
            {
                ["assetPath"] = assetPath,
                ["instanceRoot"] = ToolTargetRef.Capture(root)?.ToJson(),
                ["propertyPath"] = property.propertyPath,
            });
        }
    }

    public sealed class RevertPrefabOverrideTool : IMcpTool
    {
        public string Name => "revert_prefab_override";
        public string Description => "Reverts one SerializedProperty override on a Prefab instance.";
        public JObject InputSchema => PrefabToolSupport.InstanceSchema(true);
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var gameObject = PrefabToolSupport.ResolveGameObject(args, out var error);
            if (gameObject == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            var target = PrefabToolSupport.ResolvePropertyTarget(gameObject, (string)args["componentType"], out error);
            if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty((string)args["propertyPath"]);
            if (property == null) return ToolResult.Error("PROPERTY_NOT_FOUND", $"property not found: {(string)args["propertyPath"]}");
            PrefabUtility.RevertPropertyOverride(property, InteractionMode.AutomatedAction);
            return ToolResult.Ok($"reverted override '{property.propertyPath}'", new JObject
            {
                ["instance"] = ToolTargetRef.Capture(gameObject)?.ToJson(),
                ["propertyPath"] = property.propertyPath,
            });
        }
    }

    public sealed class UnpackPrefabTool : IMcpTool
    {
        public string Name => "unpack_prefab";
        public string Description => "Permanently unpacks a Prefab instance; this destructive operation requires confirmation.";
        public JObject InputSchema
        {
            get
            {
                var schema = PrefabToolSupport.InstanceSchema();
                ((JObject)schema["properties"])["completely"] = new JObject { ["type"] = "boolean", ["default"] = false };
                return schema;
            }
        }
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var gameObject = PrefabToolSupport.ResolveGameObject(args, out var error);
            var root = PrefabToolSupport.OutermostInstanceRoot(gameObject, out error);
            if (root == null) return ToolResult.Error("NOT_PREFAB_INSTANCE", error);
            var target = ToolTargetRef.Capture(root)?.ToJson();
            var mode = (bool?)args["completely"] ?? false ? PrefabUnpackMode.Completely : PrefabUnpackMode.OutermostRoot;
            PrefabUtility.UnpackPrefabInstance(root, mode, InteractionMode.AutomatedAction);
            return ToolResult.Ok($"unpacked Prefab instance '{root.name}' ({mode})", new JObject
            {
                ["previousTarget"] = target,
                ["currentTarget"] = ToolTargetRef.Capture(root)?.ToJson(),
                ["mode"] = mode.ToString(),
            });
        }
    }
}
