using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Analyzers;
using Maicon.SceneAnalyzer.Editor.Chat;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Analyze;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor
{
    /// Editor-load entry point. Owns the singleton instances of every long-lived
    /// piece of the plugin and wires them together. After a domain reload the
    /// static ctor runs again with a clean AppDomain — settings persist via
    /// EditorPrefs so the server picks up where it left off.
    [InitializeOnLoad]
    public static class Bootstrap
    {
        public static SceneSnapshot Snapshot { get; private set; }
        public static AssetCatalog AssetCatalog { get; private set; }
        public static AnalysisRunner Runner { get; private set; }
        public static ChangeWatcher Watcher { get; private set; }
        public static McpToolRegistry ToolRegistry { get; private set; }
        public static HttpMcpServer Server { get; private set; }

        // Coder chat state (Model B — chat panel inside Unity).
        public static ChatSession ChatSession { get; private set; }
        public static CoderApiClient ChatClient { get; private set; }

        /// Absolute path of the Unity project root (parent of `Assets/`). Captured
        /// at editor load on the main thread so it can be safely used from any
        /// background task (e.g. the chat client's project auto-discovery).
        public static string UnityProjectRoot { get; private set; }

        public static event Action OnIssuesChanged;
        public static event Action<string> OnServerLog;

        // Capped log of recent ops surfaced in the SceneAnalyzerWindow.
        private const int MaxRecentEntries = 50;
        public static readonly List<string> RecentEntries = new();

        static Bootstrap()
        {
            try
            {
                UnityProjectRoot = System.IO.Path.GetDirectoryName(UnityEngine.Application.dataPath);

                Snapshot = new SceneSnapshot();
                AssetCatalog = new AssetCatalog();
                Runner = new AnalysisRunner(Snapshot);
                Runner.Register(new GeometryAnalyzer());
                Runner.Register(new MissingReferenceAnalyzer());

                Watcher = new ChangeWatcher(Snapshot, () =>
                {
                    Runner.InvalidateCache();
                    try { OnIssuesChanged?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
                });

                ToolRegistry = new McpToolRegistry();
                // Read
                ToolRegistry.Register(new ReadHierarchyTool());
                ToolRegistry.Register(new ReadSceneTool());
                ToolRegistry.Register(new ReadComponentTool());
                ToolRegistry.Register(new GetProjectMetaTool());
                ToolRegistry.Register(new ScreenshotSceneViewTool());
                // Analyze
                ToolRegistry.Register(new AnalyzePerformanceTool());
                ToolRegistry.Register(new ValidateSceneTool());
                // Discover
                ToolRegistry.Register(new ListAssetsTool());
                ToolRegistry.Register(new SearchAssetsTool());
                ToolRegistry.Register(new ReadAssetMetadataTool());
                ToolRegistry.Register(new FindReferencesToAssetTool());
                // Write — create
                ToolRegistry.Register(new CreatePrimitiveTool());
                ToolRegistry.Register(new CreateEmptyTool());
                ToolRegistry.Register(new InstantiatePrefabTool());
                ToolRegistry.Register(new CreateLightTool());
                ToolRegistry.Register(new CreateCameraTool());
                ToolRegistry.Register(new CreateAudioSourceTool());
                ToolRegistry.Register(new CreateParticleSystemTool());
                // Write — modify
                ToolRegistry.Register(new SetTransformTool());
                ToolRegistry.Register(new SetCameraPropsTool());
                ToolRegistry.Register(new SetPropertyTool());
                ToolRegistry.Register(new AddComponentTool());
                ToolRegistry.Register(new SetMaterialTool());
                ToolRegistry.Register(new ParentObjectTool());
                ToolRegistry.Register(new DeleteObjectTool());
                // Write — render settings
                ToolRegistry.Register(new SetRenderSettingsTool());
                // Batch + selection
                ToolRegistry.Register(new ApplyChangesetTool());
                ToolRegistry.Register(new SelectObjectTool());
                // Post-processing tools (create_volume, add_post_effect, set_post_effect_property,
                // apply_color_grading) live in com.maicon.scene-analyzer.Editor.PostFx and are
                // registered there via PostFxBootstrap when SRP Core is present.

                Server = new HttpMcpServer(ToolRegistry);
                Server.OnLog += msg => RecordEntry($"[server] {msg}");

                ChatSession = new ChatSession();
                ChatClient = new CoderApiClient(ChatSession);

                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;

                if (Settings.AutoStartServer && !string.IsNullOrEmpty(Settings.Token))
                {
                    try { Server.Start(Settings.Port, Settings.Token); }
                    catch (Exception e)
                    {
                        Debug.LogError($"[scene-analyzer] auto-start failed: {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        public static void StartServer()
        {
            if (string.IsNullOrEmpty(Settings.Token))
                Settings.Token = Settings.GenerateToken();
            Server.Start(Settings.Port, Settings.Token);
        }

        public static void StopServer() => Server.Stop();

        public static void RecordEntry(string text)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
            RecentEntries.Insert(0, line);
            if (RecentEntries.Count > MaxRecentEntries)
                RecentEntries.RemoveRange(MaxRecentEntries, RecentEntries.Count - MaxRecentEntries);
            try { OnServerLog?.Invoke(line); }
            catch (Exception e) { Debug.LogException(e); }
        }

        private static void OnBeforeReload()
        {
            try { Server?.Stop(); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}
