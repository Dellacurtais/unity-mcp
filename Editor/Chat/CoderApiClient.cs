using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEngine;
using FormattingNs = Newtonsoft.Json.Formatting;

namespace Maicon.SceneAnalyzer.Editor.Chat
{
    /// HTTP client for the CodeStudio coder agent. Posts a user message to
    /// `/api/coder/sessions/<id>/messages` and parses the SSE response stream
    /// line-by-line, dispatching events into a ChatSession on the editor main
    /// thread.
    ///
    /// Reference (CodeStudio sources): src/apps/http-api/server.ts (server),
    /// src/apps/dashboard/hooks/agent-stream/openStream.ts
    /// (browser client mirrored here).
    public sealed partial class CoderApiClient
    {
        private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

        private CancellationTokenSource _cts;
        private CancellationTokenSource _syncCts;
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly ChatSession _session;
        private int _disposed;
        private readonly ConcurrentQueue<CoderStreamEvent> _streamEvents = new();
        private int _streamDrainScheduled;
        private long _enqueuedStreamEvents;
        private long _appliedStreamEvents;
        private int _attachedStreamActive;
        private int _abortRequested;
        private CancellationTokenSource _activeAttachCts;
        private readonly Dictionary<string, int> _cursorlessReplayAssistantSignatures =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> _cursorlessReplayToolIds =
            new HashSet<string>(StringComparer.Ordinal);

        private const int MaxEventsPerDrain = 96;
        private const double MaxDrainMilliseconds = 3.5;
        private static readonly TimeSpan InteractionTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan AbortRequestTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan AbortStreamGrace = TimeSpan.FromSeconds(2);

        // Sessions that haven't received their context preamble yet. A session
        // gets added here the moment it's created (either via auto-create on
        // Send, or via the "New" button) and removed on the first message that
        // actually goes out. Locked because creation and send paths can race
        // on different Task.Run continuations.
        private readonly HashSet<string> _pendingPreambleFor = new(StringComparer.Ordinal);
        private readonly object _preambleLock = new();

        private void MarkPreamblePending(string sessionId)
        {
            sessionId = CoderSessionIdentity.Normalize(sessionId);
            if (string.IsNullOrEmpty(sessionId)) return;
            lock (_preambleLock) _pendingPreambleFor.Add(sessionId);
        }

        private bool ConsumePendingPreamble(string sessionId)
        {
            sessionId = CoderSessionIdentity.Normalize(sessionId);
            if (string.IsNullOrEmpty(sessionId)) return false;
            lock (_preambleLock) return _pendingPreambleFor.Remove(sessionId);
        }

        private bool TryBeginForegroundOperation(out CancellationTokenSource operationCts)
        {
            operationCts = null;
            if (Volatile.Read(ref _disposed) != 0) return false;

            var candidate = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            if (Interlocked.CompareExchange(ref _cts, candidate, null) == null)
            {
                operationCts = candidate;
                return true;
            }

            candidate.Dispose();
            return false;
        }

        private void CompleteForegroundOperation(CancellationTokenSource operationCts)
        {
            if (operationCts == null) return;
            Interlocked.CompareExchange(ref _cts, null, operationCts);
            operationCts.Dispose();
        }

        public CoderApiClient(ChatSession session)
        {
            _session = session;
        }

        public bool IsRunning
            => Volatile.Read(ref _cts) != null || Volatile.Read(ref _attachedStreamActive) != 0;

        public bool IsStopping => Volatile.Read(ref _abortRequested) != 0;

        public bool IsSyncing => _syncCts != null && !_syncCts.IsCancellationRequested;

        public void Cancel()
        {
            var cts = Volatile.Read(ref _cts);
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
        }

        public void RequestAbort(string serverUrl, string token, string sessionId)
        {
            if (!IsRunning || IsStopping) return;
            sessionId = CoderSessionIdentity.Normalize(sessionId);
            if (!CoderSessionIdentity.IsValid(sessionId))
            {
                _session.SetConnectionState(
                    ChatConnectionState.Error,
                    "Não foi possível parar: sessão inválida.");
                return;
            }
            if (Interlocked.CompareExchange(ref _abortRequested, 1, 0) != 0) return;

            _session.SetConnectionState(ChatConnectionState.Running, "Parando execução…");
            _ = Task.Run(async () =>
            {
                _ = CompleteAbortAfterGraceAsync();
                try
                {
                    var url = $"{serverUrl.TrimEnd('/')}/api/coder/sessions/{sessionId}/abort";
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                    timeout.CancelAfter(AbortRequestTimeout);
                    using var request = CreateRequest(HttpMethod.Post, url, token);
                    using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                    var responseBody = response.IsSuccessStatusCode
                        ? null
                        : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException(
                            $"HTTP {(int)response.StatusCode}: {responseBody}");
                }
                catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
                catch (Exception e)
                {
                    if (Interlocked.Exchange(ref _abortRequested, 0) != 0)
                    {
                        await MainThreadDispatcher.RunAsync(() => _session.SetConnectionState(
                            ChatConnectionState.Running,
                            $"Falha ao parar: {e.Message}"));
                    }
                }
            });
        }

