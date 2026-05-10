using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
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
        };

        /// External assemblies (e.g. PostFx) call this from [InitializeOnLoad]
        /// to add their op kinds to the dispatcher.
        public static void RegisterHandler(string kind, Func<JObject, OpResult> handler)
        {
            if (string.IsNullOrEmpty(kind) || handler == null) return;
            Handlers[kind] = handler;
        }

        public static IReadOnlyCollection<string> KnownKinds => Handlers.Keys;

        public static Report Apply(string name, JArray ops, bool preview)
        {
            var report = new Report();
            var labels = new Dictionary<string, int>(StringComparer.Ordinal);

            if (preview)
            {
                foreach (var token in ops)
                {
                    if (token is JObject op)
                        report.Results.Add(OpResult.Success(0,
                            $"would apply: {op["kind"]}" + (op["label"] != null ? $" (label={op["label"]})" : "")));
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
                    Undo.RevertAllInCurrentGroup();
                    return report;
                }

                ResolveRefs(op, labels);

                OpResult r;
                try { r = ApplyOne(op); }
                catch (Exception e) { r = OpResult.Fail($"exception: {e.Message}"); }

                report.Results.Add(r);
                if (!r.Ok)
                {
                    report.Failed = true;
                    report.Error = $"op #{i} failed: {r.Error}";
                    report.FailedAtIndex = i;
                    Undo.RevertAllInCurrentGroup();
                    return report;
                }

                var label = (string)op["label"];
                if (!string.IsNullOrEmpty(label) && r.InstanceID != 0)
                    labels[label] = r.InstanceID;

                report.OpsApplied++;
            }

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return report;
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
