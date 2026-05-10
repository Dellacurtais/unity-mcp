using System;
using System.Collections.Generic;
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
        }

        private static readonly Dictionary<string, Func<JObject, OpResult>> Handlers
            = new(StringComparer.OrdinalIgnoreCase)
        {
            ["create_primitive"]       = PrimitiveFactory.Create,
            ["create_empty"]           = CreateEmpty,
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
            ["delete_object"]          = DeleteObject,
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

            if (preview)
            {
                for (int i = 0; i < ops.Count; i++)
                {
                    if (!(ops[i] is JObject op))
                    {
                        report.Failed = true;
                        report.FailedAtIndex = i;
                        report.Results.Add(OpResult.Fail($"op #{i}: not an object"));
                        continue;
                    }

                    var clone = (JObject)op.DeepClone();
                    ResolveRefs(clone, labels);

                    var kind = (string)clone["kind"];
                    if (string.IsNullOrEmpty(kind))
                    {
                        report.Failed = true;
                        report.FailedAtIndex = i;
                        report.Results.Add(OpResult.Fail("INVALID_ARG: missing kind"));
                        continue;
                    }
                    if (!Handlers.ContainsKey(kind))
                    {
                        report.Failed = true;
                        report.FailedAtIndex = i;
                        report.Results.Add(OpResult.Fail($"REF_BROKEN: unknown kind '{kind}'"));
                        continue;
                    }

                    var args = clone["args"] as JObject ?? clone;
                    var refErr = ValidateRefsAndIds(args, labels);
                    if (refErr != null)
                    {
                        report.Failed = true;
                        report.FailedAtIndex = i;
                        report.Results.Add(OpResult.Fail(refErr));
                        continue;
                    }

                    report.Results.Add(OpResult.Success(0, $"would apply: {kind}"));
                    report.OpsApplied++;

                    var label = (string)clone["label"];
                    if (!string.IsNullOrEmpty(label) && !labels.ContainsKey(label))
                        labels[label] = -1; // sentinel: later refs validate against it
                }
                return report;
            }

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
                    RevertAndCleanup(report);
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
                    RevertAndCleanup(report);
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
                        RevertAndCleanup(report);
                        return report;
                    }
                    labels[label] = r.InstanceID;
                }

                report.OpsApplied++;
            }

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            try { AssetDatabase.SaveAssets(); }
            catch (Exception e) { Debug.LogException(e); }
            try { OnApplied?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
            return report;
        }

        private static void SafeRevertAll()
        {
            try { Undo.RevertAllInCurrentGroup(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        /// Reverts the Undo group AND deletes any assets created earlier in
        /// the batch. Asset-creation handlers (CreateController, CreateTimeline,
        /// VolumeFactory.Create) write a path to OpResult.CreatedAssetPath;
        /// this loop deletes those paths so the failed batch leaves no
        /// orphan files behind.
        private static void RevertAndCleanup(Report report)
        {
            SafeRevertAll();
            foreach (var prior in report.Results)
            {
                if (string.IsNullOrEmpty(prior?.CreatedAssetPath)) continue;
                try { AssetDatabase.DeleteAsset(prior.CreatedAssetPath); }
                catch (Exception e) { Debug.LogException(e); }
            }
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
                        if (id > 0 && EditorUtility.InstanceIDToObject(id) == null)
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
            var args = op["args"] as JObject ?? op;
            if (Handlers.TryGetValue(kind, out var fn)) return fn(args);
            return OpResult.Fail($"unknown op kind '{kind}'. Known kinds: {string.Join(", ", Handlers.Keys)}");
        }

        /// Recursively replaces { "ref": "labelName" } objects inside the op's
        /// args with the labeled instanceId. Lets a batch chain ops that depend
        /// on the result of an earlier op (typically `parent`).
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

        private static OpResult CreateEmpty(JObject args)
        {
            var name = (string)args["name"] ?? "GameObject";
            var go = new UnityEngine.GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create Empty");
            var parent = JsonHelpers.ResolveParent(args["parent"]);
            if (parent != null) Undo.SetTransformParent(go.transform, parent.transform, "Parent");
            go.transform.localPosition = JsonHelpers.ParseVector3(args["position"]);
            go.transform.localRotation = JsonHelpers.ParseRotation(args["rotation"]);
            return OpResult.Success(go.GetInstanceID(), $"created empty '{name}'");
        }

        private static OpResult DeleteObject(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as UnityEngine.GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");

            int childCount = go.transform.childCount;
            bool force = (bool?)args["force"] ?? false;
            if (childCount > 8 && !force)
                return OpResult.Fail($"refusing to delete '{go.name}' with {childCount} children without force=true");

            Undo.DestroyObjectImmediate(go);
            return OpResult.Success(id, "deleted");
        }
    }
}
