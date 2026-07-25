using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.SceneTools
{
    internal static class SceneToolSupport
    {
        public static JObject EmptySchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };

        public static JObject SceneSelectorSchema(bool require = true, JObject extraProperties = null)
        {
            var selectorProperties = new JObject
            {
                ["scenePath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["sceneGuid"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["sceneHandle"] = SceneHandleSchema(),
                ["sceneName"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
            };
            var properties = new JObject();
            foreach (var property in selectorProperties.Properties()) properties[property.Name] = property.Value.DeepClone();
            if (extraProperties != null)
                foreach (var property in extraProperties.Properties()) properties[property.Name] = property.Value.DeepClone();

            var variants = new JArray();
            foreach (var selector in selectorProperties.Properties())
            {
                var variantProperties = new JObject { [selector.Name] = selector.Value.DeepClone() };
                if (extraProperties != null)
                    foreach (var property in extraProperties.Properties()) variantProperties[property.Name] = property.Value.DeepClone();
                variants.Add(new JObject
                {
                    ["type"] = "object",
                    ["properties"] = variantProperties,
                    ["required"] = new JArray { selector.Name },
                    ["additionalProperties"] = false,
                });
            }
            if (!require)
            {
                var variantProperties = new JObject();
                if (extraProperties != null)
                    foreach (var property in extraProperties.Properties()) variantProperties[property.Name] = property.Value.DeepClone();
                variants.Add(new JObject
                {
                    ["type"] = "object",
                    ["properties"] = variantProperties,
                    ["additionalProperties"] = false,
                });
            }

            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["oneOf"] = variants,
                ["additionalProperties"] = false,
            };
        }

        public static JObject SceneHandleSchema() => new JObject
        {
            ["type"] = "integer",
            ["oneOf"] = new JArray
            {
                new JObject { ["type"] = "integer", ["minimum"] = int.MinValue, ["maximum"] = -1 },
                new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = int.MaxValue },
            },
        };

        public static bool HasAnySelector(JObject args)
        {
            if (args == null) return false;
            return args["scenePath"] != null || args["sceneGuid"] != null ||
                   args["sceneHandle"] != null || args["sceneName"] != null;
        }

        public static Scene Resolve(JObject args, out string errorCode, out string error)
        {
            errorCode = null;
            error = null;
            args = args ?? new JObject();
            var supplied = new List<string>();
            foreach (var key in new[] { "scenePath", "sceneGuid", "sceneHandle", "sceneName" })
                if (args[key] != null && args[key].Type != JTokenType.Null) supplied.Add(key);
            if (supplied.Count != 1)
            {
                errorCode = "BAD_ARGS";
                error = "exactly one of scenePath, sceneGuid, sceneHandle or sceneName must be supplied";
                return default;
            }

            string path = null;
            string name = null;
            int? handle = null;
            var selector = supplied[0];
            var token = args[selector];
            if (selector == "sceneHandle")
            {
                if (token.Type != JTokenType.Integer || (long)token == 0 || (long)token < int.MinValue || (long)token > int.MaxValue)
                {
                    errorCode = "BAD_ARGS";
                    error = "sceneHandle must be a non-zero Int32";
                    return default;
                }
                handle = (int)token;
            }
            else
            {
                if (token.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)token))
                {
                    errorCode = "BAD_ARGS";
                    error = selector + " must be a non-empty string";
                    return default;
                }
                var value = ((string)token).Trim();
                if (selector == "scenePath") path = value.Replace('\\', '/');
                else if (selector == "sceneName") name = value;
                else
                {
                    path = UnityEditor.AssetDatabase.GUIDToAssetPath(value);
                    if (string.IsNullOrEmpty(path))
                    {
                        errorCode = "SCENE_NOT_FOUND";
                        error = "sceneGuid does not resolve to a scene asset";
                        return default;
                    }
                }
            }

            var matches = new List<Scene>();
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (!scene.IsValid()) continue;
                if (handle.HasValue && scene.handle != handle.Value) continue;
                if (!string.IsNullOrEmpty(path) && !string.Equals(scene.path, path, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(name) && !string.Equals(scene.name, name, StringComparison.Ordinal)) continue;
                matches.Add(scene);
            }
            if (matches.Count == 1) return matches[0];
            errorCode = matches.Count == 0 ? "SCENE_NOT_FOUND" : "SCENE_AMBIGUOUS";
            error = matches.Count == 0
                ? "no open scene matched the supplied selector"
                : "sceneName matched multiple open scenes; use scenePath or sceneHandle";
            return default;
        }

        public static JObject ToJson(Scene scene)
        {
            return new JObject
            {
                ["name"] = scene.name,
                ["path"] = scene.path,
                ["guid"] = string.IsNullOrEmpty(scene.path) ? null : UnityEditor.AssetDatabase.AssetPathToGUID(scene.path),
                ["handle"] = (int)scene.handle,
                ["buildIndex"] = scene.buildIndex,
                ["isLoaded"] = scene.isLoaded,
                ["isDirty"] = scene.isLoaded && scene.isDirty,
                ["isActive"] = scene == SceneManager.GetActiveScene(),
                ["rootCount"] = scene.isLoaded ? scene.rootCount : 0,
            };
        }

        public static bool TrySceneAssetPath(string value, out string normalized, out string error)
        {
            normalized = (value ?? string.Empty).Trim().Replace('\\', '/');
            error = null;
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || !normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                error = "destinationPath must be an Assets/*.unity path";
                return false;
            }
            var segments = normalized.Split('/');
            var invalidFileNameChars = Path.GetInvalidFileNameChars();
            if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment == "." || segment == ".."
                || segment != segment.Trim() || segment.EndsWith(".", StringComparison.Ordinal)
                || segment.IndexOfAny(invalidFileNameChars) >= 0))
            {
                error = "destinationPath contains an invalid or non-canonical path segment";
                return false;
            }
            segments[0] = "Assets";
            normalized = string.Join("/", segments);
            var root = Path.GetFullPath(Bootstrap.UnityProjectRoot);
            var assetsRoot = Path.GetFullPath(Path.Combine(root, "Assets")) + Path.DirectorySeparatorChar;
            var absolute = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!absolute.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
            {
                error = "destinationPath resolves outside the project Assets directory";
                return false;
            }
            return true;
        }

        public static void MarkSnapshotStale()
        {
            Bootstrap.Snapshot?.MarkStale();
        }

        public static bool TryEnsureFolder(string scenePath, out List<string> createdFolders, out string error)
        {
            createdFolders = new List<string>();
            error = null;
            var folder = Path.GetDirectoryName(scenePath)?.Replace('\\', '/');
            return TryEnsureFolderRecursive(folder, createdFolders, out error);
        }

        public static void RollbackSaveDestination(string destinationPath, bool removeDestination, List<string> createdFolders)
        {
            try
            {
                if (removeDestination && !string.IsNullOrEmpty(destinationPath)
                    && !string.IsNullOrEmpty(UnityEditor.AssetDatabase.AssetPathToGUID(destinationPath)))
                    UnityEditor.AssetDatabase.DeleteAsset(destinationPath);

                if (createdFolders == null) return;
                for (var index = createdFolders.Count - 1; index >= 0; index--)
                {
                    var folder = createdFolders[index];
                    var absolute = Path.Combine(Bootstrap.UnityProjectRoot, folder.Replace('/', Path.DirectorySeparatorChar));
                    if (Directory.Exists(absolute) && !Directory.EnumerateFileSystemEntries(absolute).Any())
                        UnityEditor.AssetDatabase.DeleteAsset(folder);
                }
            }
            catch
            {
                // Preserve the original save error. Rollback is best-effort and only
                // touches the destination/folders created by this invocation.
            }
        }

        private static bool TryEnsureFolderRecursive(string folder, List<string> createdFolders, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(folder))
            {
                error = "destinationPath must have a parent folder";
                return false;
            }
            if (UnityEditor.AssetDatabase.IsValidFolder(folder)) return true;
            int slash = folder.LastIndexOf('/');
            var parent = slash > 0 ? folder.Substring(0, slash) : "Assets";
            var name = slash > 0 ? folder.Substring(slash + 1) : folder;
            if (!TryEnsureFolderRecursive(parent, createdFolders, out error)) return false;
            var guid = UnityEditor.AssetDatabase.CreateFolder(parent, name);
            if (string.IsNullOrEmpty(guid) || !UnityEditor.AssetDatabase.IsValidFolder(folder))
            {
                error = $"failed to create destination folder '{folder}'";
                return false;
            }
            createdFolders.Add(folder);
            return true;
        }
    }

    public sealed class ListLoadedScenesTool : IMcpTool
    {
        public string Name => "list_loaded_scenes";
        public string Description => "Lists every scene open in the Hierarchy, separating loaded scenes from AdditiveWithoutLoading entries and reporting stable GUID/path/handle plus active/dirty state.";
        public JObject InputSchema => SceneToolSupport.EmptySchema();
        public bool IsWrite => false;
        public JObject OutputSchema => SceneToolSchemas.ListScenes;
        public ToolResult Execute(JObject args)
        {
            var scenes = new JArray();
            var loadedScenes = new JArray();
            var unloadedScenes = new JArray();
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                var row = SceneToolSupport.ToJson(SceneManager.GetSceneAt(index));
                scenes.Add(row);
                (row.Value<bool>("isLoaded") ? loadedScenes : unloadedScenes).Add(row.DeepClone());
            }
            return ToolResult.Ok($"{loadedScenes.Count} loaded and {unloadedScenes.Count} unloaded scene(s) open", new JObject
            {
                ["activeSceneHandle"] = (int)SceneManager.GetActiveScene().handle,
                ["sceneCount"] = scenes.Count,
                ["loadedCount"] = loadedScenes.Count,
                ["unloadedCount"] = unloadedScenes.Count,
                ["scenes"] = scenes,
                ["loadedScenes"] = loadedScenes,
                ["unloadedScenes"] = unloadedScenes,
            });
        }
    }

    public sealed class CreateSceneTool : IMcpTool
    {
        public string Name => "create_scene";
        public string Description => "Creates a new empty or default unsaved scene, optionally additively. Saving is always a separate save_scene call.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["additive"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["withDefaultObjects"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public JObject OutputSchema => SceneToolSchemas.CreateScene;

        public ToolResult Execute(JObject args)
        {
            bool additive = (bool?)args["additive"] ?? true;
            if (!additive && HasDirtyScenes())
                return ToolResult.Error("UNSAVED_SCENES", "single-scene creation would close dirty scenes; save or close them explicitly first");
            if (additive && HasUntitledScene())
                return ToolResult.Error("UNTITLED_SCENE_OPEN", "Unity cannot create an additive scene while an untitled scene is already open; save or close it first");
            try
            {
                var scene = EditorSceneManager.NewScene(
                    (bool?)args["withDefaultObjects"] ?? false ? NewSceneSetup.DefaultGameObjects : NewSceneSetup.EmptyScene,
                    additive ? NewSceneMode.Additive : NewSceneMode.Single);
                SceneToolSupport.MarkSnapshotStale();
                return ToolResult.Ok("created unsaved scene", new JObject
                {
                    ["scene"] = SceneToolSupport.ToJson(scene),
                    ["saved"] = false,
                    ["nextTool"] = "save_scene",
                });
            }
            catch (Exception e)
            {
                return ToolResult.Error("CREATE_FAILED", e.Message);
            }
        }

        private static bool HasDirtyScenes()
        {
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene.IsValid() && scene.isLoaded && scene.isDirty) return true;
            }
            return false;
        }

        private static bool HasUntitledScene()
        {
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene.IsValid() && scene.isLoaded && string.IsNullOrEmpty(scene.path)) return true;
            }
            return false;
        }
    }

    public sealed class OpenSceneTool : IMcpTool
    {
        public string Name => "open_scene";
        public string Description => "Opens a scene asset in Single, Additive or AdditiveWithoutLoading mode. Unsaved scenes are never auto-saved.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["scenePath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Single", "Additive", "AdditiveWithoutLoading" }, ["default"] = "Additive" },
            },
            ["required"] = new JArray { "scenePath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public JObject OutputSchema => SceneToolSchemas.SceneResult;
        public ToolResult Execute(JObject args)
        {
            var path = ((string)args["scenePath"] ?? string.Empty).Trim().Replace('\\', '/');
            if (string.IsNullOrEmpty(path) || UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEditor.SceneAsset>(path) == null)
                return ToolResult.Error("SCENE_NOT_FOUND", $"scene asset not found: {path}");
            if (!Enum.TryParse((string)args["mode"] ?? "Additive", true, out OpenSceneMode mode) || !Enum.IsDefined(typeof(OpenSceneMode), mode))
                return ToolResult.Error("BAD_ARGS", "mode must be Single, Additive or AdditiveWithoutLoading");
            if (mode == OpenSceneMode.Single)
            {
                for (int index = 0; index < SceneManager.sceneCount; index++)
                {
                    var current = SceneManager.GetSceneAt(index);
                    if (current.IsValid() && current.isLoaded && current.isDirty)
                        return ToolResult.Error("UNSAVED_SCENES", "single-scene open would close dirty scenes; save or close them explicitly first");
                }
            }
            try
            {
                var scene = EditorSceneManager.OpenScene(path, mode);
                SceneToolSupport.MarkSnapshotStale();
                return ToolResult.Ok($"opened scene '{path}' ({mode})", new JObject { ["scene"] = SceneToolSupport.ToJson(scene) });
            }
            catch (Exception e)
            {
                return ToolResult.Error("OPEN_FAILED", e.Message);
            }
        }
    }

    public sealed class CloseSceneTool : IMcpTool
    {
        public string Name => "close_scene";
        public string Description => "Closes/removes one open scene entry. Dirty scenes are rejected unless discardUnsaved=true; the last loaded scene cannot be removed.";
        public JObject InputSchema
        {
            get
            {
                return SceneToolSupport.SceneSelectorSchema(true, new JObject
                {
                    ["discardUnsaved"] = new JObject { ["type"] = "boolean", ["default"] = false },
                });
            }
        }
        public bool IsWrite => true;
        public JObject OutputSchema => SceneToolSchemas.CloseScene;
        public ToolResult Execute(JObject args)
        {
            var scene = SceneToolSupport.Resolve(args, out var errorCode, out var error);
            if (!scene.IsValid()) return ToolResult.Error(errorCode ?? "SCENE_NOT_FOUND", error);
            if (scene.isLoaded && EditorSceneManager.loadedSceneCount <= 1)
                return ToolResult.Error("LAST_LOADED_SCENE", "cannot close the last loaded scene; open or create another loaded scene first");
            if (scene.isLoaded && scene.isDirty && !((bool?)args["discardUnsaved"] ?? false))
                return ToolResult.Error("SCENE_DIRTY", "scene has unsaved changes; save it or pass discardUnsaved=true");
            var snapshot = SceneToolSupport.ToJson(scene);
            if (!EditorSceneManager.CloseScene(scene, true))
                return ToolResult.Error("CLOSE_FAILED", "EditorSceneManager.CloseScene returned false", snapshot);
            SceneToolSupport.MarkSnapshotStale();
            return ToolResult.Ok("scene closed", new JObject { ["closedScene"] = snapshot });
        }
    }

    public sealed class SetActiveSceneTool : IMcpTool
    {
        public string Name => "set_active_scene";
        public string Description => "Sets one loaded scene active without saving any scene.";
        public JObject InputSchema => SceneToolSupport.SceneSelectorSchema();
        public bool IsWrite => true;
        public JObject OutputSchema => SceneToolSchemas.SceneResult;
        public ToolResult Execute(JObject args)
        {
            var scene = SceneToolSupport.Resolve(args, out var errorCode, out var error);
            if (!scene.IsValid()) return ToolResult.Error(errorCode ?? "SCENE_NOT_FOUND", error);
            if (!scene.isLoaded) return ToolResult.Error("SCENE_NOT_LOADED", "an unloaded hierarchy entry cannot be made active");
            if (SceneManager.GetActiveScene() != scene && !SceneManager.SetActiveScene(scene))
                return ToolResult.Error("SET_ACTIVE_FAILED", "SceneManager.SetActiveScene returned false");
            SceneToolSupport.MarkSnapshotStale();
            return ToolResult.Ok($"active scene is now '{scene.name}'", new JObject { ["scene"] = SceneToolSupport.ToJson(scene) });
        }
    }

    public sealed class SaveSceneTool : IMcpTool
    {
        public string Name => "save_scene";
        public string Description => "Explicitly saves one loaded scene. Unsaved scenes require destinationPath to avoid modal dialogs.";
        public JObject InputSchema
        {
            get
            {
                var schema = SceneToolSupport.SceneSelectorSchema(false, new JObject
                {
                    ["destinationPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                    ["saveAsCopy"] = new JObject { ["type"] = "boolean", ["default"] = false },
                });
                schema["allOf"] = new JArray
                {
                    new JObject
                    {
                        ["oneOf"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JObject
                                {
                                    ["destinationPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                                    ["saveAsCopy"] = new JObject { ["type"] = "boolean", ["enum"] = new JArray { true } },
                                },
                                ["required"] = new JArray { "destinationPath", "saveAsCopy" },
                            },
                            new JObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JObject
                                {
                                    ["saveAsCopy"] = new JObject { ["type"] = "boolean", ["enum"] = new JArray { false } },
                                },
                                ["required"] = new JArray { "saveAsCopy" },
                            },
                            new JObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JObject
                                {
                                    ["scenePath"] = new JObject { ["type"] = "string" },
                                    ["sceneGuid"] = new JObject { ["type"] = "string" },
                                    ["sceneHandle"] = new JObject { ["type"] = "integer" },
                                    ["sceneName"] = new JObject { ["type"] = "string" },
                                    ["destinationPath"] = new JObject { ["type"] = "string" },
                                },
                                ["additionalProperties"] = false,
                            },
                        },
                    },
                };
                return schema;
            }
        }
        public bool IsWrite => true;
        public JObject OutputSchema => SceneToolSchemas.SaveScene;
        public ToolResult Execute(JObject args)
        {
            Scene scene;
            string error = null;
            string errorCode = null;
            bool hasSelector = SceneToolSupport.HasAnySelector(args);
            scene = hasSelector ? SceneToolSupport.Resolve(args, out errorCode, out error) : SceneManager.GetActiveScene();
            if (!scene.IsValid()) return ToolResult.Error(errorCode ?? "SCENE_NOT_FOUND", error ?? "active scene is invalid");
            if (!scene.isLoaded) return ToolResult.Error("SCENE_NOT_LOADED", "an unloaded hierarchy entry cannot be saved");
            var destination = (string)args["destinationPath"];
            bool saveAsCopy = (bool?)args["saveAsCopy"] ?? false;
            if (saveAsCopy && string.IsNullOrWhiteSpace(destination))
                return ToolResult.Error("DESTINATION_REQUIRED", "saveAsCopy requires an explicit destinationPath");
            if (string.IsNullOrEmpty(scene.path) && string.IsNullOrWhiteSpace(destination))
                return ToolResult.Error("DESTINATION_REQUIRED", "unsaved scenes require destinationPath");

            List<string> createdFolders = null;
            bool removeDestinationOnFailure = false;
            if (!string.IsNullOrWhiteSpace(destination))
            {
                if (!SceneToolSupport.TrySceneAssetPath(destination, out destination, out error))
                    return ToolResult.Error("BAD_ARGS", error);
                bool samePath = string.Equals(scene.path, destination, StringComparison.OrdinalIgnoreCase);
                if (saveAsCopy && samePath)
                    return ToolResult.Error("BAD_ARGS", "saveAsCopy requires a destination different from the source scene");
                var destinationGuid = UnityEditor.AssetDatabase.AssetPathToGUID(destination);
                if (!samePath && !string.IsNullOrEmpty(destinationGuid))
                    return ToolResult.Error("DESTINATION_EXISTS", "destinationPath already contains an asset; choose a new path");
                removeDestinationOnFailure = !samePath && string.IsNullOrEmpty(destinationGuid);
                if (!SceneToolSupport.TryEnsureFolder(destination, out createdFolders, out error))
                {
                    SceneToolSupport.RollbackSaveDestination(null, false, createdFolders);
                    return ToolResult.Error("DESTINATION_FOLDER_FAILED", error);
                }
            }
            try
            {
                bool success = EditorSceneManager.SaveScene(scene, destination ?? string.Empty, saveAsCopy);
                if (!success)
                {
                    SceneToolSupport.RollbackSaveDestination(destination, removeDestinationOnFailure, createdFolders);
                    return ToolResult.Error("SAVE_FAILED", "EditorSceneManager.SaveScene returned false");
                }
                SceneToolSupport.MarkSnapshotStale();
                return ToolResult.Ok($"saved scene '{scene.name}'", new JObject
                {
                    ["scene"] = SceneToolSupport.ToJson(scene),
                    ["destinationPath"] = destination ?? scene.path,
                    ["saveAsCopy"] = saveAsCopy,
                });
            }
            catch (Exception e)
            {
                SceneToolSupport.RollbackSaveDestination(destination, removeDestinationOnFailure, createdFolders);
                return ToolResult.Error("SAVE_FAILED", e.Message);
            }
        }
    }

    public sealed class SaveAllScenesTool : IMcpTool
    {
        public string Name => "save_all_scenes";
        public string Description => "Explicitly saves all dirty, already-named loaded scenes; reports unsaved scenes instead of opening dialogs.";
        public JObject InputSchema => SceneToolSupport.EmptySchema();
        public bool IsWrite => true;
        public JObject OutputSchema => SceneToolSchemas.SaveAllScenes;
        public ToolResult Execute(JObject args)
        {
            var saved = new JArray();
            var skipped = new JArray();
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (!scene.IsValid() || !scene.isLoaded || !scene.isDirty) continue;
                if (string.IsNullOrEmpty(scene.path))
                {
                    skipped.Add(new JObject { ["scene"] = SceneToolSupport.ToJson(scene), ["reason"] = "destination required" });
                    continue;
                }
                try
                {
                    if (EditorSceneManager.SaveScene(scene)) saved.Add(SceneToolSupport.ToJson(scene));
                    else skipped.Add(new JObject { ["scene"] = SceneToolSupport.ToJson(scene), ["reason"] = "save failed" });
                }
                catch (Exception e)
                {
                    skipped.Add(new JObject { ["scene"] = SceneToolSupport.ToJson(scene), ["reason"] = e.Message });
                }
            }
            if (saved.Count > 0) SceneToolSupport.MarkSnapshotStale();
            return ToolResult.Ok($"saved {saved.Count} scene(s); skipped {skipped.Count}", new JObject
            {
                ["savedCount"] = saved.Count,
                ["skippedCount"] = skipped.Count,
                ["saved"] = saved,
                ["skipped"] = skipped,
            });
        }
    }
}
