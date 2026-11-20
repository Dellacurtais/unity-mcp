using Maicon.SceneAnalyzer.Editor.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    /// Applies a list of write operations as a single Undo group. Handlers are
    /// dispatched through a registry so secondary asmdefs (e.g. PostFx, gated by
    /// SRP Core) can plug in extra op kinds without main needing to know about
    /// them.
    ///
    /// Op shape: { kind: <string>, label?: <string>, args: <object> }. When an
    /// op has a `label`, the resulting GameObject's instanceId is stored and can
    /// be referenced by later ops via `{ ref: <label> }` anywhere in their args.
    public static class ChangesetExecutor
    {
        public sealed class Report
        {
            public bool Failed;
            public string Error;
            public int FailedAtIndex = -1;
            public int OpsApplied;
            public List<OpResult> Results = new();
            public string Outcome = "notApplied";
            public bool? RollbackComplete;
            public List<string> RollbackErrors = new();
        }

        [ThreadStatic] private static DeferredAssetSaves _assetSaves;

        // Standalone tools persist immediately; a changeset owns the commit boundary.
        internal static bool SaveAssetIfDirty(UnityEngine.Object asset)
        {
            if (_assetSaves != null) { _assetSaves.Track(asset); return false; }
            AssetDatabase.SaveAssetIfDirty(asset);
            return true;
        }

        private sealed class DeferredAssetSaves : IDisposable
        {
            private readonly DeferredAssetSaves _previous = _assetSaves;
            private readonly Dictionary<int, UnityEngine.Object> _assets = new();
            private readonly Dictionary<string, byte[]> _before = new(StringComparer.Ordinal);
            private bool _commitStarted;

            public DeferredAssetSaves() { _assetSaves = this; }
            public void Track(UnityEngine.Object asset)
            {
                var path = AssetDatabase.GetAssetPath(asset);
                if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Deferred save requires an asset path.");
                if (!_before.ContainsKey(path)) _before[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
                _assets[asset.GetMcpInstanceId()] = asset;
            }
            public void Commit()
            {
                _commitStarted = true;
                foreach (var asset in _assets.Values)
                {
                    if (asset == null) throw new InvalidOperationException("An asset scheduled for saving was destroyed.");
                    AssetDatabase.SaveAssetIfDirty(asset);
                    if (EditorUtility.IsDirty(asset))
                        throw new InvalidOperationException($"Asset remained dirty after save: {AssetDatabase.GetAssetPath(asset)}");
                }
            }
            public void VerifyOrRestore(Report report)
            {
                foreach (var entry in _before)
                {
                    if (report.Results.Any(result => string.Equals(result?.CreatedAssetPath, entry.Key, StringComparison.Ordinal))) continue;
                    if (entry.Value == null) continue; // Created assets are removed by RevertAndCleanup.
                    try
                    {
                        var unchanged = File.Exists(entry.Key) && File.ReadAllBytes(entry.Key).SequenceEqual(entry.Value);
                        if (unchanged) continue;
                        if (_commitStarted) File.WriteAllBytes(entry.Key, entry.Value);
                        if (!File.Exists(entry.Key) || !File.ReadAllBytes(entry.Key).SequenceEqual(entry.Value))
                            report.RollbackErrors.Add($"Asset contents were not restored: {entry.Key}");
                    }
                    catch (Exception e) { report.RollbackErrors.Add($"Asset restore failed for {entry.Key}: {e.Message}"); }
                }
            }
            public void Dispose() { _assetSaves = _previous; }
        }

        private static readonly Dictionary<string, Func<JObject, OpResult>> Handlers
            = new(StringComparer.OrdinalIgnoreCase)
        {
            ["create_primitive"]       = PrimitiveFactory.Create,
            ["create_empty"]           = GameObjectFactory.CreateEmpty,
            ["instantiate_prefab"]     = PrefabFactory.Instantiate,
            ["create_light"]           = LightFactory.Create,
            ["create_camera"]          = CameraFactory.Create,
            ["create_audio_source"]    = AudioSourceFactory.Create,
            ["create_particle_system"] = ParticleTemplates.Create,
            ["set_particle_module"]    = ParticleEditor.SetModule,
            ["play_particle_preview"]  = ParticleEditor.PlayPreview,
            ["set_transform"]          = TransformEditor.SetTransform,
            ["set_camera_props"]       = CameraFactory.SetProps,
            ["look_at"]                = GameObjectEditor.LookAt,
            ["duplicate_object"]       = GameObjectEditor.Duplicate,
            ["parent_object"]          = TransformEditor.Parent,
            ["add_component"]          = ComponentEditor.AddComponent,
            ["remove_component"]       = ComponentEditor.RemoveComponent,
            ["set_enabled"]            = ComponentEditor.SetEnabled,
            ["set_property"]           = ComponentEditor.SetProperty,
            ["set_material"]           = RendererMaterialEditor.SetMaterial,
            ["set_active"]             = GameObjectEditor.SetActive,
            ["rename_object"]          = GameObjectEditor.Rename,
            ["set_tag"]                = GameObjectEditor.SetTag,
            ["set_layer"]              = GameObjectEditor.SetLayer,
            ["delete_object"]          = GameObjectEditor.Delete,
            ["set_render_settings"]    = RenderSettingsEditor.Apply,
            // ScriptableObject + array helper
            ["create_scriptable_object"]    = ScriptableObjectAuthor.Create,
            ["set_so_property"]             = ScriptableObjectAuthor.SetProperty,
            ["duplicate_scriptable_object"] = ScriptableObjectAuthor.Duplicate,
            ["modify_array_property"]       = ScriptableObjectAuthor.ModifyArrayProperty,
            // NavMesh Tier 1
            ["bake_navmesh"]                = NavMeshAuthor.BakeLegacy,
            ["set_navmesh_agent"]           = NavMeshAuthor.SetAgent,
            ["add_offmesh_link"]            = NavMeshAuthor.AddOffMeshLink,
            ["add_navmesh_obstacle"]        = NavMeshAuthor.AddObstacle,
            // Animation
            ["set_animator_parameter"]   = AnimationAuthor.SetAnimatorParameter,
            ["play_animator_state"]      = AnimationAuthor.PlayAnimatorState,
            ["create_animator_controller"] = AnimationAuthor.CreateController,
            ["add_animator_state"]       = AnimationAuthor.AddState,
            ["add_animator_transition"]  = AnimationAuthor.AddTransition,
            ["add_animator_parameter"]   = AnimationAuthor.AddParameter,
            ["assign_controller_to_animator"] = AnimationAuthor.AssignControllerToAnimator,
            ["add_animator_layer"]       = AnimationAuthor.AddLayer,
            ["create_blend_tree"]        = AnimationAuthor.CreateBlendTree,
            ["add_blend_tree_motion"]    = AnimationAuthor.AddBlendTreeMotion,
            ["add_animation_event"]      = AnimationAuthor.AddAnimationEvent,
            // Collider authoring
            ["auto_fit_collider"]        = PhysicsAuthor.AutoFitCollider,
            ["add_compound_colliders"]   = PhysicsAuthor.AddCompoundColliders,
            ["set_physics_material"]     = PhysicsAuthor.SetPhysicsMaterial,
            // Audio
            ["create_audio_source_3d"]   = AudioAuthor.Create3DSource,
            ["set_audio_source_props"]   = AudioAuthor.SetSourceProps,
            ["play_audio_source"]        = AudioAuthor.PlayAudio,
            ["assign_to_mixer_group"]    = AudioAuthor.AssignToMixerGroup,
            ["set_mixer_parameter"]      = AudioAuthor.SetMixerParameter,
            ["play_mixer_snapshot"]      = AudioAuthor.PlayMixerSnapshot,
            ["setup_background_music"]   = AudioAuthor.SetupBackgroundMusic,
            ["create_reverb_zone"]       = AudioAuthor.CreateReverbZone,
            ["add_audio_listener"]       = AudioAuthor.AddListener,
        };

        /// External assemblies (e.g. PostFx) call this from [InitializeOnLoad]
        /// to add their op kinds to the dispatcher.
        public static void RegisterHandler(string kind, Func<JObject, OpResult> handler)
        {
            if (string.IsNullOrEmpty(kind) || handler == null) return;
            Handlers[kind] = handler;
        }

        public static IReadOnlyCollection<string> KnownKinds => Handlers.Keys;

        /// Fires after a successful (non-preview, non-failed) Apply. Lets
        /// Bootstrap invalidate the snapshot/runner cache without coupling
        /// this Authoring asmdef back to Editor namespace.
        public static event Action OnApplied;

        public static Report Apply(string name, JArray ops, bool preview)
        {
            var report = new Report();
            var labels = new Dictionary<string, int>(StringComparer.Ordinal);
            var preflight = Preflight(ops);
            if (preview || preflight.Failed) return preflight;

            using var assetSaves = new DeferredAssetSaves();

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(string.IsNullOrEmpty(name) ? "Apply Changeset" : name);
            int group = Undo.GetCurrentGroup();

            for (int i = 0; i < ops.Count; i++)
            {
                var op = ops[i] as JObject;
                if (op == null)
                {
                    report.Failed = true;
                    report.Error = $"op #{i} is not an object";
                    report.FailedAtIndex = i;
                    RevertAndCleanup(report, group);
                    return report;
                }

                ResolveRefs(op, labels);

                OpResult r;
                try { r = ApplyOne(op); }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    var inner = e.InnerException != null
                        ? $" -> {e.InnerException.GetType().Name}: {e.InnerException.Message}"
                        : "";
                    r = new OpResult
                    {
                        Ok = false,
                        Error = $"{e.GetType().Name}: {e.Message}{inner}",
                        ExceptionType = e.GetType().FullName,
                        StackTrace = e.StackTrace,
                    };
                }

                report.Results.Add(r);
                if (!r.Ok)
                {
                    report.Failed = true;
                    report.Error = $"op #{i} failed: {r.Error}";
                    report.FailedAtIndex = i;
                    RevertAndCleanup(report, group);
                    return report;
                }

                var label = (string)op["label"];
                if (!string.IsNullOrEmpty(label) && r.InstanceID != 0)
                {
                    if (labels.ContainsKey(label))
                    {
                        report.Failed = true;
                        report.Error = $"op #{i}: duplicate label '{label}' (already bound by an earlier op)";
                        report.FailedAtIndex = i;
                        // Replace the success entry with a failure but preserve
                        // CreatedAssetPath so RevertAndCleanup can still delete
                        // any asset this op just made.
                        var failure = OpResult.Fail($"DUPLICATE_LABEL: '{label}' already bound by an earlier op");
                        failure.CreatedAssetPath = r.CreatedAssetPath;
                        failure.InstanceID = r.InstanceID;
                        report.Results[report.Results.Count - 1] = failure;
                        RevertAndCleanup(report, group);
                        return report;
                    }
                    labels[label] = r.InstanceID;
                }

                report.OpsApplied++;
            }

            try { assetSaves.Commit(); }
            catch (Exception e)
            {
                report.Failed = true;
                report.Error = $"asset commit failed: {e.Message}";
                RevertAndCleanup(report, group);
                return report;
            }
            Undo.CollapseUndoOperations(group);
            report.Outcome = "applied";
            try { OnApplied?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
            return report;
        }

        /// Reverts the Undo group AND deletes any assets created earlier in
        /// the batch. Asset-creation handlers (CreateController, CreateTimeline,
        /// VolumeFactory.Create) write a path to OpResult.CreatedAssetPath;
        /// this loop deletes those paths so the failed batch leaves no
        /// orphan files behind.
        private static void RevertAndCleanup(Report report, int group)
        {
            try { Undo.FlushUndoRecordObjects(); Undo.RevertAllDownToGroup(group); }
            catch (Exception e) { report.RollbackErrors.Add($"Undo rollback failed: {e.Message}"); }
            foreach (var prior in report.Results)
            {
                if (string.IsNullOrEmpty(prior?.CreatedAssetPath)) continue;
                try
                {
                    if (AssetDatabase.AssetPathExists(prior.CreatedAssetPath)
                        && (!AssetDatabase.DeleteAsset(prior.CreatedAssetPath) || AssetDatabase.AssetPathExists(prior.CreatedAssetPath)))
                        report.RollbackErrors.Add($"Created asset was not removed: {prior.CreatedAssetPath}");
                }
                catch (Exception e) { report.RollbackErrors.Add($"Asset cleanup failed for {prior.CreatedAssetPath}: {e.Message}"); }
            }
            _assetSaves?.VerifyOrRestore(report);
            report.RollbackComplete = report.RollbackErrors.Count == 0;
            report.Outcome = report.RollbackComplete.Value ? "rolledBack" : "rollbackIncomplete";
        }

        /// Walks args looking for unresolved `{ ref: "x" }` objects and
        /// `instanceId`-shaped int properties. Returns the first error, or
        /// null when clean. Used by preview to catch broken refs / dangling
        /// instanceIds before Apply mutates anything.
        private static string ValidateRefsAndIds(JToken token, Dictionary<string, int> labels)
        {
            if (token is JObject obj)
            {
                if (obj.Count == 1 && obj["ref"] != null)
                {
                    var refName = (string)obj["ref"];
                    if (string.IsNullOrEmpty(refName) || !labels.ContainsKey(refName))
                        return $"REF_BROKEN: label '{refName}' not defined by any earlier op";
                    return null;
                }

                foreach (var prop in obj.Properties())
                {
                    if (IsIdProperty(prop.Name) && prop.Value is JValue jv && jv.Type == JTokenType.Integer)
                    {
                        var id = (int)jv;
                        if (id > 0 && UnityObjectIds.Resolve(id) == null)
                            return $"REF_BROKEN: {prop.Name}={id} does not resolve to a live object";
                    }
                    var nested = ValidateRefsAndIds(prop.Value, labels);
                    if (nested != null) return nested;
                }
            }
            else if (token is JArray arr)
            {
                foreach (var item in arr)
                {
                    var nested = ValidateRefsAndIds(item, labels);
                    if (nested != null) return nested;
                }
            }
            return null;
        }

        private static bool IsIdProperty(string name)
        {
            return name == "instanceId" || name == "parentId"
                || name == "newParentId" || name == "volumeInstanceId";
        }

        public static OpResult ApplyOne(JObject op)
        {
            var kind = (string)op["kind"];
            if (string.IsNullOrEmpty(kind)) return OpResult.Fail("op missing 'kind'");
            var args = OperationArgs(op);
            if (Handlers.TryGetValue(kind, out var fn)) return fn(args);
            return OpResult.Fail($"unknown op kind '{kind}'. Known kinds: {string.Join(", ", Handlers.Keys)}");
        }

        /// Recursively replaces { "ref": "labelName" } objects inside the op's
        /// args with the labeled instanceId. Lets a batch chain ops that depend
        /// on the result of an earlier op (typically `parent`).
        private static Report Preflight(JArray ops)
        {
            var report = new Report();
            var labels = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < ops.Count; index++)
            {
                if (!(ops[index] is JObject op))
                {
                    AddPreflightFailure(report, index, $"INVALID_ARG: op #{index} must be an object");
                    continue;
                }

                var clone = (JObject)op.DeepClone();
                var kind = ((string)clone["kind"] ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(kind))
                {
                    AddPreflightFailure(report, index, "INVALID_ARG: missing kind");
                    continue;
                }
                if (!Handlers.ContainsKey(kind))
                {
                    AddPreflightFailure(report, index, $"INVALID_ARG: unknown kind '{kind}'");
                    continue;
                }

                var labelToken = clone["label"];
                var label = labelToken?.Type == JTokenType.String ? ((string)labelToken)?.Trim() : null;
                if (labelToken != null && string.IsNullOrEmpty(label))
                {
                    AddPreflightFailure(report, index, "INVALID_ARG: label must be a non-empty string");
                    continue;
                }
                if (label != null && labels.ContainsKey(label))
                {
                    AddPreflightFailure(report, index, $"INVALID_ARG: duplicate label '{label}'");
                    continue;
                }

                ResolveRefs(clone, labels);
                var arguments = OperationArgs(clone);
                var referenceError = ValidateRefsAndIds(arguments, labels);
                if (referenceError != null)
                {
                    AddPreflightFailure(report, index, referenceError);
                    continue;
                }

                if (Bootstrap.ToolRegistry != null
                    && Bootstrap.ToolRegistry.TryGet(kind, out var tool)
                    && !ToolSchemaValidator.ValidateArguments(arguments, tool.InputSchema, out var schemaError))
                {
                    AddPreflightFailure(report, index, $"INVALID_ARG: {schemaError}");
                    continue;
                }

                report.Results.Add(OpResult.Success(0, $"would apply: {kind}"));
                report.OpsApplied++;
                if (label != null) labels[label] = -1;
            }
            return report;
        }

        private static void AddPreflightFailure(Report report, int index, string error)
        {
            report.Failed = true;
            if (report.FailedAtIndex < 0)
            {
                report.FailedAtIndex = index;
                report.Error = $"op #{index} failed preflight: {error}";
            }
            report.Results.Add(OpResult.Fail(error));
        }

        private static JObject OperationArgs(JObject op)
        {
            if (op["args"] is JObject nested) return nested;
            var inline = (JObject)op.DeepClone();
            inline.Remove("kind");
            inline.Remove("label");
            return inline;
        }

        private static void ResolveRefs(JToken token, Dictionary<string, int> labels)
        {
            if (token is JObject obj)
            {
                foreach (var prop in new List<JProperty>(obj.Properties()))
                {
                    if (prop.Value is JObject inner && inner.Count == 1 && inner["ref"] != null)
                    {
                        var refName = (string)inner["ref"];
                        if (!string.IsNullOrEmpty(refName) && labels.TryGetValue(refName, out var id))
                            prop.Value = id;
                    }
                    else
                    {
                        ResolveRefs(prop.Value, labels);
                    }
                }
            }
            else if (token is JArray arr)
            {
                foreach (var item in arr) ResolveRefs(item, labels);
            }
        }

    }
}
