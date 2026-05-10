using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Analyzers;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Chat;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Analyze;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Discover;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools.Reflect;
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

        /// Cache of providers/models fetched from `GET /api/coder/providers`.
        /// Populated by CoderApiClient.RefreshProviders. The window reads this
        /// to render the provider/model dropdowns dynamically.
        public static List<CoderProviderInfo> CoderProviders { get; set; } = new();
        public static event Action OnCoderProvidersChanged;
        public static void NotifyCoderProvidersChanged() {
            try { OnCoderProvidersChanged?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
        }

        public static event Action OnIssuesChanged;
        public static event Action<string> OnServerLog;

        /// Called by HttpMcpServer after a write tool succeeds, and by the
        /// ChangesetExecutor.OnApplied subscriber below. Idempotent — MarkStale
        /// is a flag flip and InvalidateCache is a bool clear, so duplicate
        /// firing on apply_changeset is harmless.
        public static void NotifyWriteCompleted()
        {
            try
            {
                Snapshot?.MarkStale();
                Runner?.InvalidateCache();
                OnIssuesChanged?.Invoke();
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        // Capped log of recent ops surfaced in the SceneAnalyzerWindow.
        private const int MaxRecentEntries = 50;
        public static readonly List<string> RecentEntries = new();

        static Bootstrap()
        {
            try
            {
                UnityProjectRoot = System.IO.Path.GetDirectoryName(UnityEngine.Application.dataPath);

                // Migration: v0.8.0 and earlier defaulted CoderProjectId to 1
                // and never persisted the workdir of the matched project. Any
                // user with a stale cached id and no workdir is silently
                // pinned to whatever has id 1 in the dashboard. Reset the id
                // so the next Send/CreateSession auto-resolves correctly.
                if (Settings.CoderSettingsVersion < 2)
                {
                    if (string.IsNullOrEmpty(Settings.CoderProjectWorkdir))
                        Settings.CoderProjectId = 0;
                    Settings.CoderSettingsVersion = 2;
                }

                Snapshot = new SceneSnapshot();
                AssetCatalog = new AssetCatalog();
                Runner = new AnalysisRunner(Snapshot);
                // performance — order matters for the maxIssues cap
                // (cheap + low-volume first so chatty analyzers can't starve them).
                Runner.Register(new HierarchyDepthAnalyzer());
                Runner.Register(new GeometryAnalyzer());
                Runner.Register(new LightingAnalyzer());
                Runner.Register(new DrawCallEstimatorAnalyzer());
                Runner.Register(new ScriptCostAnalyzer());
                // validation
                Runner.Register(new MissingReferenceAnalyzer());
                Runner.Register(new RequiredFieldAnalyzer());

                Watcher = new ChangeWatcher(Snapshot, () =>
                {
                    Runner.InvalidateCache();
                    try { OnIssuesChanged?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
                });

                ChangesetExecutor.OnApplied += () =>
                {
                    try
                    {
                        Snapshot?.MarkStale();
                        Runner?.InvalidateCache();
                        OnIssuesChanged?.Invoke();
                    }
                    catch (Exception e) { Debug.LogException(e); }
                };

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
                ToolRegistry.Register(new ListComponentTypesTool());
                ToolRegistry.Register(new DescribeComponentTypeTool());
                ToolRegistry.Register(new ListScriptableObjectTypesTool());
                ToolRegistry.Register(new ReadScriptableObjectTool());
                ToolRegistry.Register(new FindScriptableObjectsTool());
                ToolRegistry.Register(new ListAnimatorControllersTool());
                ToolRegistry.Register(new ReadAnimatorControllerTool());
                ToolRegistry.Register(new ReadAnimationClipTool());
                ToolRegistry.Register(new ReadAvatarTool());
                ToolRegistry.Register(new ReadAnimatorStateTool());
                ToolRegistry.Register(new ListAudioMixersTool());
                ToolRegistry.Register(new ReadAudioMixerTool());
                ToolRegistry.Register(new InspectAudioSetupTool());
                // Console / build verification
                ToolRegistry.Register(new ReadConsoleTool());
                ToolRegistry.Register(new ClearConsoleTool());
                ToolRegistry.Register(new WaitForCompileCleanTool());
                // Physics queries + scene classification
                ToolRegistry.Register(new RaycastTool());
                ToolRegistry.Register(new OverlapQueryTool());
                ToolRegistry.Register(new SweepQueryTool());
                ToolRegistry.Register(new CheckGroundTool());
                ToolRegistry.Register(new ComputeBoundsTool());
                ToolRegistry.Register(new QuerySceneTool());
                ToolRegistry.Register(new ClassifySceneTool());
                ToolRegistry.Register(new QueryCollisionLayersTool());
                // NavMesh Tier 1 — built-in queries + discovery + legacy bake + agent + links/obstacle
                ToolRegistry.Register(new NavMeshSamplePositionTool());
                ToolRegistry.Register(new NavMeshCalculatePathTool());
                ToolRegistry.Register(new NavMeshRaycastTool());
                ToolRegistry.Register(new NavMeshFindClosestEdgeTool());
                ToolRegistry.Register(new ListNavMeshAreasTool());
                ToolRegistry.Register(new ListNavMeshAgentTypesTool());
                ToolRegistry.Register(new BakeNavMeshTool());
                ToolRegistry.Register(new SetNavMeshAgentTool());
                ToolRegistry.Register(new AddOffMeshLinkTool());
                ToolRegistry.Register(new AddNavMeshObstacleTool());
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
                ToolRegistry.Register(new RemoveComponentTool());
                ToolRegistry.Register(new SetEnabledTool());
                ToolRegistry.Register(new SetActiveTool());
                ToolRegistry.Register(new RenameObjectTool());
                ToolRegistry.Register(new SetTagTool());
                ToolRegistry.Register(new SetLayerTool());
                ToolRegistry.Register(new SetMaterialTool());
                ToolRegistry.Register(new ParentObjectTool());
                ToolRegistry.Register(new DeleteObjectTool());
                ToolRegistry.Register(new DuplicateObjectTool());
                ToolRegistry.Register(new LookAtTool());
                ToolRegistry.Register(new SetParticleModuleTool());
                ToolRegistry.Register(new PlayParticlePreviewTool());
                ToolRegistry.Register(new RefreshAssetsTool());
                // ScriptableObject + array helper
                ToolRegistry.Register(new CreateScriptableObjectTool());
                ToolRegistry.Register(new SetSoPropertyTool());
                ToolRegistry.Register(new DuplicateScriptableObjectTool());
                ToolRegistry.Register(new ModifyArrayPropertyTool());
                // Animation (Mecanim) — Tier 1 + 2
                ToolRegistry.Register(new SetAnimatorParameterTool());
                ToolRegistry.Register(new PlayAnimatorStateTool());
                ToolRegistry.Register(new CreateAnimatorControllerTool());
                ToolRegistry.Register(new AddAnimatorStateTool());
                ToolRegistry.Register(new AddAnimatorTransitionTool());
                ToolRegistry.Register(new AddAnimatorParameterTool());
                ToolRegistry.Register(new AssignAnimatorControllerTool());
                ToolRegistry.Register(new AddAnimatorLayerTool());
                ToolRegistry.Register(new CreateBlendTreeTool());
                ToolRegistry.Register(new AddBlendTreeMotionTool());
                ToolRegistry.Register(new AddAnimationEventTool());
                // Collider authoring
                ToolRegistry.Register(new AutoFitColliderTool());
                ToolRegistry.Register(new AddCompoundCollidersTool());
                ToolRegistry.Register(new SetPhysicsMaterialTool());
                // Audio
                ToolRegistry.Register(new CreateAudioSource3DTool());
                ToolRegistry.Register(new SetAudioSourcePropsTool());
                ToolRegistry.Register(new PlayAudioSourceTool());
                ToolRegistry.Register(new AssignToMixerGroupTool());
                ToolRegistry.Register(new SetMixerParameterTool());
                ToolRegistry.Register(new PlayMixerSnapshotTool());
                ToolRegistry.Register(new SetupBackgroundMusicTool());
                ToolRegistry.Register(new CreateReverbZoneTool());
                ToolRegistry.Register(new AddAudioListenerTool());
                // Write — render settings
                ToolRegistry.Register(new SetRenderSettingsTool());
                // Batch + selection
                ToolRegistry.Register(new ApplyChangesetTool());
                ToolRegistry.Register(new SelectObjectTool());
                // Reflect — menu items + static method invocation. execute_menu_item and
                // invoke_static_method are write-gated; list_menu_items and
                // describe_static_method are read-only discovery aids.
                ToolRegistry.Register(new ListMenuItemsTool());
                ToolRegistry.Register(new DescribeStaticMethodTool());
                ToolRegistry.Register(new ExecuteMenuItemTool());
                ToolRegistry.Register(new InvokeStaticMethodTool());
                // Post-processing tools (create_volume, add_post_effect, set_post_effect_property,
                // apply_color_grading) live in com.maicon.scene-analyzer.Editor.PostFx and are
                // registered there via PostFxBootstrap when SRP Core is present.

                Server = new HttpMcpServer(ToolRegistry);
                Server.OnLog += msg => RecordEntry($"[server] {msg}");

                ChatSession = new ChatSession();
                ChatClient = new CoderApiClient(ChatSession);

                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
                AssemblyReloadEvents.afterAssemblyReload += OnAfterReload;

                // Auto-start: if EITHER user toggled AutoStartServer once OR
                // the sticky flag is set (meaning they clicked Start at some
                // point in this project — survives domain reload). Sticky is
                // the main resilience mechanism — survives Unity's frequent
                // post-script-change reloads without forcing the dev to click
                // Start every time.
                bool wantAutoStart = Settings.AutoStartServer || Settings.ServerStickyRunning;
                if (wantAutoStart && !string.IsNullOrEmpty(Settings.Token))
                {
                    try
                    {
                        Server.Start(Settings.Port, Settings.Token);
                        NotifyServerState($"online at port {Settings.Port}");
                    }
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

        /// Fired when the server transitions state (started / stopped / restarted
        /// after assembly reload). The chat panel subscribes so the dev sees a
        /// timestamped marker in the conversation when Unity recovers from a
        /// reload — no more "is the server back?" guesswork.
        public static event Action<string> OnServerStateChanged;

        private static void NotifyServerState(string state)
        {
            try { OnServerStateChanged?.Invoke(state); }
            catch (Exception e) { Debug.LogException(e); }
        }

        public static void StartServer()
        {
            if (string.IsNullOrEmpty(Settings.Token))
                Settings.Token = Settings.GenerateToken();
            Server.Start(Settings.Port, Settings.Token);
            // Mark sticky so subsequent assembly reloads auto-restart without
            // the dev clicking Start again.
            Settings.ServerStickyRunning = true;
            NotifyServerState($"started at port {Settings.Port}");
        }

        public static void StopServer()
        {
            Server.Stop();
            // Explicit user-initiated stop clears sticky — they don't want it
            // coming back automatically.
            Settings.ServerStickyRunning = false;
            NotifyServerState("stopped (sticky cleared)");
        }

        private static void OnAfterReload()
        {
            // The static ctor already auto-started the server (if sticky).
            // Notify the chat panel that we're back so the dev / agent knows
            // an in-flight 503 will succeed on retry.
            if (Server != null && Server.IsRunning)
                NotifyServerState($"back online at port {Settings.Port} after assembly reload");
        }

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
            try
            {
                // Pass a reason so HttpMcpServer writes 503 to in-flight tool
                // calls before the AppDomain is torn down. Without this, the
                // MCP client hangs on the dead socket until its 60s request
                // timeout fires (-32001).
                Server?.Stop("Unity assembly reload — server restarting; retry in ~5s");
            }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}