        private async Task CompleteAbortAfterGraceAsync()
        {
            try
            {
                await Task.Delay(AbortStreamGrace, _lifetimeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                return;
            }

            if (!IsStopping) return;
            CancelActiveStreams();
            await MainThreadDispatcher.RunAsync(() =>
            {
                if (Interlocked.Exchange(ref _abortRequested, 0) != 0)
                    _session.AbortAssistant();
            });
        }

        private void CancelActiveStreams()
        {
            var foreground = Volatile.Read(ref _cts);
            var attached = Volatile.Read(ref _activeAttachCts);
            try { foreground?.Cancel(); } catch { }
            try { attached?.Cancel(); } catch { }
        }

        public void StopSessionSync()
        {
            var cts = Interlocked.Exchange(ref _syncCts, null);
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Cancel();
            CancelActiveStreams();
            StopSessionSync();
            try { _lifetimeCts.Cancel(); } catch { }
            while (_streamEvents.TryDequeue(out _)) { }
            _lifetimeCts.Dispose();
        }

        public void Send(string serverUrl, string token, string sessionId, string content,
            string profile, string agent)
        {
            // Caller is the main thread (button click). Don't dispatch + wait —
            // would deadlock against the dispatcher running on EditorApplication.update.
            if (!TryBeginForegroundOperation(out var operationCts))
            {
                _session.AddSystemNote("a request is already in flight — cancel it first");
                return;
            }

            var token2 = operationCts.Token;
            // Capture EVERYTHING from Settings on the main thread before
            // dispatching — EditorPrefs is not guaranteed thread-safe.
            // (CoderProjectId / CoderProjectWorkdir are read inside
            // ResolveProjectAsync via its own MainThreadDispatcher hop.)
            var unityRoot = Bootstrap.UnityProjectRoot;
            var injectContext = Settings.CoderAutoInjectContext;
            var customPreamble = Settings.CoderCustomPreamble;
            var snapProvider = Settings.CoderProvider;
            var snapModel = Settings.CoderModel;
            var snapThinking = Settings.CoderThinking;
            var sessionOptions = CoderSessionOptions.CaptureCurrent();

            _ = Task.Run(async () =>
            {
                try
                {
                    var effectiveSessionId = CoderSessionIdentity.Normalize(sessionId);

                    if (string.IsNullOrEmpty(effectiveSessionId))
                    {
                        // Always go through the resolver — it cheaply returns
                        // the cached id when the workdir still matches, and
                        // re-detects on drift (the bug prior to v0.8.1 was
                        // skipping this whenever any id was cached).
                        int projectId = await ResolveProjectAsync(serverUrl, token, unityRoot, token2);
                        if (projectId <= 0)
                        {
                            await MainThreadDispatcher.RunAsync(() => _session.AddError(
                                "step 1 failed (no project id) — aborting before session/message"));
                            return;
                        }

                        await MainThreadDispatcher.RunAsync(() => _session.AddSystemNote(
                            $"[2/3] creating session under project {projectId}…"));
                        effectiveSessionId = await CreateSessionAsync(
                            serverUrl, token, projectId,
                            $"Unity chat — {DateTime.Now:HH:mm:ss}",
                            snapProvider, snapModel, agent, sessionOptions, token2);
                        if (string.IsNullOrEmpty(effectiveSessionId))
                        {
                            await MainThreadDispatcher.RunAsync(() => _session.AddError(
                                "step 2 failed (no session id) — aborting before message"));
                            return;
                        }
                        await MainThreadDispatcher.RunAsync(() =>
                        {
                            Settings.CoderSessionId = effectiveSessionId;
                            _session.AddSystemNote($"[2/3] session id = {effectiveSessionId}");
                        });
                        MarkPreamblePending(effectiveSessionId);
                    }

                    var contentToSend = content;
                    var injectThisTime = injectContext && ConsumePendingPreamble(effectiveSessionId);
                    if (injectThisTime)
                    {
                        var preamble = string.IsNullOrEmpty(customPreamble)
                            ? BuildDefaultPreamble(unityRoot)
                            : customPreamble;
                        contentToSend = preamble + "\n\n---\n\n" + content;
                        await MainThreadDispatcher.RunAsync(() => _session.AddSystemNote(
                            "(injected unity context preamble on first message of this session)"));
                    }

                    await MainThreadDispatcher.RunAsync(() => _session.AddSystemNote(
                        $"[3/3] POST /api/coder/sessions/{effectiveSessionId}/messages " +
                        (snapThinking ? "(🧠 think mode)" : "") + " …"));
                    await SendAsync(serverUrl, token, effectiveSessionId, contentToSend, profile, agent, snapThinking, token2);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    try
                    {
                        await MainThreadDispatcher.RunAsync(() => _session.AddError(
                            $"unhandled error in send pipeline: {e.GetType().Name}: {e.Message}"));
                    }
                    catch { /* last-ditch — don't mask the original */ }
                }
                finally
                {
                    CompleteForegroundOperation(operationCts);
                }
            });
        }

        public static string BuildDefaultPreamble(string unityRoot)
        {
            return
                "[unity-context — auto-injected by the Unity MCP plugin on the first message of this session]\n\n" +
                $"This request originated in the Unity Editor running at `{unityRoot ?? "<unknown>"}`. " +
                $"The plugin-managed agent `{UnityBuildAgentManifest.SelectionId}` activates the external `unity` tier. " +
                "Verify the live route with `ext_unity__get_server_health` before trusting this context. " +
                "The Unity MCP plugin exposes scene tools through the registered MCP server `unity`, prefixed `ext_unity__`:\n" +
                "- **Read / inspect**: `read_hierarchy`, `read_scene`, `read_component`, `get_project_meta`, `screenshot_scene_view`.\n" +
                "- **Discover assets & types**: `list_assets`, `search_assets`, `read_asset_metadata`, `find_references_to_asset`, " +
                "`list_component_types` (every Component subclass loaded — built-in + user MonoBehaviours), " +
                "`describe_component_type` (schema of a type without an instance: serialized fields, attributes like Range/Tooltip, RequireComponent list — works for ANY type including ScriptableObject subclasses), " +
                "`list_scriptable_object_types` (every concrete ScriptableObject subclass loaded; surfaces [CreateAssetMenu] entries — pair with read_scriptable_object), " +
                "`read_scriptable_object` (dump fields of an SO asset — analog of read_component but for assets, not Components on a GO).\n" +
                "- **Create**: `create_primitive`, `create_empty`, `instantiate_prefab`, `create_light`, `create_camera`, " +
                "`create_audio_source`, `create_particle_system`. " +
                "ScriptableObject assets: `create_scriptable_object` (className + path + optional initialFields), " +
                "`set_so_property` (set a SerializedProperty on the SO asset), " +
                "`duplicate_scriptable_object` (clone to a new path), " +
                "`modify_array_property` (append/insert/remove/set/resize/clear on any array field — works for Component fields AND SO fields, handles Unity's `Array.size`/`Array.data[N]` plumbing internally).\n" +
                "- **Modify scene**: `set_transform`, `set_camera_props`, `look_at` (point a camera/object at a target), " +
                "`set_property` (any SerializedProperty), `set_material`, " +
                "`add_component`, `remove_component`, `set_enabled` (Behaviour/Renderer/Collider on/off), " +
                "`set_active`, `rename_object`, `set_tag`, `set_layer`, `parent_object`, `duplicate_object` (clones with components+children, supports offset/position), `delete_object`.\n" +
                "- **Particles**: `set_particle_module` (typed setter for main/emission/shape/colorOverLifetime/sizeOverLifetime/velocityOverLifetime/noise/trails/renderer/collision), " +
                "`play_particle_preview` (play/stop/pause/clear/restart/simulate the in-editor preview).\n" +
                "- **Animation (Mecanim)**: `list_animator_controllers`, `read_animator_controller` (states/transitions/parameters/layers), " +
                "`read_animation_clip` (curves + events), `read_avatar` (humanoid bones), `read_animator_state` (runtime state). " +
                "Authoring: `create_animator_controller`, `add_animator_parameter` (Float/Int/Bool/Trigger), `add_animator_state` (with optional motion clip), " +
                "`add_animator_transition` (from/to + conditions), `add_animator_layer` (with weight/avatarMask), " +
                "`create_blend_tree` + `add_blend_tree_motion` (1D/2D), `add_animation_event` (function call at time T), " +
                "`assign_controller_to_animator` (set Animator.runtimeAnimatorController). Runtime: `set_animator_parameter`, `play_animator_state`.\n" +
                "- **Timeline** (when `com.unity.timeline` is installed): `create_timeline_asset`, `add_timeline_track` " +
                "(Animation/Audio/Activation/Control/Signal/Marker), `add_timeline_clip`, `assign_timeline_to_director`, `play_timeline`.\n" +
                "- **Physics queries**: `raycast` (single or all hits, with layerMask + trigger filter), `overlap_query` (Box/Sphere/Capsule), `sweep_query`, " +
                "`check_ground` (raycast down from a GO and report what's below — answers \"is X on the sidewalk/floor?\" — returns hit's tag/layer), " +
                "`compute_bounds` (world AABB), `query_collision_layers` (per-layer collision matrix).\n" +
                "- **Scene classification & spatial query**: `classify_scene` (single-shot bucket summary by tag/layer/components/hierarchy/spatial/prefab), " +
                "`query_scene` (combined filter: tag + layer + componentTypes (must-have-all) + nameContains + pathContains + withinRadius, sortBy name/distance/depth).\n" +
                "- **Collider authoring**: `auto_fit_collider` (Box/Sphere/Capsule/Mesh sized to renderer bounds), " +
                "`add_compound_colliders` (one primitive per child renderer — better fit for buildings/vehicles), `set_physics_material`.\n" +
                "- **NavMesh — queries** (built-in, edit-time): `navmesh_sample_position` (project a point onto the navmesh — answers \"is this walkable?\"), " +
                "`navmesh_calculate_path` (full path A→B with status/corners), `navmesh_raycast` (line-of-sight on navmesh — note INVERTED convention: returns BLOCKED when line crosses non-walkable), " +
                "`navmesh_find_closest_edge`. areaMask accepts int, area name string, or array of names. " +
                "**Bake & runtime**: `bake_navmesh` (legacy global), `set_navmesh_agent` (combo: optional setters for destination/speed/etc + always returns full agent state). " +
                "**Modern per-surface** (when `com.unity.ai.navigation` is installed): `list_navmesh_surfaces`, `bake_navmesh_surface` (one surface or all), " +
                "`add_navmesh_surface` (configure agentTypeID/collectObjects/layerMask/voxelSize + optional immediate bake), `add_navmesh_modifier` (override area on geometry, or as a volume box).\n" +
                "- **Audio — sources & runtime**: `create_audio_source` (basic), `create_audio_source_3d` (3D point source with rolloff/min/max distance — use for ambient point emitters like an engine box), " +
                "`set_audio_source_props` (typed setter for every common AudioSource field), `play_audio_source` (play/stop/pause/unpause/playOneShot — only emits in Play Mode), " +
                "`add_audio_listener` (auto-removes others), `inspect_audio_setup` (audit: listeners, sources by mixer/2D/3D/loop, reverb zones, warnings).\n" +
                "- **Audio — mixers**: `list_audio_mixers`, `read_audio_mixer` (groups + snapshots + exposed parameters with current values), `assign_to_mixer_group` " +
                "(route a source through e.g. 'SFX' or 'MainMixer/Music'), `set_mixer_parameter` (runtime exposed param — must already be Exposed in the editor), " +
                "`play_mixer_snapshot` (transitions over `transitionSeconds` — swap moods).\n" +
                "- **Audio — convenience**: `setup_background_music` (creates/reuses a `__BGM__` GameObject with 2D loop AudioSource for the given clip, optional mixer routing), " +
                "`create_reverb_zone` (AudioReverbZone with preset Cave/Hall/Forest/etc.).\n" +
                "Footstep pattern: write a `Footsteps.cs` MonoBehaviour with `PlayFootstep(AnimationEvent)` that calls `PlayOneShot`, then refresh_assets, add_component on the character, then `add_animation_event` at each step time on the walk/run clip.\n" +
                "- **Render & post-fx** (URP/HDRP): `set_render_settings`, `create_volume`, `add_post_effect`, " +
                "`remove_post_effect`, `set_post_effect_property`, `apply_color_grading`, " +
                "`list_post_effects` (effects on a volume OR all VolumeComponent types available), " +
                "`describe_post_effect` (parameter schema of an effect type).\n" +
                "- **Batch & selection**: `apply_changeset` (atomic batch — all create/modify ops above as ONE Undo group, " +
                "with `label`+`{ref:…}` chaining for parent references), `select_object`.\n" +
                "- **Console / build verification**: `read_console` (Debug.Log/Warning/Error + uncaught exceptions + structured compile errors with file:line; filter by severity / since-seq / messageContains), " +
                "`clear_console` (mark a clean slate before a complex op so the next read_console only shows what happened during it), " +
                "`wait_for_compile_clean` (refresh + cooperatively wait for compile + report errors+warnings since start in a single call).\n\n" +
                "When asked to add or tweak a component you don't know yet, the typical loop is: " +
                "`list_component_types` → `describe_component_type` → `add_component` → `set_property` per field. " +
                "For ScriptableObject data assets, the equivalent loop is " +
                "`list_scriptable_object_types` → `describe_component_type` (works for SO too) → `create_scriptable_object` (with `initialFields`) → `set_so_property` for later edits. " +
                "For arrays/lists on either Components or SOs, prefer `modify_array_property` over `set_property` with the raw `Array.size`/`Array.data[N]` paths.\n\n" +
                "**After creating a NEW `.cs` MonoBehaviour script** (via filesystem write), prefer `wait_for_compile_clean` over `refresh_assets` — it tells you in ONE call whether the compile succeeded AND surfaces any error with file:line. If you still call `refresh_assets`, follow up with `read_console severity=errors_only since=<seq before refresh>` to verify there were no compile errors before calling `add_component`.\n\n" +
                "**Avoid redundant tool calls**: the dashboard refuses identical tool+args calls after a few retries (`Refused: was called with these exact arguments N times`). Strategy: keep findings as plain text in your reply (instead of recalling the same query), change one argument when you do need fresh data, and after any error change strategy rather than repeat. The Unity tool responses end with `[unity-call #N @ HH:mm:ss.fff]` so revalidation against an unchanged scene works — but only when the INPUT args differ.\n\n" +
                "Skills are optional specialization, not a gate before Unity work. For routine scene inspection, authoring, save and validation, call the relevant `ext_unity__*` tools directly. Use `list_skill_namespaces`, `get_skills_list`, `get_skill_content` or `invoke_skill` only when a reusable specialist workflow materially helps; never repeat discovery or delay an explicit Editor operation just to invoke a skill.\n\n" +
                "Subsequent messages in this session don't need this preamble — the agent should remember. Now respond to the user message that follows.";
        }

        /// Public entry point so the UI's "New" button can create a session
        /// without sending a message. Fire-and-forget; status is reported via
        /// the ChatSession transcript.
        ///
        /// The `projectId` parameter is intentionally IGNORED — `ResolveProjectAsync`
        /// auto-detects/creates the project that matches the current Unity
        /// workdir. Bypassing this with a hard-coded id was the v0.8.0 bug
        /// where new sessions silently attached to whatever happened to have
        /// id 1 in the dashboard. Kept in the signature for source-compat.
        public void CreateSession(string serverUrl, string token, int projectId, string title,
            string provider, string model, string agent)
        {
            if (!TryBeginForegroundOperation(out var operationCts))
            {
                _session.AddSystemNote("a request is already in flight — cancel it first");
                return;
            }
            var ct = operationCts.Token;
            var unityRoot = Bootstrap.UnityProjectRoot;
            var sessionOptions = CoderSessionOptions.CaptureCurrent();
            _ = Task.Run(async () =>
            {
                try
                {
                    var resolvedProjectId = await ResolveProjectAsync(serverUrl, token, unityRoot, ct);
                    if (resolvedProjectId <= 0)
                    {
                        await MainThreadDispatcher.RunAsync(() => _session.AddError(
                            "create session aborted — couldn't resolve a project for this Unity workdir"));
                        return;
                    }

                    var id = await CreateSessionAsync(
                        serverUrl, token, resolvedProjectId, title, provider, model, agent, sessionOptions, ct);
                    if (CoderSessionIdentity.IsValid(id))
                    {
                        // Mark the session as needing the preamble on its first
                        // outgoing message — the New button creates the session
                        // here, but the message hasn't been sent yet, so the
                        // Send path won't see "justCreatedSession" anymore.
                        MarkPreamblePending(id);
                        await MainThreadDispatcher.RunAsync(() =>
                        {
                            Settings.CoderSessionId = id;
                            _session.AddSystemNote(
                                $"session #{id} created (project_id={resolvedProjectId}) — preamble will be injected on first message");
                        });
                    }
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    await MainThreadDispatcher.RunAsync(() => _session.AddError(
                        $"create session pipeline error: {e.GetType().Name}: {e.Message}"));
                }
                finally { CompleteForegroundOperation(operationCts); }
            });
        }

        /// Fire-and-forget for the UI: ensures a CodeStudio project exists matching
        /// the Unity workdir (creates if needed) and stores its id in Settings.
        public void EnsureProject(string serverUrl, string token, string unityRoot)
        {
            if (!TryBeginForegroundOperation(out var operationCts))
            {
                _session.AddSystemNote("a request is already in flight — cancel it first");
                return;
            }
            var ct = operationCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await EnsureProjectAsync(serverUrl, token, unityRoot, ct);
                    if (result.Id > 0)
                    {
                        await MainThreadDispatcher.RunAsync(() =>
                        {
                            Settings.CoderProjectId = result.Id;
                            Settings.CoderProjectWorkdir = unityRoot;
                            Settings.CoderProjectName = result.Name;
                            _session.AddSystemNote(
                                $"project #{result.Id} '{result.Name}' ready (root_path={unityRoot})");
                        });
                    }
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    await MainThreadDispatcher.RunAsync(() => _session.AddError(
                        $"ensure project pipeline error: {e.GetType().Name}: {e.Message}"));
                }
                finally { CompleteForegroundOperation(operationCts); }
            });
        }

        /// Answer an `ask_user` tool call. POSTs the dev's answer to
        /// `/api/coder/sessions/<id>/answer` so the paused run continues.
        public void AnswerQuestion(string serverUrl, string token, string sessionId, string questionId, string answer)
        {
            var key = "question:" + (questionId ?? "");
            var body = new JObject { ["answer"] = answer ?? "" };
            if (!string.IsNullOrEmpty(questionId)) body["question_id"] = questionId;
            _ = PostInteractionAsync(serverUrl, token, sessionId, "answer", key, body,
                $"Pergunta respondida: {answer}");
        }

        /// Approve / reject a plan submitted by the agent via `submit_plan`.
        public void RespondToPlan(
            string serverUrl,
            string token,
            string sessionId,
            string planId,
            bool approved,
            string feedback,
            bool aborted = false)
        {
            var key = "plan:" + (planId ?? "");
            var body = BuildPlanResponseBody(planId, approved, feedback, aborted);
            _ = PostInteractionAsync(serverUrl, token, sessionId, "plan-response", key, body,
                approved && !aborted
                    ? "Plano aprovado"
                    : aborted
                        ? "Plano rejeitado"
                        : "Alterações solicitadas no plano");
        }

        internal static JObject BuildPlanResponseBody(
            string planId, bool approved, string feedback, bool aborted)
        {
            var body = new JObject
            {
                ["approved"] = approved && !aborted,
                ["feedback"] = feedback ?? "",
                ["aborted"] = aborted,
            };
            if (!string.IsNullOrEmpty(planId)) body["plan_id"] = planId;
            return body;
        }

        public void RespondToSpec(
            string serverUrl,
            string token,
            string sessionId,
            string specId,
            string specPath,
            bool approved,
            string requestedChanges)
        {
            var key = "spec:" + (specId ?? "");
            var body = new JObject
            {
                ["approved"] = approved,
                ["requested_changes"] = requestedChanges ?? "",
                ["spec_path"] = specPath ?? "",
            };
            _ = PostInteractionAsync(serverUrl, token, sessionId, "spec-response", key, body,
                approved ? "Especificação aprovada" : "Alterações solicitadas na especificação");
        }

        public void RespondToDecision(
            string serverUrl,
            string token,
            string sessionId,
            string decisionId,
            JObject answers)
        {
            var key = "decision:" + (decisionId ?? "");
            var body = new JObject { ["answers"] = answers ?? new JObject() };
            _ = PostInteractionAsync(serverUrl, token, sessionId, "decide", key, body,
                "Decisão enviada");
        }

        public void RespondToTasks(string serverUrl, string token, string sessionId, string toolUseId, bool approved)
        {
            var key = "tasks:" + (toolUseId ?? "");
            var body = new JObject { ["approved"] = approved };
            if (!string.IsNullOrEmpty(toolUseId)) body["tool_use_id"] = toolUseId;
            _ = PostInteractionAsync(serverUrl, token, sessionId, "tasks-response", key, body,
                approved ? "Tarefas aprovadas" : "Tarefas rejeitadas");
        }

        public void RespondToToolApproval(string serverUrl, string token, string sessionId,
            string toolUseId, bool approved, string note, bool alwaysAllow)
        {
            var key = "approval:" + (toolUseId ?? "");
            var body = new JObject
            {
                ["tool_use_id"] = toolUseId ?? "",
                ["approved"] = approved,
                ["note"] = note ?? "",
                ["always_allow"] = approved && alwaysAllow,
            };
            _ = PostInteractionAsync(serverUrl, token, sessionId, "tool-approval", key, body,
                approved ? "Comando aprovado" : "Comando negado");
        }

        public void SwitchProvider(string serverUrl, string token, string sessionId, string provider, string model)
            => MutateSession(serverUrl, token, sessionId, HttpMethod.Post, "switch",
                new JObject { ["provider"] = provider ?? "", ["model"] = model ?? "" },
                $"Modelo ativo alterado para {provider} · {model}");

        public void SetSessionAgent(string serverUrl, string token, string sessionId, string agent)
            => MutateSession(serverUrl, token, sessionId, new HttpMethod("PATCH"), "agent",
                new JObject { ["agent"] = string.IsNullOrWhiteSpace(agent) ? JValue.CreateNull() : agent });

        public void SetReasoningEffort(string serverUrl, string token, string sessionId, string effort)
            => MutateSession(serverUrl, token, sessionId, new HttpMethod("PATCH"), "reasoning-effort",
                new JObject
                {
                    ["effort"] = string.IsNullOrWhiteSpace(effort) ? JValue.CreateNull() : effort,
                });

        public void SetExecutionMode(string serverUrl, string token, string sessionId, string mode)
            => MutateSession(serverUrl, token, sessionId, new HttpMethod("PATCH"), "execution-mode",
                new JObject { ["mode"] = mode == "ultra" ? "ultra" : "standard" });

        public void SetThinking(string serverUrl, string token, string sessionId, bool thinking)
            => MutateSession(serverUrl, token, sessionId, new HttpMethod("PATCH"), "thinking",
                new JObject { ["thinking"] = thinking });

        public void SetPlanDepth(string serverUrl, string token, string sessionId, string depth)
            => MutateSession(serverUrl, token, sessionId, new HttpMethod("PATCH"), "plan-depth",
                new JObject { ["depth"] = string.IsNullOrWhiteSpace(depth) ? JValue.CreateNull() : depth });

        public void SetToolOutputCapLevel(string serverUrl, string token, string sessionId, string level)
            => MutateSession(serverUrl, token, sessionId, new HttpMethod("PATCH"), "tool-output-cap-level",
                new JObject { ["level"] = string.IsNullOrWhiteSpace(level) ? JValue.CreateNull() : level });

        public void SetToolPermissionMode(string serverUrl, string token, string sessionId, string mode)
            => MutateSession(serverUrl, token, sessionId, new HttpMethod("PATCH"), "tool-permission-mode",
                new JObject { ["mode"] = string.IsNullOrWhiteSpace(mode) ? JValue.CreateNull() : mode });

        public void SetResponseLanguage(string serverUrl, string token, string sessionId, string language)
            => MutateSession(serverUrl, token, sessionId, new HttpMethod("PATCH"), "response-language",
                new JObject
                {
                    ["language"] = string.IsNullOrWhiteSpace(language) ? JValue.CreateNull() : language,
                });

        public void SetOutputStyle(string serverUrl, string token, string sessionId, string style)
            => MutateSession(serverUrl, token, sessionId, new HttpMethod("PATCH"), "output-style",
                new JObject { ["style"] = string.IsNullOrWhiteSpace(style) ? JValue.CreateNull() : style });

        public void SetRunLimits(string serverUrl, string token, string sessionId, int maxTurns, int maxAutoContinues)
            => MutateSession(serverUrl, token, sessionId, new HttpMethod("PATCH"), "run-limits",
                new JObject
                {
                    ["max_turns"] = maxTurns > 0 ? new JValue(maxTurns) : JValue.CreateNull(),
                    ["max_auto_continues"] = Math.Max(0, Math.Min(1000, maxAutoContinues)),
                });

        /// Quick connectivity probe: GETs /api/projects and reports the HTTP
        /// status to the chat transcript. Useful when "I sent a message and
        /// nothing happened" — tells you immediately if the URL/port/token are
        /// pointing at a reachable CodeStudio instance.
        public void TestConnection(string serverUrl, string token)
        {
            if (!TryBeginForegroundOperation(out var operationCts))
            {
                _session.AddSystemNote("a request is already in flight — cancel it first");
                return;
            }
            var ct = operationCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    var url = $"{serverUrl.TrimEnd('/')}/api/projects";
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    if (!string.IsNullOrEmpty(token))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var resp = await Http.SendAsync(req, ct);
                    var body = await resp.Content.ReadAsStringAsync();
                    var status = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                    int count = 0;
                    try { count = JArray.Parse(body).Count; } catch { /* not an array */ }
                    await MainThreadDispatcher.RunAsync(() => _session.AddSystemNote(
                        $"connection ok: GET {url} → {status} ({count} projects visible)"));
                }
                catch (HttpRequestException e)
                {
                    await MainThreadDispatcher.RunAsync(() => _session.AddError(
                        $"connection failed: {e.Message}\nCheck: is the D:\\MCP runtime running and is the URL/port correct?"));
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    await MainThreadDispatcher.RunAsync(() => _session.AddError(
                        $"test connection error: {e.GetType().Name}: {e.Message}"));
                }
                finally { CompleteForegroundOperation(operationCts); }
            });
        }

        /// Result of an EnsureProjectAsync call — id + the dashboard's name +
        /// the root_path it agreed to. Persisted in Settings so the UI can
        /// show a meaningful status and so workdir drift can be detected.
        public readonly struct EnsureProjectResult
        {
            public readonly int Id;
            public readonly string Name;
            public readonly string RootPath;
            public EnsureProjectResult(int id, string name, string rootPath)
            { Id = id; Name = name ?? ""; RootPath = rootPath ?? ""; }
            public static EnsureProjectResult Empty => new EnsureProjectResult(0, "", "");
        }

        /// Resolves the CodeStudio `project_id` for the given Unity workdir. Lists
        /// `/api/projects`, returns the id of one whose `root_path` matches the
        /// Unity workdir (case-insensitive, separator-insensitive). Creates a
        /// new one via POST if no match.
        public async Task<EnsureProjectResult> EnsureProjectAsync(string serverUrl, string token, string unityRoot, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrEmpty(unityRoot))
                {
                    await MainThreadDispatcher.RunAsync(() =>
                        _session.AddError("Unity project root is empty — cannot auto-detect project"));
                    return EnsureProjectResult.Empty;
                }

                // 1. List
                var listUrl = $"{serverUrl.TrimEnd('/')}/api/projects";
                using (var lreq = new HttpRequestMessage(HttpMethod.Get, listUrl))
                {
                    if (!string.IsNullOrEmpty(token))
                        lreq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var lresp = await Http.SendAsync(lreq, ct);
                    var lbody = await lresp.Content.ReadAsStringAsync();
                    if (!lresp.IsSuccessStatusCode)
                    {
                        await MainThreadDispatcher.RunAsync(() => _session.AddError(
                            $"list projects failed — HTTP {(int)lresp.StatusCode}: {lbody}"));
                        return EnsureProjectResult.Empty;
                    }
                    var arr = JArray.Parse(lbody);
                    var target = NormalizePath(unityRoot);
                    foreach (var p in arr)
                    {
                        var rp = (string)p["root_path"];
                        if (string.IsNullOrEmpty(rp)) continue;
                        if (string.Equals(NormalizePath(rp), target, StringComparison.OrdinalIgnoreCase))
                        {
                            var existingId = (int?)p["id"] ?? 0;
                            if (existingId > 0)
                            {
                                var existingName = (string)p["name"] ?? "";
                                await MainThreadDispatcher.RunAsync(() => _session.AddSystemNote(
                                    $"matched existing project #{existingId} '{existingName}' for {unityRoot}"));
                                return new EnsureProjectResult(existingId, existingName, rp);
                            }
                        }
                    }
                }

                // 2. Create
                var createUrl = $"{serverUrl.TrimEnd('/')}/api/projects";
                var createBody = new JObject
                {
                    ["root_path"] = unityRoot,
                    ["name"] = SafeName(unityRoot),
                    ["description"] = "Auto-created by Unity MCP plugin",
                };
                using (var creq = new HttpRequestMessage(HttpMethod.Post, createUrl)
                {
                    Content = new StringContent(createBody.ToString(FormattingNs.None),
                        Encoding.UTF8, "application/json"),
                })
                {
                    if (!string.IsNullOrEmpty(token))
                        creq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var cresp = await Http.SendAsync(creq, ct);
                    var cbody = await cresp.Content.ReadAsStringAsync();
                    if (!cresp.IsSuccessStatusCode)
                    {
                        await MainThreadDispatcher.RunAsync(() => _session.AddError(
                            $"create project failed — HTTP {(int)cresp.StatusCode}: {cbody}"));
                        return EnsureProjectResult.Empty;
                    }
                    var created = JObject.Parse(cbody);
                    var id = (int?)created["id"] ?? 0;
                    var createdName = (string)created["name"] ?? "";
                    var createdRoot = (string)created["root_path"] ?? unityRoot;
                    if (id > 0)
                    {
                        await MainThreadDispatcher.RunAsync(() => _session.AddSystemNote(
                            $"created project #{id} '{createdName}' for {unityRoot}"));
                    }
                    return new EnsureProjectResult(id, createdName, createdRoot);
                }
            }
            catch (OperationCanceledException) { return EnsureProjectResult.Empty; }
            catch (Exception e)
            {
                Debug.LogException(e);
                await MainThreadDispatcher.RunAsync(() => _session.AddError($"ensure project error: {e.Message}"));
                return EnsureProjectResult.Empty;
            }
        }

        /// Cache-aware project resolver shared by Send / CreateSession. Returns
        /// the cached id when it still matches the current Unity workdir,
        /// otherwise re-runs EnsureProjectAsync and persists the new id +
        /// workdir + name. Returns 0 on failure (the underlying call already
        /// surfaced the error in the chat transcript).
        private async Task<int> ResolveProjectAsync(string serverUrl, string token, string unityRoot, CancellationToken ct)
        {
            int cachedId = 0;
            string cachedRoot = "";
            await MainThreadDispatcher.RunAsync(() =>
            {
                cachedId = Settings.CoderProjectId;
                cachedRoot = Settings.CoderProjectWorkdir;
            });

            if (cachedId > 0 && WorkdirMatches(cachedRoot, unityRoot))
                return cachedId;

            var note = cachedId > 0
                ? $"[1/3] cached project #{cachedId} points at '{cachedRoot}' but Unity is at '{unityRoot}' — re-resolving"
                : "[1/3] no project cached — auto-detecting from Unity workdir";
            await MainThreadDispatcher.RunAsync(() => _session.AddSystemNote(note));

            var result = await EnsureProjectAsync(serverUrl, token, unityRoot, ct);
            if (result.Id <= 0) return 0;

            await MainThreadDispatcher.RunAsync(() =>
            {
                Settings.CoderProjectId = result.Id;
                Settings.CoderProjectWorkdir = unityRoot;
                Settings.CoderProjectName = result.Name;
                _session.AddSystemNote($"[1/3] project id = {result.Id} ('{result.Name}')");
            });
            return result.Id;
        }

        public static bool WorkdirMatches(string a, string b)
        {
            return string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            try { p = Path.GetFullPath(p); } catch { /* leave as-is */ }
            return p.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        }

        private static string SafeName(string root)
        {
            try { return Path.GetFileName(root.TrimEnd('/', '\\')); }
            catch { return root; }
        }

        /// Fire-and-forget: fetches `/api/coder/providers`, fills
        /// Bootstrap.CoderProviders, fires the OnCoderProvidersChanged event so
        /// the window dropdowns refresh.
        public void RefreshProviders(string serverUrl, string token)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var ct = _lifetimeCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    var url = $"{serverUrl.TrimEnd('/')}/api/coder/providers";
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    if (!string.IsNullOrEmpty(token))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var resp = await Http.SendAsync(req, ct);
                    var body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        await MainThreadDispatcher.RunAsync(() => _session.AddError(
                            $"refresh providers failed — HTTP {(int)resp.StatusCode}: {body}"));
                        return;
                    }
                    var root = JObject.Parse(body);
                    var arr = root["providers"] as JArray ?? new JArray();
                    var list = new List<CoderProviderInfo>();
                    foreach (var p in arr)
                    {
                        var info = new CoderProviderInfo
                        {
                            Id = (string)p["id"],
                            Name = (string)p["name"],
                            Kind = (string)p["kind"],
                            IsDefault = (bool?)p["is_default"] ?? false,
                            HasKey = (bool?)p["has_key"] ?? false,
                        };
                        var models = p["models"] as JArray;
                        if (models != null)
                        {
                            foreach (var m in models)
                            {
                                info.Models.Add(new CoderModelInfo
                                {
                                    Id = (string)m["id"],
                                    Name = (string)m["name"],
                                    Mode = (string)m["mode"],
                                    ContextWindow = (int?)m["context_window"],
                                    DefaultMaxTokens = (int?)m["default_max_tokens"],
                                    CanReason = (bool?)m["can_reason"] ?? false,
                                    DefaultReasoningLevel = (string)m["default_reasoning_level"],
                                    SupportedReasoningLevels = (m["supported_reasoning_levels"] as JArray)?
                                        .Values<string>().Where(value => !string.IsNullOrEmpty(value)).ToList()
                                        ?? new List<string>(),
                                });
                            }
                        }
                        list.Add(info);
                    }

                    await MainThreadDispatcher.RunAsync(() =>
                    {
                        Bootstrap.CoderProviders = list;
                        Bootstrap.NotifyCoderProvidersChanged();
                        _session.AddSystemNote(
                            $"providers refreshed: {list.Count} provider(s), " +
                            $"{list.Sum(p => p.Models.Count)} model(s) total");
                    });
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    await MainThreadDispatcher.RunAsync(() => _session.AddError(
                        $"refresh providers error: {e.GetType().Name}: {e.Message}"));
                }
            });
        }

        public async Task<string> CreateSessionAsync(string serverUrl, string token, int projectId,
            string title, string provider, string model, string agent, CancellationToken ct)
            => await CreateSessionAsync(
                serverUrl, token, projectId, title, provider, model, agent, null, ct);

        public async Task<string> CreateSessionAsync(string serverUrl, string token, int projectId,
            string title, string provider, string model, string agent, CoderSessionOptions options, CancellationToken ct)
        {
            try
            {
                if (string.Equals(agent, UnityBuildAgentManifest.SelectionId, StringComparison.Ordinal))
                    await InstallOrUpdateUnityAgentAsync(serverUrl, token, ct).ConfigureAwait(false);
                var url = $"{serverUrl.TrimEnd('/')}/api/coder/sessions";
                var body = BuildCreateSessionBody(projectId, title, provider, model, agent, options);

                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None),
                        Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrEmpty(token))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var resp = await Http.SendAsync(req, ct);
                var bodyText = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    await MainThreadDispatcher.RunAsync(() => _session.AddError(
                        $"create session failed — HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}\n{bodyText}"));
                    return "";
                }

                var json = JObject.Parse(bodyText);
                var id = CoderSessionIdentity.FromToken(json["id"]);
                if (string.IsNullOrEmpty(id))
                {
                    await MainThreadDispatcher.RunAsync(() => _session.AddError(
                        $"create session returned no id: {bodyText}"));
                }
                else if (options != null)
                {
                    try
                    {
                        await ApplyPostCreationSettingsAsync(serverUrl, token, id, options, ct);
                    }
                    catch (Exception settingsException)
                    {
                        await MainThreadDispatcher.RunAsync(() => _session.AddSystemNote(
                            $"Sessão criada, mas uma configuração avançada não foi aplicada: {settingsException.Message}"));
                    }
                }
                return id;
            }
            catch (OperationCanceledException) { return ""; }
            catch (Exception e)
            {
                Debug.LogException(e);
                await MainThreadDispatcher.RunAsync(() => _session.AddError($"create session error: {e.Message}"));
                return "";
            }
        }

        public static JObject BuildCreateSessionBody(int projectId, string title, string provider,
            string model, string agent, CoderSessionOptions options)
        {
            var body = new JObject
            {
                ["project_id"] = projectId,
                ["title"] = string.IsNullOrEmpty(title)
                    ? $"Unity chat — {DateTime.Now:yyyy-MM-dd HH:mm}"
                    : title,
                ["provider"] = string.IsNullOrEmpty(provider) ? "anthropic" : provider,
            };
            if (!string.IsNullOrEmpty(model)) body["model"] = model;
            if (!string.IsNullOrEmpty(agent)) body["agent"] = agent;
            if (options == null) return body;

            body["execution_mode"] = options.ExecutionMode == "ultra" ? "ultra" : "standard";
            body["reasoning_effort"] = string.IsNullOrWhiteSpace(options.ReasoningEffort)
                ? JValue.CreateNull()
                : options.ReasoningEffort.Trim();
            body["thinking_enabled"] = options.ThinkingEnabled;
            body["max_turns"] = options.MaxTurns > 0
                ? new JValue(options.MaxTurns)
                : JValue.CreateNull();
            body["max_auto_continues"] = Math.Max(0, Math.Min(1000, options.MaxAutoContinues));
            return body;
        }

        public static JObject BuildMessageBody(string content, string profile, string agent, bool thinking)
        {
            var body = new JObject { ["content"] = content ?? "" };
            if (!string.IsNullOrWhiteSpace(agent))
                body["agent"] = agent.Trim();
            else
                body["profile"] = NormalizeProfile(profile);
            if (thinking) body["thinking"] = true;
            return body;
        }

        private static string NormalizeProfile(string profile)
        {
            switch ((profile ?? "").Trim().ToLowerInvariant())
            {
                case "bootstrap": return "bootstrap";
                case "navigate": return "navigate";
                case "full": return "full";
                default: return "auto";
            }
        }

        private async Task SendAsync(string serverUrl, string token, string sessionId, string content,
            string profile, string agent, bool thinking, CancellationToken ct)
        {
            try
            {
                var url = $"{serverUrl.TrimEnd('/')}/api/coder/sessions/{sessionId}/messages";
                var body = BuildMessageBody(content, profile, agent, thinking);

                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"),
                };
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                if (!string.IsNullOrEmpty(_session.LastEventId))
                    req.Headers.Add("Last-Event-ID", _session.LastEventId);
                if (!string.IsNullOrEmpty(token))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                await MainThreadDispatcher.RunAsync(() => _session.StartAssistant());

                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var errBody = await resp.Content.ReadAsStringAsync();
                    await MainThreadDispatcher.RunAsync(() => _session.AddError(
                        $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}\n{errBody}"));
                    return;
                }

                using var stream = await resp.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await ParseSseAsync(reader, ct);
            }
            catch (OperationCanceledException)
            {
                await MainThreadDispatcher.RunAsync(() => _session.AddSystemNote("(cancelled)"));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                await MainThreadDispatcher.RunAsync(() => _session.AddError($"client error: {e.Message}"));
            }
            finally
            {
                try
                {
                    var drainTarget = Interlocked.Read(ref _enqueuedStreamEvents);
                    await WaitForStreamDrainAsync(drainTarget, _lifetimeCts.Token);
                }
                catch (OperationCanceledException) { }
                await MainThreadDispatcher.RunAsync(() => _session.FinishAssistant());
            }
        }

        private async Task<bool> ParseSseAsync(StreamReader reader, CancellationToken ct)
        {
            string eventType = null;
            var dataBuilder = new StringBuilder();
            string lastId = null;
            var terminalSeen = false;

            while (!ct.IsCancellationRequested)
            {
                var line = await ReadLineWithCancellationAsync(reader, ct);
                if (line == null) break;

                if (line.Length == 0)
                {
                    if (dataBuilder.Length > 0)
                    {
                        var data = dataBuilder.ToString();
                        var streamEvent = CoderStreamEvent.Create(eventType ?? "message", data, lastId);
                        EnqueueStreamEvent(streamEvent);
                        if (streamEvent.IsTerminal)
                        {
                            terminalSeen = true;
                            dataBuilder.Clear();
                            break;
                        }
                    }
                    eventType = null;
                    dataBuilder.Clear();
                    continue;
                }

                if (line.StartsWith(":")) continue; // SSE comment / heartbeat
                if (line.StartsWith("id:")) lastId = line.Substring(3).TrimStart();
                else if (line.StartsWith("event:")) eventType = line.Substring(6).TrimStart();
                else if (line.StartsWith("data:"))
                {
                    if (dataBuilder.Length > 0) dataBuilder.Append('\n');
                    dataBuilder.Append(line.Substring(5).TrimStart());
                }
            }

            if (!terminalSeen && dataBuilder.Length > 0)
                EnqueueStreamEvent(CoderStreamEvent.Create(eventType ?? "message", dataBuilder.ToString(), lastId));

            var drainTarget = Interlocked.Read(ref _enqueuedStreamEvents);
            await WaitForStreamDrainAsync(drainTarget, _lifetimeCts.Token);
            return terminalSeen;
        }

        private static async Task<string> ReadLineWithCancellationAsync(
            StreamReader reader,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var readTask = reader.ReadLineAsync();
            if (readTask.IsCompleted) return await readTask;

            var cancelled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(readTask, cancelled.Task) != readTask)
                    ct.ThrowIfCancellationRequested();
            }
            return await readTask;
        }

        private void EnqueueStreamEvent(CoderStreamEvent streamEvent)
        {
            if (streamEvent == null || _lifetimeCts.IsCancellationRequested) return;
            _streamEvents.Enqueue(streamEvent);
            Interlocked.Increment(ref _enqueuedStreamEvents);
            ScheduleStreamDrain();
        }

        private void ScheduleStreamDrain()
        {
            if (_lifetimeCts.IsCancellationRequested) return;
            if (Interlocked.CompareExchange(ref _streamDrainScheduled, 1, 0) != 0) return;
            _ = MainThreadDispatcher.RunAsync(DrainStreamEvents, _lifetimeCts.Token).ContinueWith(task =>
            {
                if (task.IsFaulted && task.Exception != null)
                    Debug.LogException(task.Exception.GetBaseException());
            }, TaskScheduler.Default);
        }

        private void DrainStreamEvents()
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var rawEvents = new List<CoderStreamEvent>(MaxEventsPerDrain);
            while (rawEvents.Count < MaxEventsPerDrain
                && started.Elapsed.TotalMilliseconds < MaxDrainMilliseconds
                && _streamEvents.TryDequeue(out var streamEvent))
            {
                rawEvents.Add(streamEvent);
            }

            if (rawEvents.Count > 0)
            {
                var events = CoderStreamEvent.CoalesceAdjacentText(rawEvents);
                _session.ApplyBatch(() =>
                {
                    foreach (var streamEvent in events) DispatchEvent(streamEvent);
                });
                Interlocked.Add(ref _appliedStreamEvents, rawEvents.Count);
            }

            Interlocked.Exchange(ref _streamDrainScheduled, 0);
            if (!_streamEvents.IsEmpty) ScheduleStreamDrain();
        }

        private async Task WaitForStreamDrainAsync(long target, CancellationToken ct)
        {
            while (Interlocked.Read(ref _appliedStreamEvents) < target)
            {
                ct.ThrowIfCancellationRequested();
                ScheduleStreamDrain();
                await Task.Delay(8, ct);
            }
        }

        // ---- Safe JToken accessors ----------------------------------------
        // A JObject/JArray cast directly to string throws ArgumentException
        // ("Can not convert Object to String"). The agent's SSE payloads can
        // legitimately carry objects/arrays where we expected strings (e.g.
        // `question.options` may be `[{label, value}, ...]` instead of plain
        // strings). These helpers tolerate any shape and fall back to a safe
        // textual representation.

        private static string AsString(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.String) return (string)t;
            if (t is JValue v) return v.Value?.ToString() ?? "";
            return t.ToString(FormattingNs.None);
        }

        private static int? AsInt(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Integer) return (int)t;
            if (t.Type == JTokenType.Float) return (int)(double)t;
            if (t.Type == JTokenType.String && int.TryParse((string)t, out var v)) return v;
            return null;
        }

        private static bool? AsBool(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            if (t.Type == JTokenType.String && bool.TryParse((string)t, out var v)) return v;
            return null;
        }

        private static double? AsDouble(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return (double)t;
            if (t.Type == JTokenType.String && double.TryParse((string)t,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
            return null;
        }

        /// Options on a question can be plain strings or objects with
        /// `{label, value}` (or similar). Pick the most human-readable label.
        private static string OptionLabel(JToken token)
        {
            if (token == null) return "";
            if (token.Type == JTokenType.String) return (string)token;
            if (token is JObject option)
            {
                var label = AsString(option["label"]) ?? AsString(option["text"])
                    ?? AsString(option["value"]) ?? AsString(option["id"]);
                if (!string.IsNullOrEmpty(label)) return label;
            }
            return AsString(token) ?? "";
        }

        private static string OptionValue(JToken token)
        {
            if (token == null) return "";
            if (token.Type == JTokenType.String) return (string)token;
            if (token is JObject option)
                return AsString(option["value"]) ?? AsString(option["id"])
                    ?? AsString(option["label"]) ?? AsString(option["text"]) ?? "";
            return AsString(token) ?? "";
        }

        private void DispatchEvent(CoderStreamEvent streamEvent)
        {
            if (streamEvent == null) return;
            if (!string.IsNullOrEmpty(streamEvent.LastEventId)) _session.LastEventId = streamEvent.LastEventId;

            try
            {
                DispatchEventInner(streamEvent.Type, streamEvent.Data, streamEvent.Payload);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                var data = streamEvent.Data;
                _session.AddSystemNote(
                    $"(evento `{streamEvent.Type}` ignorado: {exception.GetType().Name}: {exception.Message}; bruto: " +
                    (data?.Length > 200 ? data.Substring(0, 200) + "…" : data) + ")");
            }
        }

        private void DispatchEventInner(string type, string data, JToken payload)
        {
            switch (type)
            {
                case "text":
                    EndCursorlessCompletionReplay();
                    _session.AppendAssistantText(AsString(payload?["text"]) ?? data ?? "");
                    break;
                case "thinking":
                    EndCursorlessCompletionReplay();
                    _session.AppendAssistantThinking(AsString(payload?["text"]) ?? data ?? "");
                    break;
                case "tool_call_start":
                {
                    var id = AsString(payload?["id"]) ?? Guid.NewGuid().ToString("N");
                    if (_cursorlessReplayToolIds.Contains(id)) break;
                    _session.AddToolCall(
                        id,
                        AsString(payload?["name"]) ?? "?",
                        payload?["input"]?.ToString(FormattingNs.None) ?? "");
                    break;
                }
                case "tool_call_result":
                {
                    var id = AsString(payload?["id"]) ?? "";
                    var resultText = TruncateJson(payload?["result"], 800);
                    var durationMs = AsDouble(payload?["duration_ms"]) ?? 0.0;
                    if (!_cursorlessReplayToolIds.Contains(id)
                        || !_session.CompletePersistedToolCall(id, resultText, durationMs))
                        _session.CompleteToolCall(id, resultText, durationMs);

                    // Surface a focused hint when the Unity MCP server timed
                    // out — most common cause is an assembly reload mid-call.
                    // Helps the agent (and the dev) understand the right
                    // action is to wait ~5s and retry.
                    if (LooksLikeUnityMcpTimeout(resultText))
                    {
                        _session.AddSystemNote(
                            "Unity MCP server didn't respond in time. " +
                            "Most likely an assembly reload happened (you saved a .cs file) " +
                            "OR refresh_assets held the main thread on an older plugin build. " +
                            "The plugin auto-restarts after reload — wait ~5s and retry.");
                    }
                    break;
                }
                case "plan":
                {
                    var p = new PendingPlan
                    {
                        Id = AsString(payload?["id"]),
                        Plan = AsString(payload?["plan"]) ?? data,
                    };
                    _session.SetPendingPlan(p);
                    break;
                }
                case "spec":
                {
                    var spec = new PendingSpec
                    {
                        Id = AsString(payload?["id"]),
                        Name = AsString(payload?["name"]) ?? "Especificação",
                        Body = AsString(payload?["body"]) ?? data,
                        SpecPath = AsString(payload?["spec_path"]),
                    };
                    _session.SetPendingSpec(spec);
                    break;
                }
                case "question":
                {
                    var q = new PendingQuestion
                    {
                        Id = AsString(payload?["id"]),
                        Question = AsString(payload?["question"]) ?? data,
                        AllowFreeform = AsBool(payload?["allowFreeform"] ?? payload?["allow_freeform"]) ?? true,
                        MultiSelect = AsBool(payload?["multiSelect"] ?? payload?["multi_select"]) ?? false,
                    };
                    if (payload?["options"] is JArray optsArr)
                    {
                        foreach (var optionToken in optsArr)
                        {
                            var label = OptionLabel(optionToken);
                            var value = OptionValue(optionToken);
                            if (string.IsNullOrEmpty(label) && string.IsNullOrEmpty(value)) continue;
                            if (string.IsNullOrEmpty(label)) label = value;
                            if (string.IsNullOrEmpty(value)) value = label;
                            q.Options.Add(label);
                            q.Choices.Add(new PendingOption { Label = label, Value = value });
                        }
                    }
                    _session.SetPendingQuestion(q);
                    break;
                }
                case "decision":
                {
                    var decision = new PendingDecision
                    {
                        Id = AsString(payload?["id"]),
                        Header = AsString(payload?["header"]),
                        AllowFreeform = AsBool(payload?["allowFreeform"] ?? payload?["allow_freeform"]) ?? false,
                    };
                    foreach (var questionToken in payload?["questions"] as JArray ?? new JArray())
                    {
                        var question = new PendingDecisionQuestion
                        {
                            Id = AsString(questionToken?["id"]) ?? Guid.NewGuid().ToString("N"),
                            Header = AsString(questionToken?["header"]),
                            Question = AsString(questionToken?["question"]) ?? "Escolha uma opção.",
                            MultiSelect = AsBool(questionToken?["multiSelect"] ?? questionToken?["multi_select"]) ?? false,
                        };
                        foreach (var optionToken in questionToken?["options"] as JArray ?? new JArray())
                        {
                            var label = OptionLabel(optionToken);
                            var value = OptionValue(optionToken);
                            if (string.IsNullOrEmpty(label) && string.IsNullOrEmpty(value)) continue;
                            question.Choices.Add(new PendingOption
                            {
                                Label = string.IsNullOrEmpty(label) ? value : label,
                                Value = string.IsNullOrEmpty(value) ? label : value,
                            });
                        }
                        decision.Questions.Add(question);
                    }
                    _session.SetPendingDecision(decision);
                    break;
                }
                case "tasks_posted":
                {
                    var t = new PendingTasks
                    {
                        ToolUseId = AsString(payload?["tool_use_id"]),
                        Summary = AsString(payload?["summary"]) ?? data,
                    };
                    _session.SetPendingTasks(t);
                    break;
                }
                case "tool_approval_request":
                {
                    var approval = new PendingToolApproval
                    {
                        ToolUseId = AsString(payload?["toolUseId"] ?? payload?["tool_use_id"]),
                        ParentToolUseId = AsString(payload?["parentToolUseId"] ?? payload?["parent_tool_use_id"]),
                        ToolName = AsString(payload?["toolName"] ?? payload?["tool_name"]) ?? "ferramenta",
                        ArgsSummary = AsString(payload?["argsSummary"] ?? payload?["args_summary"]) ?? "",
                        InputJson = payload?["input"]?.ToString(FormattingNs.Indented) ?? "",
                        RiskClass = AsString(payload?["riskClass"] ?? payload?["risk_class"]) ?? "write",
                        ApprovalKind = AsString(payload?["approvalKind"] ?? payload?["approval_kind"]) ?? "standard",
                        Nested = AsBool(payload?["nested"]) ?? false,
                    };
                    _session.SetPendingToolApproval(approval);
                    break;
                }
                case "tool_approval_resolved":
                    _session.ClearPendingToolApproval(
                        AsString(payload?["toolUseId"] ?? payload?["tool_use_id"]));
                    break;
                case "provider_swapped":
                    _session.AddSystemNote(
                        $"Modelo ativo: {AsString(payload?["provider"]) ?? "provider"} · "
                        + $"{AsString(payload?["model"]) ?? "modelo"}");
                    break;
                case "session":
                    var sid = CoderSessionIdentity.FromToken(payload?["id"]);
                    if (!string.IsNullOrEmpty(sid)) Settings.CoderSessionId = sid;
                    break;
                case "error":
                    ClearCursorlessReplayBaseline();
                    Interlocked.Exchange(ref _abortRequested, 0);
                    _session.AddError(AsString(payload?["message"]) ?? data);
                    break;
                case "message_complete":
                    if (!ConsumeCursorlessReplayCompletion(payload?["message"]))
                        ApplyCompletedMessage(payload?["message"]);
                    break;
                case "done":
                    ClearCursorlessReplayBaseline();
                    if (Interlocked.Exchange(ref _abortRequested, 0) != 0)
                        _session.AbortAssistant();
                    else
                        _session.FinishAssistant();
                    break;
                case "ping":
                case "context_status":
                case "index_status":
                case "todos_updated":
                    // Silent — could add subtle indicators later if needed.
                    break;
                default:
                    // Unknown event types: store as system note so the dev can debug.
                    _session.AddSystemNote($"{type}: {data}");
                    break;
            }
        }

        private void PrepareCursorlessReplay()
        {
            ClearCursorlessReplayBaseline();
            var lastRemoteUser = -1;
            for (var index = _session.Messages.Count - 1; index >= 0; index--)
            {
                var message = _session.Messages[index];
                if (message.IsRemote && message.Role == ChatRole.User)
                {
                    lastRemoteUser = index;
                    break;
                }
            }

            for (var index = lastRemoteUser + 1; index < _session.Messages.Count; index++)
            {
                var message = _session.Messages[index];
                if (!message.IsRemote || message.Role != ChatRole.Assistant) continue;
                var signature = ReplaySignature(message);
                if (!string.IsNullOrEmpty(signature))
                {
                    _cursorlessReplayAssistantSignatures.TryGetValue(signature, out var count);
                    _cursorlessReplayAssistantSignatures[signature] = count + 1;
                }
                foreach (var call in message.ToolCalls)
                    if (!string.IsNullOrEmpty(call?.Id)) _cursorlessReplayToolIds.Add(call.Id);
            }

            _session.PrepareForLiveReplay();
        }

        private bool ConsumeCursorlessReplayCompletion(JToken message)
        {
            if (_cursorlessReplayAssistantSignatures.Count == 0) return false;
            var signature = ReplaySignature(message);
            if (string.IsNullOrEmpty(signature)
                || !_cursorlessReplayAssistantSignatures.TryGetValue(signature, out var count))
                return false;
            if (count <= 1) _cursorlessReplayAssistantSignatures.Remove(signature);
            else _cursorlessReplayAssistantSignatures[signature] = count - 1;
            return true;
        }

        private void EndCursorlessCompletionReplay()
            => _cursorlessReplayAssistantSignatures.Clear();

        private void ClearCursorlessReplayBaseline()
        {
            _cursorlessReplayAssistantSignatures.Clear();
            _cursorlessReplayToolIds.Clear();
        }

        private static string ReplaySignature(ChatMessage message)
        {
            if (message == null) return "";
            var tools = new JArray();
            foreach (var call in message.ToolCalls)
            {
                tools.Add(new JObject
                {
                    ["id"] = call?.Id ?? "",
                    ["name"] = call?.Name ?? "",
                    ["input"] = NormalizeReplayInput(call?.InputJson),
                });
            }
            return new JObject
            {
                ["text"] = message.Text ?? "",
                ["thinking"] = message.ThinkingText ?? "",
                ["tools"] = tools,
            }.ToString(FormattingNs.None);
        }

        private static string ReplaySignature(JToken message)
        {
            var text = new StringBuilder();
            var thinking = new StringBuilder();
            var tools = new JArray();
            foreach (var block in message?["content"] as JArray ?? new JArray())
            {
                switch (AsString(block?["type"]))
                {
                    case "text":
                        text.Append(AsString(block?["text"]) ?? "");
                        break;
                    case "thinking":
                        thinking.Append(AsString(block?["text"]) ?? "");
                        break;
                    case "tool_use":
                        tools.Add(new JObject
                        {
                            ["id"] = AsString(block?["id"]) ?? "",
                            ["name"] = AsString(block?["name"]) ?? "",
                            ["input"] = block?["input"]?.ToString(FormattingNs.None) ?? "",
                        });
                        break;
                }
            }
            if (text.Length == 0 && thinking.Length == 0 && tools.Count == 0) return "";
            return new JObject
            {
                ["text"] = text.ToString(),
                ["thinking"] = thinking.ToString(),
                ["tools"] = tools,
            }.ToString(FormattingNs.None);
        }

        private static string NormalizeReplayInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            try { return JToken.Parse(input).ToString(FormattingNs.None); }
            catch { return input; }
        }

        private void ApplyCompletedMessage(JToken message)
        {
            var content = message?["content"] as JArray;
            if (content == null) return;
            foreach (var block in content)
            {
                var type = AsString(block?["type"]);
                if (type == "text")
                {
                    var text = AsString(block?["text"]);
                    if (!string.IsNullOrWhiteSpace(text)
                        && !string.Equals(_session.StreamingAssistant?.Text, text, StringComparison.Ordinal)
                        && !(_session.StreamingAssistant?.Text?.EndsWith(text, StringComparison.Ordinal) ?? false))
                        _session.AppendAssistantText(
                            string.IsNullOrWhiteSpace(_session.StreamingAssistant?.Text) ? text : "\n\n" + text);
                }
                else if (type == "thinking")
                {
                    var thinking = AsString(block?["text"]);
                    if (!string.IsNullOrWhiteSpace(thinking)
                        && !string.Equals(_session.StreamingAssistant?.ThinkingText, thinking, StringComparison.Ordinal)
                        && !(_session.StreamingAssistant?.ThinkingText?.EndsWith(thinking, StringComparison.Ordinal) ?? false))
                        _session.AppendAssistantThinking(
                            string.IsNullOrWhiteSpace(_session.StreamingAssistant?.ThinkingText)
                                ? thinking
                                : "\n\n" + thinking);
                }
                else if (type == "tool_use")
                {
                    _session.AddToolCall(
                        AsString(block?["id"]) ?? "",
                        AsString(block?["name"]) ?? "tool",
                        block?["input"]?.ToString(FormattingNs.None) ?? "{}");
                }
            }
        }

        private static string TruncateJson(JToken token, int max)
        {
            if (token == null) return "";
            var s = token.ToString(Newtonsoft.Json.Formatting.None);
            return s.Length > max ? s.Substring(0, max) + " …" : s;
        }

        private static bool LooksLikeUnityMcpTimeout(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            // Match symptoms of either MCP SDK timeout (-32001) or our own
            // server announcing it shut down for an assembly reload.
            return text.IndexOf("-32001", StringComparison.Ordinal) >= 0
                || text.IndexOf("Request timed out", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("assembly reload", StringComparison.OrdinalIgnoreCase) >= 0
                || (text.IndexOf("ext_unity", StringComparison.OrdinalIgnoreCase) >= 0
                    && text.IndexOf("disconnected", StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
