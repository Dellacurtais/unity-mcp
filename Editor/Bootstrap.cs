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
using Maicon.SceneAnalyzer.Editor.ProjectTools;
using Maicon.SceneAnalyzer.Editor.SceneTools;
using Maicon.SceneAnalyzer.Editor.PrefabTools;
using Maicon.SceneAnalyzer.Editor.AssetTools;
using Maicon.SceneAnalyzer.Editor.PackageTools;
using Maicon.SceneAnalyzer.Editor.BuildTools;
using Maicon.SceneAnalyzer.Editor.SystemTools;
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
        internal static McpServerInstanceCoordinator ServerCoordinator { get; private set; }
        internal static McpServerInstanceSnapshot ServerInstanceState
            => ServerCoordinator?.Snapshot ?? McpServerInstanceSnapshot.Empty;
        internal static string ServerCoordinationError { get; private set; }
        public static DateTimeOffset DomainLoadedUtc { get; } = DateTimeOffset.UtcNow;

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
        public static List<CoderAgentInfo> CoderAgents { get; set; } = new();
        public static List<CoderSessionInfo> CoderSessions { get; set; } = new();
        public static event Action OnCoderProvidersChanged;
        public static event Action OnCoderAgentsChanged;
        public static event Action OnCoderSessionsChanged;

        public static void NotifyCoderProvidersChanged()
        {
            try { OnCoderProvidersChanged?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
        }

        public static void NotifyCoderAgentsChanged()
        {
            try { OnCoderAgentsChanged?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
        }

        public static void NotifyCoderSessionsChanged()
        {
            try { OnCoderSessionsChanged?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
        }

        public static void UpdateCoderSession(CoderSessionInfo session)
        {
            if (session == null || session.Id <= 0) return;
            var index = CoderSessions.FindIndex(item => item.Id == session.Id);
            if (index >= 0)
            {
                if (CoderSessions[index].SemanticallyEquals(session)) return;
                CoderSessions[index] = session;
            }
            else CoderSessions.Insert(0, session);
            NotifyCoderSessionsChanged();
        }

        public static event Action OnIssuesChanged;
        public static event Action OnServerCoordinationChanged;
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
                SceneAnalyzerVersion.Initialize();

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
                ToolRegistry.Register(new GetToolInventoryTool());
                ToolRegistry.Register(new GetServerHealthTool());
                ToolRegistry.Register(new SelfTestTool());
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

                // Project, compilation, quality
                ToolRegistry.Register(new GetEditorContextTool());
                ToolRegistry.Register(new GetProjectSettingsTool());
                ToolRegistry.Register(new GetCompilationStatusTool());
                ToolRegistry.Register(new GetCompilationDiagnosticsTool());
                ToolRegistry.Register(new RequestScriptCompilationTool());
                ToolRegistry.Register(new ListTagsLayersTool());
                ToolRegistry.Register(new PreviewProjectSettingsChangeTool());
                ToolRegistry.Register(new ApplyProjectSettingsChangeTool());
                ToolRegistry.Register(new ListQualityLevelsTool());
                ToolRegistry.Register(new SetQualitySettingsTool());
                // Multi-scene
                ToolRegistry.Register(new ListLoadedScenesTool());
                ToolRegistry.Register(new CreateSceneTool());
                ToolRegistry.Register(new OpenSceneTool());
                ToolRegistry.Register(new CloseSceneTool());
                ToolRegistry.Register(new SetActiveSceneTool());
                ToolRegistry.Register(new SaveSceneTool());
                ToolRegistry.Register(new SaveAllScenesTool());
                // Prefabs and Prefab Mode
                ToolRegistry.Register(new GetPrefabStageTool());
                ToolRegistry.Register(new OpenPrefabStageTool());
                ToolRegistry.Register(new ClosePrefabStageTool());
                ToolRegistry.Register(new ReadPrefabTool());
                ToolRegistry.Register(new CreatePrefabTool());
                ToolRegistry.Register(new CreatePrefabVariantTool());
                ToolRegistry.Register(new ListPrefabOverridesTool());
                ToolRegistry.Register(new ApplyPrefabOverrideTool());
                ToolRegistry.Register(new RevertPrefabOverrideTool());
                ToolRegistry.Register(new UnpackPrefabTool());
                // Assets/importers/VCS
                ToolRegistry.Register(new GetAssetDependenciesTool());
                ToolRegistry.Register(new GetAssetDependentsTool());
                ToolRegistry.Register(new CreateFolderTool());
                ToolRegistry.Register(new CopyAssetTool());
                ToolRegistry.Register(new MoveAssetTool());
                ToolRegistry.Register(new PreviewDeleteAssetsTool());
                ToolRegistry.Register(new ConfirmDeleteAssetsTool());
                ToolRegistry.Register(new ReadImportSettingsTool());
                ToolRegistry.Register(new PreviewImportSettingsTool());
                ToolRegistry.Register(new ApplyImportSettingsTool());
                ToolRegistry.Register(new ReimportAssetsTool());
                ToolRegistry.Register(new GetReimportJobTool());
                ToolRegistry.Register(new CancelReimportJobTool());
                ToolRegistry.Register(new GetVersionControlStatusTool());
                ToolRegistry.Register(new MakeAssetsEditableTool());
                // Packages and builds
                ToolRegistry.Register(new ListPackagesTool());
                ToolRegistry.Register(new SearchPackagesTool());
                ToolRegistry.Register(new PreviewAddPackageTool());
                ToolRegistry.Register(new ConfirmAddPackageTool());
                ToolRegistry.Register(new PreviewRemovePackageTool());
                ToolRegistry.Register(new ConfirmRemovePackageTool());
                ToolRegistry.Register(new GetPackageJobTool());
                ToolRegistry.Register(new ListBuildProfilesTool());
                ToolRegistry.Register(new ValidateBuildTool());
                ToolRegistry.Register(new PreviewBuildTool());
                ToolRegistry.Register(new StartBuildTool());
                ToolRegistry.Register(new GetBuildJobTool());
                ToolRegistry.Register(new CancelBuildJobTool());
                ToolRegistry.Register(new ReadBuildReportTool());

                // UI Toolkit, rendering, 2D, terrain, content and profiling
                ToolRegistry.Register(new GetRenderPipelineContextTool());
                ToolRegistry.Register(new ListShadersTool());
                ToolRegistry.Register(new ReadMaterialTool());
                ToolRegistry.Register(new CreateMaterialTool());
                ToolRegistry.Register(new SetMaterialPropertyTypedTool());
                ToolRegistry.Register(new GetLightingContextTool());
                ToolRegistry.Register(new ListLodGroupsTool());
                ToolRegistry.Register(new ConfigureLodGroupTool());
                ToolRegistry.Register(new SetStaticEditorFlagsTool());
                ToolRegistry.Register(new Physics2DQueryTool());
                ToolRegistry.Register(new GetPhysics2DSettingsTool());
                ToolRegistry.Register(new SetPhysics2DSettingsTool());
                ToolRegistry.Register(new ListSpritesTool());
                ToolRegistry.Register(new CreateTilemapTool());
                ToolRegistry.Register(new SetTilemapCellsTool());
                ToolRegistry.Register(new ReadTerrainTool());
                ToolRegistry.Register(new CreateTerrainTool());
                ToolRegistry.Register(new SetTerrainHeightsTool());
                ToolRegistry.Register(new ListUiDocumentsTool());
                ToolRegistry.Register(new CreatePanelSettingsTool());
                ToolRegistry.Register(new CreateUiDocumentTool());
                ToolRegistry.Register(new ReadUiAssetTool());
                ToolRegistry.Register(new WriteUiAssetTool());
                ToolRegistry.Register(new CreateAnimationClipAssetTool());
                ToolRegistry.Register(new SetAnimationFloatCurveTool());
                ToolRegistry.Register(new CreateAvatarMaskTool());
                ToolRegistry.Register(new GetPerformanceSnapshotTool());

                // Mesh analytics (inspect_submeshes, detect_sharp_edges, detect_planes,
                // sample_points_on_region, render_object_views, render_region_highlight)
                // live in com.maicon.scene-analyzer.Editor.MeshAnalytics and are
                // registered there via MeshAnalyticsBootstrap.

                // Post-processing tools (create_volume, add_post_effect, set_post_effect_property,
                // apply_color_grading) live in com.maicon.scene-analyzer.Editor.PostFx and are
                // registered there via PostFxBootstrap when SRP Core is present.

                Server = new HttpMcpServer(ToolRegistry);
                Server.OnLog += msg => RecordEntry($"[server] {msg}");
                try
                {
                    ServerCoordinator = McpServerInstanceCoordinator.CreateForCurrentProject(
                        UnityProjectRoot, SceneAnalyzerVersion.Current);
                    ServerCoordinator.StateChanged += OnServerCoordinatorStateChanged;
                    ServerCoordinator.CommandRequested += OnServerCoordinatorCommand;
                }
                catch (Exception e)
                {
                    ServerCoordinationError = e.Message;
                    Debug.LogWarning($"[scene-analyzer] MCP instance coordination unavailable: {e.Message}");
                }

                ChatSession = new ChatSession();
                ChatClient = new CoderApiClient(ChatSession);

                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
                AssemblyReloadEvents.afterAssemblyReload += OnAfterReload;
                EditorApplication.quitting += OnQuitting;

                // Sticky remains global so every project shares one client endpoint;
                // the coordinator decides which Unity process may bind it.
                bool wantAutoStart = Settings.AutoStartServer || Settings.ServerStickyRunning;
                if (wantAutoStart && !string.IsNullOrEmpty(Settings.Token))
                {
                    try
                    {
                        TryStartServer(
                            Settings.Port,
                            Settings.Token,
                            null,
                            false,
                            $"online at port {Settings.Port}",
                            false);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[scene-analyzer] auto-start failed: {e.Message}");
                    }
                }

                // Re-poll after handlers are subscribed so a handover that crossed an
                // assembly reload is resumed immediately instead of waiting for a tick.
                ServerCoordinator?.PollNow();
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
            TryStartServer(
                Settings.Port,
                Settings.Token,
                null,
                true,
                $"started at port {Settings.Port}",
                true);
        }

        public static void StopServer()
        {
            Server.Stop();
            try { ServerCoordinator?.ReleaseExplicit(); }
            catch (Exception e) { Debug.LogWarning($"[scene-analyzer] failed to release MCP lease: {e.Message}"); }
            Settings.ServerStickyRunning = false;
            NotifyServerState("stopped (sticky cleared)");
        }

        public static void RestartServer()
        {
            if (string.IsNullOrEmpty(Settings.Token))
                Settings.Token = Settings.GenerateToken();
            var port = Server != null && Server.IsRunning ? Server.Port : Settings.Port;
            Server?.Stop("MCP server settings changed — retry shortly");
            TryStartServer(
                port,
                Settings.Token,
                null,
                true,
                $"restarted at port {port}",
                true);
        }

        public static void BringServerHere()
        {
            if (ServerCoordinator == null)
                throw new InvalidOperationException(ServerCoordinationError ?? "MCP instance coordination is unavailable.");
            var requestId = ServerCoordinator.RequestHandover();
            RecordEntry($"[server] transfer requested for this project ({requestId.Substring(0, 8)})");
            NotifyServerState("transfer requested for this project");
        }

        private static bool TryStartServer(
            int port,
            string token,
            string handoverRequestId,
            bool markSticky,
            string successState,
            bool throwWhenUnavailable)
        {
            if (Server == null) throw new InvalidOperationException("MCP server is not initialized.");
            if (Server.IsRunning) return true;

            var reservation = ServerCoordinator == null
                ? McpServerAcquireResult.Rejected(
                    port,
                    ServerCoordinationError ?? "MCP instance coordination is unavailable; refusing an uncoordinated port bind.")
                : string.IsNullOrEmpty(handoverRequestId)
                    ? ServerCoordinator.TryAcquire(port, token)
                    : ServerCoordinator.TryAcquireHandover(handoverRequestId, token);

            if (!reservation.Acquired)
            {
                if (throwWhenUnavailable) throw new InvalidOperationException(reservation.Message);
                RecordEntry($"[server] {reservation.Message}");
                return false;
            }

            Settings.Port = reservation.Port;
            try
            {
                Server.Start(reservation.Port, token);
                ServerCoordinator?.MarkRunning(reservation.LeaseId, handoverRequestId);
                if (markSticky) Settings.ServerStickyRunning = true;
                NotifyServerState(successState);
                return true;
            }
            catch (Exception e)
            {
                try
                {
                    if (Server.IsRunning)
                        Server.Stop("MCP coordination failed during startup");
                }
                catch (Exception stopError)
                {
                    Debug.LogWarning($"[scene-analyzer] failed to stop partially started MCP server: {stopError.Message}");
                }

                try { ServerCoordinator?.FailStart(reservation.LeaseId, e.Message, handoverRequestId); }
                catch (Exception coordinationError)
                {
                    Debug.LogWarning($"[scene-analyzer] failed to roll back MCP lease: {coordinationError.Message}");
                }
                throw;
            }
        }

        private static void OnServerCoordinatorStateChanged(McpServerInstanceSnapshot _)
        {
            try { OnServerCoordinationChanged?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        private static void OnServerCoordinatorCommand(McpServerCoordinationCommand command)
        {
            try
            {
                switch (command.Kind)
                {
                    case McpServerCoordinationCommandKind.ReleaseForHandover:
                        Server?.Stop($"MCP server moving to {command.DestinationProjectName}; retry shortly");
                        ServerCoordinator.ReleaseForHandover(command.RequestId);
                        NotifyServerState($"moved to {command.DestinationProjectName} (port {command.Port})");
                        break;

                    case McpServerCoordinationCommandKind.AcquireAfterHandover:
                        TryStartServer(
                            command.Port,
                            Settings.Token,
                            command.RequestId,
                            true,
                            $"moved here from {command.SourceProjectName} at port {command.Port}",
                            true);
                        break;

                    case McpServerCoordinationCommandKind.RestoreAfterFailedHandover:
                        TryStartServer(
                            command.Port,
                            Settings.Token,
                            null,
                            true,
                            $"restored at port {command.Port} after failed transfer",
                            true);
                        break;
                }
            }
            catch (Exception e)
            {
                try { ServerCoordinator?.FailHandover(command.RequestId, e.Message); }
                catch (Exception coordinationError)
                {
                    Debug.LogWarning($"[scene-analyzer] failed to mark MCP transfer failure: {coordinationError.Message}");
                }

                if (command.Kind == McpServerCoordinationCommandKind.ReleaseForHandover
                    && Server != null
                    && !Server.IsRunning)
                {
                    try
                    {
                        TryStartServer(
                            command.Port,
                            Settings.Token,
                            null,
                            true,
                            $"restored at port {command.Port} after failed transfer release",
                            true);
                    }
                    catch (Exception restoreError)
                    {
                        Debug.LogError($"[scene-analyzer] failed to restore MCP server after transfer release failure: {restoreError.Message}");
                    }
                }

                RecordEntry($"[server] transfer failed: {e.Message}");
                NotifyServerState($"transfer failed: {e.Message}");
            }
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
            try { ChatClient?.Dispose(); }
            catch (Exception e) { Debug.LogException(e); }

            try { ServerCoordinator?.PrepareForReload(); }
            catch (Exception e) { Debug.LogException(e); }

            try
            {
                // Pass a reason so HttpMcpServer writes 503 to in-flight tool
                // calls before the AppDomain is torn down. Without this, the
                // MCP client hangs on the dead socket until its 60s request
                // timeout fires (-32001).
                Server?.Stop("Unity assembly reload — server restarting; retry in ~5s");
            }
            catch (Exception e) { Debug.LogException(e); }
            finally
            {
                try { ServerCoordinator?.Dispose(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        private static void OnQuitting()
        {
            try { ChatClient?.Dispose(); }
            catch (Exception e) { Debug.LogException(e); }

            try { Server?.Stop("Unity Editor is quitting"); }
            catch (Exception e) { Debug.LogException(e); }

            try { ServerCoordinator?.ReleaseForShutdown(); }
            catch (Exception e) { Debug.LogException(e); }
            finally
            {
                try { ServerCoordinator?.Dispose(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }
    }
}
