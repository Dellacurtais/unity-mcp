using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.PrefabTools
{
    internal static class PrefabToolSupport
    {
        public static JObject EmptySchema() => ObjectSchema(new JObject());

        public static JObject InstanceSchema(bool property = false, bool applyTarget = false)
        {
            var properties = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer", ["not"] = new JObject { ["const"] = 0 } },
                ["target"] = SchemaHelpers.TargetRefSchema,
                ["componentType"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["propertyPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
            };
            if (applyTarget)
                properties["prefabAssetPath"] = PrefabPathSchema("Optional destination Prefab for nested overrides; defaults to the nearest Prefab asset.");
            var schema = ObjectSchema(properties, property ? new[] { "propertyPath" } : Array.Empty<string>());
            schema["oneOf"] = new JArray
            {
                new JObject { ["required"] = new JArray { "instanceId" }, ["not"] = new JObject { ["required"] = new JArray { "target" } } },
                new JObject { ["required"] = new JArray { "target" }, ["not"] = new JObject { ["required"] = new JArray { "instanceId" } } },
            };
            return schema;
        }

        public static JObject PrefabPathSchema(string description = null) => new JObject
        {
            ["type"] = "string",
            ["pattern"] = "^Assets/(?!.*(?:^|/)\\.{1,2}(?:/|$))(?!.*//)(?!.*\\\\).+\\.prefab$",
            ["description"] = description ?? "Canonical project-relative Prefab path under Assets/.",
        };

        public static JObject AssetSchema => ObjectSchema(new JObject
        {
            ["name"] = new JObject { ["type"] = "string" },
            ["path"] = new JObject { ["type"] = "string" },
            ["guid"] = new JObject { ["type"] = "string" },
            ["target"] = SchemaHelpers.TargetRefSchema,
            ["assetType"] = new JObject { ["type"] = "string" },
        }, "name", "path", "guid", "target", "assetType");

        public static JObject GetStageOutput => ObjectSchema(new JObject
        {
            ["open"] = new JObject { ["type"] = "boolean" },
            ["assetPath"] = new JObject { ["type"] = "string" },
            ["assetGuid"] = new JObject { ["type"] = "string" },
            ["sceneHandle"] = new JObject { ["type"] = "integer" },
            ["sceneName"] = new JObject { ["type"] = "string" },
            ["isDirty"] = new JObject { ["type"] = "boolean" },
            ["root"] = SchemaHelpers.TargetRefSchema,
        }, "open");

        public static JObject OpenStageOutput => ObjectSchema(new JObject
        {
            ["asset"] = AssetSchema,
            ["sceneHandle"] = new JObject { ["type"] = "integer" },
            ["root"] = SchemaHelpers.TargetRefSchema,
        }, "asset", "sceneHandle", "root");

        public static JObject CloseStageOutput => ObjectSchema(new JObject
        {
            ["closed"] = new JObject { ["type"] = "boolean" },
            ["assetPath"] = new JObject { ["type"] = "string" },
        }, "closed");

        public static JObject ReadOutput => ObjectSchema(new JObject
        {
            ["asset"] = AssetSchema,
            ["hierarchy"] = new JObject { ["type"] = "object" },
            ["emittedObjectCount"] = new JObject { ["type"] = "integer", ["minimum"] = 1 },
            ["totalObjectCount"] = new JObject { ["type"] = "integer", ["minimum"] = 1 },
            ["truncated"] = new JObject { ["type"] = "boolean" },
        }, "asset", "hierarchy", "emittedObjectCount", "totalObjectCount", "truncated");

        public static JObject CreateOutput => ObjectSchema(new JObject
        {
            ["asset"] = AssetSchema,
            ["source"] = SchemaHelpers.TargetRefSchema,
            ["connected"] = new JObject { ["type"] = "boolean" },
        }, "asset", "source", "connected");

        public static JObject VariantOutput => ObjectSchema(new JObject
        {
            ["sourcePath"] = new JObject { ["type"] = "string" },
            ["variant"] = AssetSchema,
        }, "sourcePath", "variant");

        public static JObject OverridesOutput => ObjectSchema(new JObject
        {
            ["instanceRoot"] = SchemaHelpers.TargetRefSchema,
            ["prefabAssetPath"] = new JObject { ["type"] = "string" },
            ["count"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
            ["overrides"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
        }, "instanceRoot", "prefabAssetPath", "count", "overrides");

        public static JObject ApplyOutput => ObjectSchema(new JObject
        {
            ["assetPath"] = new JObject { ["type"] = "string" },
            ["instanceRoot"] = SchemaHelpers.TargetRefSchema,
            ["propertyPath"] = new JObject { ["type"] = "string" },
        }, "assetPath", "instanceRoot", "propertyPath");

        public static JObject RevertOutput => ObjectSchema(new JObject
        {
            ["instance"] = SchemaHelpers.TargetRefSchema,
            ["propertyPath"] = new JObject { ["type"] = "string" },
        }, "instance", "propertyPath");

        public static JObject UnpackOutput => ObjectSchema(new JObject
        {
            ["previousTarget"] = SchemaHelpers.TargetRefSchema,
            ["currentTarget"] = SchemaHelpers.TargetRefSchema,
            ["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "OutermostRoot", "Completely" } },
        }, "previousTarget", "currentTarget", "mode");

        public static GameObject ResolveGameObject(JObject args, out string error)
            => ToolTargetResolver.ResolveGameObject(args, out error);

        public static bool TryResolveSceneSource(JObject args, out GameObject source, out string error)
        {
            source = ResolveGameObject(args, out error);
            if (source == null) return false;
            if (PrefabStageUtility.GetPrefabStage(source) != null || EditorUtility.IsPersistent(source) || !source.scene.IsValid() || !source.scene.isLoaded)
            {
                error = "INVALID_SOURCE: source must be a GameObject in a loaded Main Stage scene";
                source = null;
                return false;
            }
            if (PrefabUtility.IsPartOfPrefabInstance(source) && PrefabUtility.GetOutermostPrefabInstanceRoot(source) != source)
            {
                error = "INVALID_SOURCE: a child inside a Prefab instance cannot be saved as a Prefab; use the outermost instance root";
                source = null;
                return false;
            }
            return true;
        }

        public static GameObject OutermostInstanceRoot(GameObject gameObject, out string error)
        {
            error = null;
            if (gameObject == null) { error = "GameObject is required"; return null; }
            if (EditorUtility.IsPersistent(gameObject) || PrefabUtility.IsPartOfPrefabAsset(gameObject))
            {
                error = "target must be a Prefab instance in a loaded scene";
                return null;
            }
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

        public static bool TryGetApplyAssetPath(UnityEngine.Object target, string requestedPath, out string assetPath, out string error)
        {
            error = null;
            assetPath = requestedPath;
            var gameObject = target as GameObject ?? (target as Component)?.gameObject;
            if (string.IsNullOrEmpty(assetPath)) assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
            else if (!TryNormalizePrefabPath(assetPath, out assetPath, out error)) return false;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null || PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.NotAPrefab)
            {
                error = $"Prefab asset not found: {assetPath}";
                return false;
            }
            if (PrefabUtility.GetCorrespondingObjectFromSourceAtPath(target, assetPath) == null)
            {
                error = $"'{assetPath}' is not a valid apply target for the selected property; choose a source Prefab in the nested instance chain";
                return false;
            }
            return true;
        }

        public static JObject PrefabAssetJson(GameObject prefab)
        {
            if (prefab == null) return null;
            var path = AssetDatabase.GetAssetPath(prefab);
            return new JObject
            {
                ["name"] = prefab.name,
                ["path"] = path,
                ["guid"] = string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path),
                ["target"] = ToolTargetRef.Capture(prefab)?.ToJson(),
                ["assetType"] = PrefabUtility.GetPrefabAssetType(prefab).ToString(),
            };
        }

        public static bool TryNormalizePrefabPath(string rawPath, out string path, out string error)
        {
            path = null;
            error = null;
            if (string.IsNullOrEmpty(rawPath) || !string.Equals(rawPath, rawPath.Trim(), StringComparison.Ordinal)
                || rawPath.IndexOf('\\') >= 0 || !rawPath.StartsWith("Assets/", StringComparison.Ordinal)
                || !rawPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                error = "path must be a canonical Assets/*.prefab path using forward slashes";
                return false;
            }
            var segments = rawPath.Split('/');
            var invalid = Path.GetInvalidFileNameChars();
            if (segments.Length < 2 || segments.Any(segment => string.IsNullOrWhiteSpace(segment)
                || segment == "." || segment == ".." || segment.IndexOfAny(invalid) >= 0))
            {
                error = "path contains an empty, dot, whitespace-only, or invalid segment";
                return false;
            }
            path = string.Join("/", segments);
            return true;
        }

        public static bool EnsureFolder(string assetPath, List<string> createdFolders, out string error)
        {
            error = null;
            return EnsureFolderRecursive(Path.GetDirectoryName(assetPath)?.Replace('\\', '/'), createdFolders, out error);
        }

        public static void CleanupFailedWrite(string assetPath, List<string> createdFolders)
        {
            if (!string.IsNullOrEmpty(assetPath) && AssetDatabase.LoadMainAssetAtPath(assetPath) != null)
                AssetDatabase.DeleteAsset(assetPath);
            if (createdFolders == null) return;
            for (var index = createdFolders.Count - 1; index >= 0; index--)
                if (AssetDatabase.IsValidFolder(createdFolders[index]) && AssetDatabase.FindAssets(string.Empty, new[] { createdFolders[index] }).Length == 0)
                    AssetDatabase.DeleteAsset(createdFolders[index]);
        }

        private static bool EnsureFolderRecursive(string folder, List<string> createdFolders, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return true;
            var slash = folder.LastIndexOf('/');
            var parent = slash > 0 ? folder.Substring(0, slash) : "Assets";
            var name = slash > 0 ? folder.Substring(slash + 1) : folder;
            if (!EnsureFolderRecursive(parent, createdFolders, out error)) return false;
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, name)))
            {
                error = $"could not create folder '{folder}'";
                return false;
            }
            createdFolders?.Add(folder);
            return true;
        }

        public static ToolDescriptor DestructiveDescriptor(string name, JObject outputSchema, McpCapability capabilities)
            => new ToolDescriptor
            {
                Name = name,
                Category = "prefab",
                Capabilities = capabilities | McpCapability.Destructive,
                Risk = McpRiskLevel.High,
                ConfirmationPolicy = McpConfirmationPolicy.Required,
                EditorMode = McpEditorMode.EditModeOnly,
                IsIdempotent = false,
                OutputSchema = outputSchema,
            };

        private static JObject ObjectSchema(JObject properties, params string[] required)
        {
            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = false,
            };
            if (required != null && required.Length > 0) schema["required"] = new JArray(required);
            return schema;
        }
    }

    public sealed class GetPrefabStageTool : IMcpTool
    {
        public string Name => "get_prefab_stage";
        public string Description => "Returns the current Prefab Mode stage, asset path, root target and dirty state.";
        public JObject InputSchema => PrefabToolSupport.EmptySchema();
        public JObject OutputSchema => PrefabToolSupport.GetStageOutput;
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
                ["sceneHandle"] = (int)stage.scene.handle,
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
            ["properties"] = new JObject { ["prefabPath"] = PrefabToolSupport.PrefabPathSchema() },
            ["required"] = new JArray { "prefabPath" },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => PrefabToolSupport.OpenStageOutput;
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            if (!PrefabToolSupport.TryNormalizePrefabPath((string)args["prefabPath"], out var path, out var pathError))
                return ToolResult.Error("BAD_ARGS", pathError);
            var currentStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (currentStage != null && currentStage.scene.isDirty)
                return ToolResult.Error("PREFAB_STAGE_DIRTY",
                    "current Prefab Stage has unsaved changes; save or revert them before opening another Prefab");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null || PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.NotAPrefab)
                return ToolResult.Error("PREFAB_NOT_FOUND", $"Prefab asset not found: {path}");
            PrefabStage stage;
            try { stage = PrefabStageUtility.OpenPrefab(path); }
            catch (Exception exception) { return ToolResult.Error("PREFAB_STAGE_OPEN_FAILED", exception.Message); }
            if (stage == null || stage.prefabContentsRoot == null)
                return ToolResult.Error("PREFAB_STAGE_OPEN_FAILED", $"Unity did not open Prefab Mode for '{path}'");
            return ToolResult.Ok($"opened Prefab Mode for '{path}'", new JObject
            {
                ["asset"] = PrefabToolSupport.PrefabAssetJson(prefab),
                ["sceneHandle"] = (int)stage.scene.handle,
                ["root"] = ToolTargetRef.Capture(stage.prefabContentsRoot)?.ToJson(),
            });
        }
    }

    public sealed class ClosePrefabStageTool : IMcpTool
    {
        public string Name => "close_prefab_stage";
        public string Description => "Returns to the Main Stage. Dirty Prefab stages are rejected to avoid modal prompts or implicit saving.";
        public JObject InputSchema => PrefabToolSupport.EmptySchema();
        public JObject OutputSchema => PrefabToolSupport.CloseStageOutput;
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
                ["prefabPath"] = PrefabToolSupport.PrefabPathSchema(),
                ["maxDepth"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 100, ["default"] = 20 },
                ["maxObjects"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 10000, ["default"] = 1000 },
            },
            ["required"] = new JArray { "prefabPath" },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => PrefabToolSupport.ReadOutput;
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            if (!PrefabToolSupport.TryNormalizePrefabPath((string)args["prefabPath"], out var path, out var pathError))
                return ToolResult.Error("BAD_ARGS", pathError);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null || PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.NotAPrefab)
                return ToolResult.Error("PREFAB_NOT_FOUND", $"Prefab not found: {path}");
            var totalObjectCount = prefab.GetComponentsInChildren<Transform>(true).Length;
            int remaining = (int?)args["maxObjects"] ?? 1000;
            var hierarchy = ReadNode(prefab.transform, 0, (int?)args["maxDepth"] ?? 20, ref remaining);
            var emittedObjectCount = ((int?)args["maxObjects"] ?? 1000) - remaining;
            return ToolResult.Ok($"read Prefab '{prefab.name}'", new JObject
            {
                ["asset"] = PrefabToolSupport.PrefabAssetJson(prefab),
                ["hierarchy"] = hierarchy,
                ["emittedObjectCount"] = emittedObjectCount,
                ["totalObjectCount"] = totalObjectCount,
                ["truncated"] = emittedObjectCount < totalObjectCount,
            });
        }

        private static JObject ReadNode(Transform transform, int depth, int maxDepth, ref int remaining)
        {
            if (transform == null || remaining <= 0) return null;
            remaining--;
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
        public JObject InputSchema
        {
            get
            {
                var schema = PrefabToolSupport.InstanceSchema();
                var properties = (JObject)schema["properties"];
                properties["prefabPath"] = PrefabToolSupport.PrefabPathSchema();
                properties["connect"] = new JObject { ["type"] = "boolean", ["default"] = true };
                schema["required"] = new JArray { "prefabPath" };
                return schema;
            }
        }
        public JObject OutputSchema => PrefabToolSupport.CreateOutput;
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            if (!PrefabToolSupport.TryResolveSceneSource(args, out var source, out var error))
                return ToolResult.Error("INVALID_SOURCE", error);
            if (!PrefabToolSupport.TryNormalizePrefabPath((string)args["prefabPath"], out var path, out var pathError))
                return ToolResult.Error("BAD_ARGS", pathError);
            if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                return ToolResult.Error("ASSET_EXISTS", $"asset already exists: {path}");
            var createdFolders = new List<string>();
            if (!PrefabToolSupport.EnsureFolder(path, createdFolders, out var folderError))
            {
                PrefabToolSupport.CleanupFailedWrite(null, createdFolders);
                return ToolResult.Error("FOLDER_CREATE_FAILED", folderError);
            }
            try
            {
                bool success;
                var prefab = (bool?)args["connect"] ?? true
                    ? PrefabUtility.SaveAsPrefabAssetAndConnect(source, path, InteractionMode.AutomatedAction, out success)
                    : PrefabUtility.SaveAsPrefabAsset(source, path, out success);
                if (!success || prefab == null)
                {
                    PrefabToolSupport.CleanupFailedWrite(path, createdFolders);
                    return ToolResult.Error("PREFAB_CREATE_FAILED", $"could not create Prefab at '{path}'");
                }
                return ToolResult.Ok($"created Prefab '{path}'", new JObject
                {
                    ["asset"] = PrefabToolSupport.PrefabAssetJson(prefab),
                    ["source"] = ToolTargetRef.Capture(source)?.ToJson(),
                    ["connected"] = (bool?)args["connect"] ?? true,
                });
            }
            catch (Exception exception)
            {
                PrefabToolSupport.CleanupFailedWrite(path, createdFolders);
                return ToolResult.Error("PREFAB_CREATE_FAILED", exception.Message);
            }
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
                ["sourcePrefabPath"] = PrefabToolSupport.PrefabPathSchema(),
                ["variantPath"] = PrefabToolSupport.PrefabPathSchema(),
            },
            ["required"] = new JArray { "sourcePrefabPath", "variantPath" },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => PrefabToolSupport.VariantOutput;
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            if (!PrefabToolSupport.TryNormalizePrefabPath((string)args["sourcePrefabPath"], out var sourcePath, out var sourcePathError))
                return ToolResult.Error("BAD_ARGS", sourcePathError);
            if (!PrefabToolSupport.TryNormalizePrefabPath((string)args["variantPath"], out var variantPath, out var variantPathError))
                return ToolResult.Error("BAD_ARGS", variantPathError);
            if (string.Equals(sourcePath, variantPath, StringComparison.OrdinalIgnoreCase))
                return ToolResult.Error("BAD_ARGS", "variantPath must differ from sourcePrefabPath");
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (source == null || PrefabUtility.GetPrefabAssetType(source) == PrefabAssetType.NotAPrefab)
                return ToolResult.Error("PREFAB_NOT_FOUND", $"source Prefab not found: {sourcePath}");
            if (AssetDatabase.LoadMainAssetAtPath(variantPath) != null)
                return ToolResult.Error("ASSET_EXISTS", $"asset already exists: {variantPath}");
            var createdFolders = new List<string>();
            if (!PrefabToolSupport.EnsureFolder(variantPath, createdFolders, out var folderError))
            {
                PrefabToolSupport.CleanupFailedWrite(null, createdFolders);
                return ToolResult.Error("FOLDER_CREATE_FAILED", folderError);
            }
            GameObject temporary = null;
            var previewScene = default(Scene);
            try
            {
                previewScene = EditorSceneManager.NewPreviewScene();
                temporary = PrefabUtility.InstantiatePrefab(source, previewScene) as GameObject;
                if (temporary == null)
                {
                    PrefabToolSupport.CleanupFailedWrite(variantPath, createdFolders);
                    return ToolResult.Error("INSTANTIATE_FAILED", "could not instantiate source Prefab in an isolated preview scene");
                }
                bool success;
                var variant = PrefabUtility.SaveAsPrefabAsset(temporary, variantPath, out success);
                if (!success || variant == null)
                {
                    PrefabToolSupport.CleanupFailedWrite(variantPath, createdFolders);
                    return ToolResult.Error("VARIANT_CREATE_FAILED", $"could not create variant at '{variantPath}'");
                }
                return ToolResult.Ok($"created Prefab Variant '{variantPath}'", new JObject
                {
                    ["sourcePath"] = sourcePath,
                    ["variant"] = PrefabToolSupport.PrefabAssetJson(variant),
                });
            }
            catch (Exception exception)
            {
                PrefabToolSupport.CleanupFailedWrite(variantPath, createdFolders);
                return ToolResult.Error("VARIANT_CREATE_FAILED", exception.Message);
            }
            finally
            {
                if (temporary != null) UnityEngine.Object.DestroyImmediate(temporary);
                if (previewScene.IsValid()) EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }
    }

    public sealed class ListPrefabOverridesTool : IMcpTool
    {
        public string Name => "list_prefab_overrides";
        public string Description => "Lists property overrides on the outermost Prefab instance root.";
        public JObject InputSchema => PrefabToolSupport.InstanceSchema();
        public JObject OutputSchema => PrefabToolSupport.OverridesOutput;
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
                ["count"] = array.Count,
                ["overrides"] = array,
            });
        }
    }

    public sealed class ApplyPrefabOverrideTool : IMcpTool
    {
        public string Name => "apply_prefab_override";
        public string Description => "Applies one SerializedProperty override from a Prefab instance to a source Prefab asset. Nested instances may require prefabAssetPath.";
        public JObject InputSchema => PrefabToolSupport.InstanceSchema(true, true);
        public JObject OutputSchema => PrefabToolSupport.ApplyOutput;
        public ToolDescriptor Descriptor => PrefabToolSupport.DestructiveDescriptor(Name, OutputSchema, McpCapability.SceneWrite | McpCapability.AssetWrite);
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
            if (!PrefabToolSupport.TryGetApplyAssetPath(target, (string)args["prefabAssetPath"], out var assetPath, out var assetError))
                return ToolResult.Error("INVALID_PREFAB_TARGET", assetError);
            if (!AssetDatabase.IsOpenForEdit(assetPath, out var editMessage, StatusQueryOptions.ForceUpdate))
                return ToolResult.Error("ASSET_NOT_EDITABLE", editMessage);
            try { PrefabUtility.ApplyPropertyOverride(property, assetPath, InteractionMode.AutomatedAction); }
            catch (Exception exception) { return ToolResult.Error("APPLY_OVERRIDE_FAILED", exception.Message); }
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
        public string Description => "Reverts one SerializedProperty override on a Prefab instance. AutomatedAction does not record Undo, so confirmation is required.";
        public JObject InputSchema => PrefabToolSupport.InstanceSchema(true);
        public JObject OutputSchema => PrefabToolSupport.RevertOutput;
        public ToolDescriptor Descriptor => PrefabToolSupport.DestructiveDescriptor(Name, OutputSchema, McpCapability.SceneWrite);
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
            try { PrefabUtility.RevertPropertyOverride(property, InteractionMode.AutomatedAction); }
            catch (Exception exception) { return ToolResult.Error("REVERT_OVERRIDE_FAILED", exception.Message); }
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
        public string Description => "Permanently unpacks a Prefab instance; AutomatedAction does not record Undo, so confirmation is required.";
        public JObject InputSchema
        {
            get
            {
                var schema = PrefabToolSupport.InstanceSchema();
                ((JObject)schema["properties"])["completely"] = new JObject { ["type"] = "boolean", ["default"] = false };
                return schema;
            }
        }
        public JObject OutputSchema => PrefabToolSupport.UnpackOutput;
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var gameObject = PrefabToolSupport.ResolveGameObject(args, out var error);
            var root = PrefabToolSupport.OutermostInstanceRoot(gameObject, out error);
            if (root == null) return ToolResult.Error("NOT_PREFAB_INSTANCE", error);
            var target = ToolTargetRef.Capture(root)?.ToJson();
            var mode = (bool?)args["completely"] ?? false ? PrefabUnpackMode.Completely : PrefabUnpackMode.OutermostRoot;
            try { PrefabUtility.UnpackPrefabInstance(root, mode, InteractionMode.AutomatedAction); }
            catch (Exception exception) { return ToolResult.Error("UNPACK_FAILED", exception.Message); }
            return ToolResult.Ok($"unpacked Prefab instance '{root.name}' ({mode})", new JObject
            {
                ["previousTarget"] = target,
                ["currentTarget"] = ToolTargetRef.Capture(root)?.ToJson(),
                ["mode"] = mode.ToString(),
            });
        }
    }
}
