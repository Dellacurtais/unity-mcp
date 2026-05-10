using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    /// All Mecanim authoring + discovery + runtime helpers — AnimatorController,
    /// AnimatorState, transitions, parameters, layers, blend trees, animation
    /// clips, animation events, runtime Animator manipulation.
    public static class AnimationAuthor
    {
        // ========== Discovery ==========

        public static OpResult ListControllers(JObject args)
        {
            var nameContains = (string)args["nameContains"];
            var folder = (string)args["folder"];
            var limit = (int?)args["limit"] ?? 100;

            var arr = new JArray();
            var guids = AssetDatabase.FindAssets("t:AnimatorController");
            int count = 0;
            foreach (var g in guids)
            {
                if (count >= limit) break;
                var path = AssetDatabase.GUIDToAssetPath(g);
                if (!string.IsNullOrEmpty(folder) && !path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrEmpty(nameContains) && name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                arr.Add(new JObject { ["guid"] = g, ["path"] = path, ["name"] = name });
                count++;
            }
            return new OpResult { Ok = true, Summary = $"{count} animator controller(s)", InstanceID = 0 };
        }

        public static OpResult ReadController(JObject args)
        {
            var ac = ResolveController(args, out var err);
            if (ac == null) return OpResult.Fail(err);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{ac.name} ({AssetDatabase.GetAssetPath(ac)})");
            sb.AppendLine($"  parameters: {ac.parameters.Length}");
            foreach (var p in ac.parameters)
            {
                var def = p.type switch
                {
                    AnimatorControllerParameterType.Float => p.defaultFloat.ToString("F3"),
                    AnimatorControllerParameterType.Int => p.defaultInt.ToString(),
                    AnimatorControllerParameterType.Bool => p.defaultBool.ToString(),
                    _ => "—",
                };
                sb.AppendLine($"    {p.name}: {p.type}  default={def}");
            }

            sb.AppendLine($"  layers: {ac.layers.Length}");
            for (int li = 0; li < ac.layers.Length; li++)
            {
                var l = ac.layers[li];
                sb.AppendLine($"  [{li}] '{l.name}' weight={l.defaultWeight} blend={l.blendingMode}");
                var sm = l.stateMachine;
                sb.AppendLine($"      states: {sm.states.Length}");
                foreach (var sc in sm.states)
                {
                    var s = sc.state;
                    var motion = s.motion != null ? s.motion.name : "(none)";
                    sb.AppendLine($"        '{s.name}'  motion={motion} speed={s.speed} transitions={s.transitions.Length}");
                    foreach (var t in s.transitions)
                    {
                        var dest = t.destinationState != null ? t.destinationState.name : "(any/exit)";
                        var conds = string.Join(" && ", t.conditions.Select(c => $"{c.parameter}{ConditionSym(c.mode)}{c.threshold}"));
                        sb.AppendLine($"          → '{dest}' duration={t.duration} hasExit={t.hasExitTime} [{conds}]");
                    }
                }
                if (sm.anyStateTransitions.Length > 0)
                {
                    sb.AppendLine($"      anyState transitions: {sm.anyStateTransitions.Length}");
                    foreach (var t in sm.anyStateTransitions)
                    {
                        var dest = t.destinationState != null ? t.destinationState.name : "(exit)";
                        sb.AppendLine($"        → '{dest}'");
                    }
                }
            }
            return new OpResult { Ok = true, Summary = sb.ToString(), InstanceID = ac.GetInstanceID() };
        }

        public static OpResult ReadClip(JObject args)
        {
            var clip = ResolveClip(args, out var err);
            if (clip == null) return OpResult.Fail(err);

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            var bindings = AnimationUtility.GetCurveBindings(clip);
            var events = AnimationUtility.GetAnimationEvents(clip);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{clip.name} ({AssetDatabase.GetAssetPath(clip)})");
            sb.AppendLine($"  length={clip.length:F3}s  frameRate={clip.frameRate}  isLooping={clip.isLooping}");
            sb.AppendLine($"  loopTime={settings.loopTime} mirror={settings.mirror} cycleOffset={settings.cycleOffset}");
            sb.AppendLine($"  curves: {bindings.Length}");
            int shown = 0;
            foreach (var b in bindings)
            {
                if (shown++ >= 30) { sb.AppendLine($"    [+{bindings.Length - 30} more curves]"); break; }
                sb.AppendLine($"    {b.path}/{b.propertyName} ({b.type.Name})");
            }
            sb.AppendLine($"  events: {events.Length}");
            foreach (var e in events)
                sb.AppendLine($"    @{e.time:F3}s  {e.functionName}(str='{e.stringParameter}' f={e.floatParameter} i={e.intParameter})");

            return new OpResult { Ok = true, Summary = sb.ToString(), InstanceID = clip.GetInstanceID() };
        }

        // ========== Runtime (Animator on a scene GameObject) ==========

        public static OpResult SetAnimatorParameter(JObject args)
        {
            var anim = ResolveAnimator(args, out var err);
            if (anim == null) return OpResult.Fail(err);
            var name = (string)args["name"];
            var type = ((string)args["type"] ?? "auto").ToLowerInvariant();
            var value = args["value"];
            if (string.IsNullOrEmpty(name)) return OpResult.Fail("name is required");

            // Auto-detect type from controller if "auto"
            if (type == "auto")
            {
                if (anim.runtimeAnimatorController is AnimatorController ac)
                {
                    var p = ac.parameters.FirstOrDefault(pp => pp.name == name);
                    if (p == null) return OpResult.Fail($"parameter '{name}' not on controller");
                    type = p.type.ToString().ToLowerInvariant();
                }
                else return OpResult.Fail("animator has no AnimatorController; specify type explicitly");
            }

            try
            {
                switch (type)
                {
                    case "float": anim.SetFloat(name, (float)value); break;
                    case "int": anim.SetInteger(name, (int)value); break;
                    case "bool": anim.SetBool(name, (bool)value); break;
                    case "trigger":
                        if (value != null && value.Type == JTokenType.Boolean && !(bool)value) anim.ResetTrigger(name);
                        else anim.SetTrigger(name);
                        break;
                    default: return OpResult.Fail($"unknown parameter type '{type}'");
                }
            }
            catch (Exception e) { return OpResult.Fail($"failed to set: {e.Message}"); }
            return OpResult.Success(anim.GetInstanceID(), $"set {type} {name} = {value}");
        }

        public static OpResult PlayAnimatorState(JObject args)
        {
            var anim = ResolveAnimator(args, out var err);
            if (anim == null) return OpResult.Fail(err);
            var stateName = (string)args["state"];
            var layer = (int?)args["layer"] ?? 0;
            var normalizedTime = (float?)args["normalizedTime"] ?? 0f;
            if (string.IsNullOrEmpty(stateName)) return OpResult.Fail("state is required");
            anim.Play(stateName, layer, normalizedTime);
            return OpResult.Success(anim.GetInstanceID(), $"playing '{stateName}' on layer {layer}");
        }

        public static OpResult ReadAnimatorState(JObject args)
        {
            var anim = ResolveAnimator(args, out var err);
            if (anim == null) return OpResult.Fail(err);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Animator on '{anim.gameObject.name}' (controller={anim.runtimeAnimatorController?.name ?? "none"})");
            for (int l = 0; l < anim.layerCount; l++)
            {
                var info = anim.GetCurrentAnimatorStateInfo(l);
                sb.AppendLine($"  layer {l} '{anim.GetLayerName(l)}' weight={anim.GetLayerWeight(l):F2}  state hash={info.fullPathHash} normT={info.normalizedTime:F3} length={info.length:F3}");
            }
            if (anim.runtimeAnimatorController is AnimatorController ac)
            {
                sb.AppendLine($"  parameters:");
                foreach (var p in ac.parameters)
                {
                    var v = p.type switch
                    {
                        AnimatorControllerParameterType.Float => anim.GetFloat(p.name).ToString("F3"),
                        AnimatorControllerParameterType.Int => anim.GetInteger(p.name).ToString(),
                        AnimatorControllerParameterType.Bool => anim.GetBool(p.name).ToString(),
                        _ => "(trigger)",
                    };
                    sb.AppendLine($"    {p.name} ({p.type}) = {v}");
                }
            }
            return new OpResult { Ok = true, Summary = sb.ToString(), InstanceID = anim.GetInstanceID() };
        }

        // ========== Authoring — Controller / State / Transition / Parameter ==========

        public static OpResult CreateController(JObject args)
        {
            var path = (string)args["path"];
            if (string.IsNullOrEmpty(path)) return OpResult.Fail("path is required (e.g. Assets/Animations/MyController.controller)");
            if (!path.EndsWith(".controller")) path += ".controller";
            EnsureFolder(Path.GetDirectoryName(path));
            var ac = AnimatorController.CreateAnimatorControllerAtPath(path);
            return OpResult.Success(ac.GetInstanceID(), $"created '{ac.name}' at {path}");
        }

        public static OpResult AddState(JObject args)
        {
            var ac = ResolveController(args, out var err);
            if (ac == null) return OpResult.Fail(err);
            var stateName = (string)args["state"];
            if (string.IsNullOrEmpty(stateName)) return OpResult.Fail("state name is required");
            var layerIndex = (int?)args["layer"] ?? 0;
            if (layerIndex >= ac.layers.Length) return OpResult.Fail($"layer {layerIndex} out of range (controller has {ac.layers.Length})");

            var layer = ac.layers[layerIndex];
            var s = layer.stateMachine.AddState(stateName);
            if (args["clipGuidOrPath"] != null)
            {
                var clip = LoadClip((string)args["clipGuidOrPath"]);
                if (clip != null) s.motion = clip;
            }
            if (args["speed"] != null) s.speed = (float)args["speed"];
            if (args["isDefault"] != null && (bool)args["isDefault"])
                layer.stateMachine.defaultState = s;

            EditorUtility.SetDirty(ac);
            return OpResult.Success(ac.GetInstanceID(), $"added state '{stateName}' to layer {layerIndex}");
        }

        public static OpResult AddTransition(JObject args)
        {
            var ac = ResolveController(args, out var err);
            if (ac == null) return OpResult.Fail(err);
            var fromName = (string)args["from"];
            var toName = (string)args["to"];
            if (string.IsNullOrEmpty(toName)) return OpResult.Fail("to is required");
            var layerIndex = (int?)args["layer"] ?? 0;
            if (layerIndex >= ac.layers.Length) return OpResult.Fail($"layer {layerIndex} out of range");
            var sm = ac.layers[layerIndex].stateMachine;

            var toState = sm.states.FirstOrDefault(s => s.state.name == toName).state;
            if (toState == null) return OpResult.Fail($"to state '{toName}' not found");

            AnimatorStateTransition tr;
            if (string.IsNullOrEmpty(fromName) || fromName == "AnyState" || fromName == "*")
            {
                tr = sm.AddAnyStateTransition(toState);
            }
            else
            {
                var fromState = sm.states.FirstOrDefault(s => s.state.name == fromName).state;
                if (fromState == null) return OpResult.Fail($"from state '{fromName}' not found");
                tr = fromState.AddTransition(toState);
            }

            tr.hasExitTime = (bool?)args["hasExitTime"] ?? false;
            if (args["exitTime"] != null) tr.exitTime = (float)args["exitTime"];
            if (args["duration"] != null) tr.duration = (float)args["duration"];
            if (args["offset"] != null) tr.offset = (float)args["offset"];

            if (args["conditions"] is JArray conds)
            {
                foreach (var c in conds)
                {
                    if (c is JObject co)
                    {
                        var pname = (string)co["parameter"];
                        var modeStr = (string)co["mode"] ?? "Equals";
                        var threshold = (float?)co["threshold"] ?? 0f;
                        if (Enum.TryParse<AnimatorConditionMode>(modeStr, true, out var mode))
                            tr.AddCondition(mode, threshold, pname);
                    }
                }
            }

            EditorUtility.SetDirty(ac);
            return OpResult.Success(ac.GetInstanceID(),
                $"added transition {fromName ?? "AnyState"} → {toName} on layer {layerIndex}");
        }

        public static OpResult AddParameter(JObject args)
        {
            var ac = ResolveController(args, out var err);
            if (ac == null) return OpResult.Fail(err);
            var name = (string)args["name"];
            var typeStr = (string)args["type"] ?? "Float";
            if (string.IsNullOrEmpty(name)) return OpResult.Fail("name is required");
            if (!Enum.TryParse<AnimatorControllerParameterType>(typeStr, true, out var pType))
                return OpResult.Fail($"unknown parameter type '{typeStr}' (use Float/Int/Bool/Trigger)");

            ac.AddParameter(name, pType);

            // Set default if provided. AnimatorController.parameters returns a copy
            // — modify and write the array back to persist the default value.
            if (args["defaultValue"] != null)
            {
                var ps = ac.parameters;
                var idx = Array.FindIndex(ps, p => p.name == name);
                if (idx >= 0)
                {
                    var v = args["defaultValue"];
                    switch (pType)
                    {
                        case AnimatorControllerParameterType.Float: ps[idx].defaultFloat = (float)v; break;
                        case AnimatorControllerParameterType.Int: ps[idx].defaultInt = (int)v; break;
                        case AnimatorControllerParameterType.Bool: ps[idx].defaultBool = (bool)v; break;
                    }
                    ac.parameters = ps;
                }
            }

            EditorUtility.SetDirty(ac);
            return OpResult.Success(ac.GetInstanceID(), $"added parameter {pType} {name}");
        }

        public static OpResult AssignControllerToAnimator(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            var anim = go.GetComponent<Animator>();
            if (anim == null) return OpResult.Fail("no Animator on GameObject — call add_component Animator first");

            var ctrl = LoadController((string)args["controllerGuidOrPath"]);
            if (ctrl == null) return OpResult.Fail($"controller not found: {args["controllerGuidOrPath"]}");

            Undo.RecordObject(anim, "Assign Animator Controller");
            anim.runtimeAnimatorController = ctrl;
            return OpResult.Success(id, $"assigned controller '{ctrl.name}' to {go.name}");
        }

        // ========== Tier 2 — Layers / Blend Trees / Events / Avatar ==========

        public static OpResult AddLayer(JObject args)
        {
            var ac = ResolveController(args, out var err);
            if (ac == null) return OpResult.Fail(err);
            var name = (string)args["name"] ?? $"Layer {ac.layers.Length}";
            ac.AddLayer(name);

            // Modify by copy-and-write-back.
            var layers = ac.layers;
            var idx = layers.Length - 1;
            if (args["weight"] != null) layers[idx].defaultWeight = (float)args["weight"];
            if (args["blendingMode"] != null
                && Enum.TryParse<AnimatorLayerBlendingMode>((string)args["blendingMode"], true, out var bm))
                layers[idx].blendingMode = bm;
            if (args["avatarMaskGuidOrPath"] != null)
            {
                var maskRef = (string)args["avatarMaskGuidOrPath"];
                var path = maskRef.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    ? maskRef
                    : AssetDatabase.GUIDToAssetPath(maskRef);
                var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(path);
                if (mask != null) layers[idx].avatarMask = mask;
            }
            ac.layers = layers;

            EditorUtility.SetDirty(ac);
            return OpResult.Success(ac.GetInstanceID(), $"added layer '{name}' (idx={idx})");
        }

        public static OpResult CreateBlendTree(JObject args)
        {
            var ac = ResolveController(args, out var err);
            if (ac == null) return OpResult.Fail(err);
            var stateName = (string)args["state"];
            var blendType = (string)args["blendType"] ?? "Simple1D";
            var blendParameter = (string)args["blendParameter"];
            var blendParameterY = (string)args["blendParameterY"];
            var layerIndex = (int?)args["layer"] ?? 0;
            if (string.IsNullOrEmpty(stateName)) return OpResult.Fail("state name is required");

            BlendTree tree;
            var sm = ac.layers[layerIndex].stateMachine;
            ac.CreateBlendTreeInController(stateName, out tree, layerIndex);
            if (Enum.TryParse<BlendTreeType>(blendType, true, out var bt)) tree.blendType = bt;
            if (!string.IsNullOrEmpty(blendParameter)) tree.blendParameter = blendParameter;
            if (!string.IsNullOrEmpty(blendParameterY)) tree.blendParameterY = blendParameterY;

            EditorUtility.SetDirty(ac);
            return OpResult.Success(ac.GetInstanceID(),
                $"created blend tree state '{stateName}' ({blendType}, param={blendParameter})");
        }

        public static OpResult AddBlendTreeMotion(JObject args)
        {
            var ac = ResolveController(args, out var err);
            if (ac == null) return OpResult.Fail(err);
            var stateName = (string)args["state"];
            var layerIndex = (int?)args["layer"] ?? 0;
            var clip = LoadClip((string)args["clipGuidOrPath"]);
            if (clip == null) return OpResult.Fail($"clip not found: {args["clipGuidOrPath"]}");

            var sm = ac.layers[layerIndex].stateMachine;
            var state = sm.states.FirstOrDefault(s => s.state.name == stateName).state;
            if (state == null) return OpResult.Fail($"state '{stateName}' not found");
            var tree = state.motion as BlendTree;
            if (tree == null) return OpResult.Fail($"state '{stateName}' is not a blend tree");

            if (args["position"] is JArray pos && pos.Count >= 2)
                tree.AddChild(clip, new Vector2((float)pos[0], (float)pos[1]));
            else
                tree.AddChild(clip, (float?)args["threshold"] ?? 0f);

            EditorUtility.SetDirty(ac);
            return OpResult.Success(ac.GetInstanceID(), $"added '{clip.name}' to blend tree '{stateName}'");
        }

        public static OpResult AddAnimationEvent(JObject args)
        {
            var clip = ResolveClip(args, out var err);
            if (clip == null) return OpResult.Fail(err);

            var ev = new AnimationEvent
            {
                time = (float?)args["time"] ?? 0f,
                functionName = (string)args["functionName"] ?? "",
                stringParameter = (string)args["stringParameter"] ?? "",
                floatParameter = (float?)args["floatParameter"] ?? 0f,
                intParameter = (int?)args["intParameter"] ?? 0,
                messageOptions = SendMessageOptions.DontRequireReceiver,
            };

            var events = AnimationUtility.GetAnimationEvents(clip).ToList();
            events.Add(ev);
            AnimationUtility.SetAnimationEvents(clip, events.ToArray());
            EditorUtility.SetDirty(clip);
            return OpResult.Success(clip.GetInstanceID(),
                $"added event '{ev.functionName}' @ {ev.time:F3}s to '{clip.name}'");
        }

        public static OpResult ReadAvatar(JObject args)
        {
            var avatarRef = (string)args["guidOrPath"];
            var path = avatarRef.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? avatarRef
                : AssetDatabase.GUIDToAssetPath(avatarRef);
            var avatar = AssetDatabase.LoadAssetAtPath<Avatar>(path);
            if (avatar == null) return OpResult.Fail($"avatar not found: {avatarRef}");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{avatar.name}  isHuman={avatar.isHuman}  isValid={avatar.isValid}");
            if (avatar.isHuman)
            {
                // humanDescription.human gives bone mappings (not always populated outside import context)
                var hd = avatar.humanDescription;
                sb.AppendLine($"  upperArmTwist={hd.upperArmTwist}  upperLegTwist={hd.upperLegTwist}");
                sb.AppendLine($"  human bones: {hd.human?.Length ?? 0}");
                if (hd.human != null)
                {
                    foreach (var b in hd.human)
                        sb.AppendLine($"    {b.humanName} ← '{b.boneName}'");
                }
                sb.AppendLine($"  skeleton transforms: {hd.skeleton?.Length ?? 0}");
            }
            return new OpResult { Ok = true, Summary = sb.ToString(), InstanceID = avatar.GetInstanceID() };
        }

        // ========== Helpers ==========

        private static AnimatorController ResolveController(JObject args, out string err)
        {
            err = null;
            var idObj = args["controllerInstanceId"];
            if (idObj != null && idObj.Type == JTokenType.Integer)
            {
                var id = (int)idObj;
                var ac = EditorUtility.InstanceIDToObject(id) as AnimatorController;
                if (ac == null) { err = $"controller not found: instanceId={id}"; return null; }
                return ac;
            }
            var refStr = (string)args["controllerGuidOrPath"];
            if (string.IsNullOrEmpty(refStr)) { err = "controllerInstanceId or controllerGuidOrPath required"; return null; }
            var ctrl = LoadController(refStr) as AnimatorController;
            if (ctrl == null) { err = $"controller not found: {refStr}"; return null; }
            return ctrl;
        }

        private static RuntimeAnimatorController LoadController(string refStr)
        {
            if (string.IsNullOrEmpty(refStr)) return null;
            var path = refStr.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? refStr : AssetDatabase.GUIDToAssetPath(refStr);
            return AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(path);
        }

        private static AnimationClip ResolveClip(JObject args, out string err)
        {
            err = null;
            var refStr = (string)args["clipGuidOrPath"] ?? (string)args["guidOrPath"];
            var clip = LoadClip(refStr);
            if (clip == null) { err = $"clip not found: {refStr}"; return null; }
            return clip;
        }

        private static AnimationClip LoadClip(string refStr)
        {
            if (string.IsNullOrEmpty(refStr)) return null;
            var path = refStr.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? refStr : AssetDatabase.GUIDToAssetPath(refStr);
            return AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        }

        private static Animator ResolveAnimator(JObject args, out string err)
        {
            err = null;
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) { err = $"GameObject not found: instanceId={id}"; return null; }
            var anim = go.GetComponent<Animator>();
            if (anim == null) { err = "no Animator on GameObject"; return null; }
            return anim;
        }

        private static string ConditionSym(AnimatorConditionMode m) => m switch
        {
            AnimatorConditionMode.Greater => ">",
            AnimatorConditionMode.Less => "<",
            AnimatorConditionMode.Equals => "==",
            AnimatorConditionMode.NotEqual => "!=",
            AnimatorConditionMode.If => " is true",
            AnimatorConditionMode.IfNot => " is false",
            _ => "?",
        };

        private static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
            var parent = Path.GetDirectoryName(folder);
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            var name = Path.GetFileName(folder);
            if (!string.IsNullOrEmpty(parent)) AssetDatabase.CreateFolder(parent, name);
        }
    }
}
