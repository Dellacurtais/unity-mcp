using System.Collections.Generic;
using System.Diagnostics;
using Maicon.SceneAnalyzer.Editor.Mcp.Jobs;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    /// Quits this Editor through the normal File/Exit path, but never with unsaved work.
    /// It refuses while a scene or the open prefab is dirty (unless saveDirtyScenes saves
    /// the scenes first), in Play Mode, while building or compiling, or while an MCP job
    /// owns the Editor. Without confirm=true it only reports what it would do. The broker
    /// tools broker_close_editor / broker_restart_editor call it and then wait outside Unity.
    public sealed class EditorQuitTool : IMcpTool
    {
        public string Name => "editor_quit";

        public string Description =>
            "Quits this Unity Editor safely. dryRun=true (or no confirm) only reports canQuit and why not: dirty scenes, " +
            "untitled scenes, a dirty prefab stage, Play Mode, a running build, compilation, or an MCP job holding the Editor. " +
            "confirm=true quits through File/Exit about one second after answering; saveDirtyScenes=true saves the dirty, " +
            "already-saved scenes first. Never discards unsaved scene work. Prefer broker_close_editor, which also waits for the process to end.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["dryRun"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["confirm"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["saveDirtyScenes"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject arguments)
        {
            arguments = arguments ?? new JObject();
            var dryRun = (bool?)arguments["dryRun"] ?? false;
            var confirm = (bool?)arguments["confirm"] ?? false;
            var saveDirty = (bool?)arguments["saveDirtyScenes"] ?? false;
            var state = Inspect(saveDirty, out var dirtyScenes);
            var canQuit = (bool)state["canQuit"];
            if (dryRun || !confirm)
            {
                state["dryRun"] = true;
                state["message"] = canQuit
                    ? "Nothing was closed. Call again with confirm=true to quit this Editor."
                    : (string)state["reason"];
                return ToolResult.Ok(canQuit ? "Editor can quit." : "Editor cannot quit: " + state["reason"], state);
            }
            if (!canQuit)
                return ToolResult.Error("QUIT_REFUSED", (string)state["reason"], state);

            var saved = new JArray();
            if (saveDirty && dirtyScenes.Count > 0)
            {
                if (!EditorSceneManager.SaveScenes(dirtyScenes.ToArray()))
                    return ToolResult.Error("SAVE_FAILED", "Unity did not save every dirty scene; nothing was closed.", state);
                foreach (var scene in dirtyScenes) saved.Add(scene.path);
            }
            AssetDatabase.SaveAssets();

            var quitAt = EditorApplication.timeSinceStartup + 1.0;
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (EditorApplication.timeSinceStartup < quitAt) return;
                EditorApplication.update -= tick;
                // Something may have become dirty or started meanwhile; check once more.
                var last = Inspect(false, out _);
                if (!(bool)last["canQuit"])
                {
                    UnityEngine.Debug.LogWarning("[unity-mcp] editor_quit cancelled: " + last["reason"]);
                    return;
                }
                // File/Exit runs the normal quit sequence: any other unsaved editor state
                // still gets Unity's own prompt instead of being discarded.
                if (!EditorApplication.ExecuteMenuItem("File/Exit")) EditorApplication.Exit(0);
            };
            EditorApplication.update += tick;
            Bootstrap.RecordEntry("[mcp] editor_quit scheduled");

            state["scheduled"] = true;
            state["savedScenes"] = saved;
            using (var process = Process.GetCurrentProcess()) state["processId"] = process.Id;
            return ToolResult.Ok("Editor will quit in about one second.", state);
        }

        private static JObject Inspect(bool saveDirty, out List<Scene> dirtyScenes)
        {
            dirtyScenes = new List<Scene>();
            var dirtyPaths = new JArray();
            var untitled = 0;
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (!scene.isDirty) continue;
                if (string.IsNullOrEmpty(scene.path)) { untitled++; continue; }
                dirtyScenes.Add(scene);
                dirtyPaths.Add(scene.path);
            }
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            var prefabDirty = prefabStage != null && prefabStage.scene.isDirty;
            var playing = EditorApplication.isPlayingOrWillChangePlaymode;
            var building = BuildPipeline.isBuildingPlayer;
            var compiling = EditorApplication.isCompiling;
            var jobBusy = EditorMutationGate.TryGetJobOwner(out var jobId, out var jobKind);

            string reason = null;
            if (playing) reason = "The Editor is in Play Mode; exit Play Mode first.";
            else if (building) reason = "A player build is running.";
            else if (compiling) reason = "Scripts are compiling; wait for compilation to finish.";
            else if (jobBusy) reason = $"A {jobKind} job ({jobId}) is running.";
            else if (untitled > 0) reason = "An untitled scene has unsaved changes; save it with a name first.";
            else if (prefabDirty) reason = "The open prefab has unsaved changes; save or close it first.";
            else if (dirtyScenes.Count > 0 && !saveDirty)
                reason = "Scenes have unsaved changes: " + string.Join(", ", dirtyPaths) + ". Save them, or pass saveDirtyScenes=true.";

            return new JObject
            {
                ["canQuit"] = reason == null,
                ["reason"] = reason,
                ["dirtyScenes"] = dirtyPaths,
                ["untitledDirtyScenes"] = untitled,
                ["prefabStageDirty"] = prefabDirty,
                ["isPlaying"] = playing,
                ["isBuilding"] = building,
                ["isCompiling"] = compiling,
                ["activeJobId"] = jobBusy ? jobId : null,
                ["wouldSaveScenes"] = saveDirty ? dirtyPaths.DeepClone() : new JArray(),
            };
        }
    }
}
