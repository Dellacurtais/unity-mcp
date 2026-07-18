using System;
using System.IO;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Mcp;
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

        public static JObject SceneSelectorSchema(bool require = true) => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["scenePath"] = new JObject { ["type"] = "string" },
                ["sceneGuid"] = new JObject { ["type"] = "string" },
                ["sceneHandle"] = new JObject { ["type"] = "integer" },
                ["sceneName"] = new JObject { ["type"] = "string" },
            },
            ["additionalProperties"] = false,
            ["minProperties"] = require ? 1 : 0,
        };

        public static Scene Resolve(JObject args, out string error)
        {
            error = null;
            string path = (string)args["scenePath"];
            string guid = (string)args["sceneGuid"];
            string name = (string)args["sceneName"];
            int handle = (int?)args["sceneHandle"] ?? 0;
            if (!string.IsNullOrEmpty(guid)) path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);

            var matches = new System.Collections.Generic.List<Scene>();
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (!scene.IsValid()) continue;
                if (handle != 0 && scene.handle != handle) continue;
                if (!string.IsNullOrEmpty(path) && !string.Equals(scene.path, path, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(name) && !string.Equals(scene.name, name, StringComparison.Ordinal)) continue;
                matches.Add(scene);
            }
            if (matches.Count == 1) return matches[0];
            error = matches.Count == 0
                ? "SCENE_NOT_FOUND: no loaded scene matched the supplied selector"
                : "SCENE_AMBIGUOUS: selector matched multiple loaded scenes; use scenePath or sceneHandle";
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
                ["isDirty"] = scene.isDirty,
                ["isActive"] = scene == SceneManager.GetActiveScene(),
                ["rootCount"] = scene.rootCount,
            };
        }

        public static bool TrySceneAssetPath(string value, out string normalized, out string error)
        {
            normalized = (value ?? string.Empty).Replace('\\', '/');
            error = null;
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || !normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                error = "destinationPath must be an Assets/*.unity path";
                return false;
            }
            var segments = normalized.Split('/');
            if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment == "." || segment == ".."))
            {
                error = "destinationPath must not contain empty, '.' or '..' path segments";
                return false;
            }
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

        public static void EnsureFolder(string scenePath)
        {
            var folder = Path.GetDirectoryName(scenePath)?.Replace('\\', '/');
            EnsureFolderRecursive(folder);
        }

        private static void EnsureFolderRecursive(string folder)
        {
            if (string.IsNullOrEmpty(folder) || UnityEditor.AssetDatabase.IsValidFolder(folder)) return;
            int slash = folder.LastIndexOf('/');
            var parent = slash > 0 ? folder.Substring(0, slash) : "Assets";
            var name = slash > 0 ? folder.Substring(slash + 1) : folder;
            EnsureFolderRecursive(parent);
            UnityEditor.AssetDatabase.CreateFolder(parent, name);
        }
    }

    public sealed class ListLoadedScenesTool : IMcpTool
    {
        public string Name => "list_loaded_scenes";
        public string Description => "Lists every loaded scene with stable GUID/path/handle and active/dirty state.";
        public JObject InputSchema => SceneToolSupport.EmptySchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var scenes = new JArray();
            for (int index = 0; index < SceneManager.sceneCount; index++)
                scenes.Add(SceneToolSupport.ToJson(SceneManager.GetSceneAt(index)));
            return ToolResult.Ok($"{scenes.Count} loaded scene(s)", new JObject
            {
                ["activeSceneHandle"] = (int)SceneManager.GetActiveScene().handle,
                ["scenes"] = scenes,
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

        public ToolResult Execute(JObject args)
        {
            bool additive = (bool?)args["additive"] ?? true;
            if (!additive && HasDirtyScenes())
                return ToolResult.Error("UNSAVED_SCENES", "single-scene creation would close dirty scenes; save or close them explicitly first");
            var scene = EditorSceneManager.NewScene(
                (bool?)args["withDefaultObjects"] ?? false ? NewSceneSetup.DefaultGameObjects : NewSceneSetup.EmptyScene,
                additive ? NewSceneMode.Additive : NewSceneMode.Single);
            return ToolResult.Ok("created unsaved scene", new JObject
            {
                ["scene"] = SceneToolSupport.ToJson(scene),
                ["saved"] = false,
                ["nextTool"] = "save_scene",
            });
        }

        private static bool HasDirtyScenes()
        {
            for (int index = 0; index < SceneManager.sceneCount; index++)
                if (SceneManager.GetSceneAt(index).isDirty) return true;
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
                ["scenePath"] = new JObject { ["type"] = "string" },
                ["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Single", "Additive", "AdditiveWithoutLoading" }, ["default"] = "Additive" },
            },
            ["required"] = new JArray { "scenePath" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var path = (string)args["scenePath"];
            if (string.IsNullOrEmpty(path) || UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEditor.SceneAsset>(path) == null)
                return ToolResult.Error("SCENE_NOT_FOUND", $"scene asset not found: {path}");
            var mode = (OpenSceneMode)Enum.Parse(typeof(OpenSceneMode), (string)args["mode"] ?? "Additive", true);
            if (mode == OpenSceneMode.Single)
            {
                for (int index = 0; index < SceneManager.sceneCount; index++)
                    if (SceneManager.GetSceneAt(index).isDirty)
                        return ToolResult.Error("UNSAVED_SCENES", "single-scene open would close dirty scenes; save or close them explicitly first");
            }
            var scene = EditorSceneManager.OpenScene(path, mode);
            return ToolResult.Ok($"opened scene '{path}' ({mode})", new JObject { ["scene"] = SceneToolSupport.ToJson(scene) });
        }
    }

    public sealed class CloseSceneTool : IMcpTool
    {
        public string Name => "close_scene";
        public string Description => "Closes/removes one loaded scene. Dirty scenes are rejected unless discardUnsaved=true, which requires destructive confirmation.";
        public JObject InputSchema
        {
            get
            {
                var schema = SceneToolSupport.SceneSelectorSchema();
                ((JObject)schema["properties"])["discardUnsaved"] = new JObject { ["type"] = "boolean", ["default"] = false };
                return schema;
            }
        }
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var scene = SceneToolSupport.Resolve(args, out var error);
            if (!scene.IsValid()) return ToolResult.Error("SCENE_NOT_FOUND", error);
            if (scene.isDirty && !((bool?)args["discardUnsaved"] ?? false))
                return ToolResult.Error("SCENE_DIRTY", "scene has unsaved changes; save it or pass discardUnsaved=true");
            var snapshot = SceneToolSupport.ToJson(scene);
            if (!EditorSceneManager.CloseScene(scene, true))
                return ToolResult.Error("CLOSE_FAILED", "EditorSceneManager.CloseScene returned false", snapshot);
            return ToolResult.Ok("scene closed", new JObject { ["closedScene"] = snapshot });
        }
    }

    public sealed class SetActiveSceneTool : IMcpTool
    {
        public string Name => "set_active_scene";
        public string Description => "Sets one loaded scene active without saving any scene.";
        public JObject InputSchema => SceneToolSupport.SceneSelectorSchema();
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var scene = SceneToolSupport.Resolve(args, out var error);
            if (!scene.IsValid()) return ToolResult.Error("SCENE_NOT_FOUND", error);
            if (!SceneManager.SetActiveScene(scene)) return ToolResult.Error("SET_ACTIVE_FAILED", "SceneManager.SetActiveScene returned false");
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
                var schema = SceneToolSupport.SceneSelectorSchema(false);
                var properties = (JObject)schema["properties"];
                properties["destinationPath"] = new JObject { ["type"] = "string" };
                properties["saveAsCopy"] = new JObject { ["type"] = "boolean", ["default"] = false };
                return schema;
            }
        }
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            Scene scene;
            string error = null;
            bool hasSelector = args["scenePath"] != null || args["sceneGuid"] != null || args["sceneHandle"] != null || args["sceneName"] != null;
            scene = hasSelector ? SceneToolSupport.Resolve(args, out error) : SceneManager.GetActiveScene();
            if (!scene.IsValid()) return ToolResult.Error("SCENE_NOT_FOUND", error ?? "active scene is invalid");
            var destination = (string)args["destinationPath"];
            if (string.IsNullOrEmpty(scene.path) && string.IsNullOrEmpty(destination))
                return ToolResult.Error("DESTINATION_REQUIRED", "unsaved scenes require destinationPath");
            if (!string.IsNullOrEmpty(destination))
            {
                if (!SceneToolSupport.TrySceneAssetPath(destination, out destination, out error))
                    return ToolResult.Error("BAD_ARGS", error);
                SceneToolSupport.EnsureFolder(destination);
            }
            bool success = EditorSceneManager.SaveScene(scene, destination ?? string.Empty, (bool?)args["saveAsCopy"] ?? false);
            if (!success) return ToolResult.Error("SAVE_FAILED", "EditorSceneManager.SaveScene returned false");
            return ToolResult.Ok($"saved scene '{scene.name}'", new JObject
            {
                ["scene"] = SceneToolSupport.ToJson(scene),
                ["destinationPath"] = destination ?? scene.path,
                ["saveAsCopy"] = (bool?)args["saveAsCopy"] ?? false,
            });
        }
    }

    public sealed class SaveAllScenesTool : IMcpTool
    {
        public string Name => "save_all_scenes";
        public string Description => "Explicitly saves all dirty, already-named loaded scenes; reports unsaved scenes instead of opening dialogs.";
        public JObject InputSchema => SceneToolSupport.EmptySchema();
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var saved = new JArray();
            var skipped = new JArray();
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (!scene.IsValid() || !scene.isDirty) continue;
                if (string.IsNullOrEmpty(scene.path))
                {
                    skipped.Add(new JObject { ["scene"] = SceneToolSupport.ToJson(scene), ["reason"] = "destination required" });
                    continue;
                }
                if (EditorSceneManager.SaveScene(scene)) saved.Add(SceneToolSupport.ToJson(scene));
                else skipped.Add(new JObject { ["scene"] = SceneToolSupport.ToJson(scene), ["reason"] = "save failed" });
            }
            return ToolResult.Ok($"saved {saved.Count} scene(s); skipped {skipped.Count}", new JObject
            {
                ["saved"] = saved,
                ["skipped"] = skipped,
            });
        }
    }
}
