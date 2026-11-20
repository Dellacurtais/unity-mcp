using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public static class AnimationAuthor
    {
        public static OpResult ListControllers(JObject args)
        {
            var nameContains = (string)args["nameContains"];
            var folder = NormalizeFolder((string)args["folder"]);
            if (args["folder"] != null && folder == null)
                return Fail("BAD_ARGS", "folder must be Assets or a folder under Assets/");
            if (!TryReadInt(args["limit"], 100, 1, 500, "limit", out var limit, out var limitError))
                return OpResult.Fail(limitError);

            var paths = AssetDatabase.FindAssets("t:AnimatorController")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .Where(path => folder == null || PathIsUnderFolder(path, folder))
                .Where(path => string.IsNullOrEmpty(nameContains)
                    || Path.GetFileNameWithoutExtension(path).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            var controllers = new JArray();
            foreach (var path in paths.Take(limit))
            {
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                if (controller != null) controllers.Add(AssetReference(controller));
            }

            var result = OpResult.Success(0, $"{controllers.Count} of {paths.Length} AnimatorController asset(s)");
            result.Data = new JObject
            {
                ["controllers"] = controllers,
                ["count"] = controllers.Count,
                ["total"] = paths.Length,
                ["limit"] = limit,
                ["truncated"] = controllers.Count < paths.Length,
            };
            return result;
        }

        public static OpResult ReadController(JObject args)
        {
            var controller = ResolveController(args, out var error, false);
            if (controller == null) return OpResult.Fail(error);

            var parameters = new JArray(controller.parameters.Select(ParameterJson));
            var layers = new JArray();
            for (var index = 0; index < controller.layers.Length; index++)
            {
                var layer = controller.layers[index];
                var states = new JArray();
                if (layer.stateMachine != null)
                {
                    foreach (var child in layer.stateMachine.states.OrderBy(child => child.state != null ? child.state.name : string.Empty, StringComparer.Ordinal))
                    {
                        if (child.state != null) states.Add(StateJson(child.state));
                    }
                }

                var anyStateTransitions = layer.stateMachine != null
                    ? new JArray(layer.stateMachine.anyStateTransitions.Select(TransitionJson))
                    : new JArray();
                layers.Add(new JObject
                {
                    ["index"] = index,
                    ["name"] = layer.name,
                    ["defaultWeight"] = layer.defaultWeight,
                    ["blendingMode"] = layer.blendingMode.ToString(),
                    ["avatarMask"] = AssetReference(layer.avatarMask),
                    ["stateMachineInstanceId"] = layer.stateMachine != null ? layer.stateMachine.GetMcpInstanceId() : 0,
                    ["defaultState"] = layer.stateMachine != null && layer.stateMachine.defaultState != null
                        ? layer.stateMachine.defaultState.name
                        : null,
                    ["states"] = states,
                    ["anyStateTransitions"] = anyStateTransitions,
                });
            }

            var data = AssetReference(controller);
            data["parameters"] = parameters;
            data["layers"] = layers;
            var result = OpResult.Success(controller.GetMcpInstanceId(),
                $"AnimatorController '{controller.name}': {parameters.Count} parameter(s), {layers.Count} layer(s)");
            result.Data = data;
            return result;
        }

        public static OpResult ReadClip(JObject args)
        {
            var clip = ResolveClip(args, out var error, false);
            if (clip == null) return OpResult.Fail(error);

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            var floatBindings = AnimationUtility.GetCurveBindings(clip)
                .OrderBy(binding => binding.path, StringComparer.Ordinal)
                .ThenBy(binding => binding.type != null ? binding.type.FullName : string.Empty, StringComparer.Ordinal)
                .ThenBy(binding => binding.propertyName, StringComparer.Ordinal)
                .ToArray();
            var objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip)
                .OrderBy(binding => binding.path, StringComparer.Ordinal)
                .ThenBy(binding => binding.type != null ? binding.type.FullName : string.Empty, StringComparer.Ordinal)
                .ThenBy(binding => binding.propertyName, StringComparer.Ordinal)
                .ToArray();
            var events = AnimationUtility.GetAnimationEvents(clip)
                .OrderBy(animationEvent => animationEvent.time)
                .ThenBy(animationEvent => animationEvent.functionName, StringComparer.Ordinal)
                .ToArray();

            var data = AssetReference(clip);
            data["length"] = clip.length;
            data["frameRate"] = clip.frameRate;
            data["isLooping"] = clip.isLooping;
            data["legacy"] = clip.legacy;
            data["humanMotion"] = clip.humanMotion;
            data["settings"] = new JObject
            {
                ["loopTime"] = settings.loopTime,
                ["loopBlend"] = settings.loopBlend,
                ["loopBlendOrientation"] = settings.loopBlendOrientation,
                ["loopBlendPositionY"] = settings.loopBlendPositionY,
                ["loopBlendPositionXZ"] = settings.loopBlendPositionXZ,
                ["mirror"] = settings.mirror,
                ["cycleOffset"] = settings.cycleOffset,
            };
            data["curveCount"] = floatBindings.Length + objectBindings.Length;
            data["floatCurveBindings"] = new JArray(floatBindings.Select(binding => BindingJson(binding, "float")));
            data["objectReferenceCurveBindings"] = new JArray(objectBindings.Select(binding => BindingJson(binding, "objectReference")));
            data["eventCount"] = events.Length;
            data["events"] = new JArray(events.Select(AnimationEventJson));

            var result = OpResult.Success(clip.GetMcpInstanceId(),
                $"AnimationClip '{clip.name}': {data["curveCount"]} curve(s), {events.Length} event(s)");
            result.Data = data;
            return result;
        }

        public static OpResult SetAnimatorParameter(JObject args)
        {
            if (!EditorApplication.isPlaying)
                return Fail("PLAY_MODE_REQUIRED", "set_animator_parameter is only available in Play Mode");

            var animator = ResolveAnimator(args, out var error);
            if (animator == null) return OpResult.Fail(error);
            var name = ((string)args["name"] ?? string.Empty).Trim();
            if (name.Length == 0) return Fail("BAD_ARGS", "name is required");

            var parameter = animator.parameters.FirstOrDefault(candidate => candidate.name == name);
            if (parameter == null) return Fail("PARAMETER_NOT_FOUND", $"parameter '{name}' is not on the Animator controller");
            var requestedType = ((string)args["type"] ?? "auto").Trim().ToLowerInvariant();
            var actualType = parameter.type.ToString().ToLowerInvariant();
            if (requestedType != "auto" && requestedType != actualType)
                return Fail("PARAMETER_TYPE_MISMATCH", $"parameter '{name}' is {parameter.type}, not {requestedType}");

            var value = args["value"];
            if (parameter.type != AnimatorControllerParameterType.Trigger && value == null)
                return Fail("BAD_ARGS", $"value is required for a {parameter.type} parameter");
            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Float:
                    if (!TryReadFloat(value, 0f, null, null, "value", out var floatValue, out var floatError))
                        return OpResult.Fail(floatError);
                    animator.SetFloat(name, floatValue);
                    break;
                case AnimatorControllerParameterType.Int:
                    if (!TryReadInt(value, 0, int.MinValue, int.MaxValue, "value", out var intValue, out var intError, true))
                        return OpResult.Fail(intError);
                    animator.SetInteger(name, intValue);
                    break;
                case AnimatorControllerParameterType.Bool:
                    if (value == null || value.Type != JTokenType.Boolean)
                        return Fail("BAD_ARGS", "value must be a boolean for a Bool parameter");
                    animator.SetBool(name, (bool)value);
                    break;
                case AnimatorControllerParameterType.Trigger:
                    if (value != null && value.Type != JTokenType.Boolean)
                        return Fail("BAD_ARGS", "value must be omitted or boolean for a Trigger parameter");
                    if (value != null && !(bool)value) animator.ResetTrigger(name);
                    else animator.SetTrigger(name);
                    break;
                default:
                    return Fail("UNSUPPORTED_PARAMETER_TYPE", parameter.type.ToString());
            }

            var result = OpResult.Success(animator.GetMcpInstanceId(), $"set {parameter.type} parameter '{name}'");
            result.Data = new JObject
            {
                ["instanceId"] = animator.gameObject.GetMcpInstanceId(),
                ["animatorInstanceId"] = animator.GetMcpInstanceId(),
                ["parameter"] = RuntimeParameterJson(animator, parameter),
            };
            return result;
        }

        public static OpResult PlayAnimatorState(JObject args)
        {
            if (!EditorApplication.isPlaying)
                return Fail("PLAY_MODE_REQUIRED", "play_animator_state is only available in Play Mode");

            var animator = ResolveAnimator(args, out var error);
            if (animator == null) return OpResult.Fail(error);
            var requestedState = ((string)args["state"] ?? string.Empty).Trim();
            if (requestedState.Length == 0) return Fail("BAD_ARGS", "state is required");
            if (!TryReadInt(args["layer"], 0, -1, Math.Max(0, animator.layerCount - 1), "layer", out var layer, out var layerError))
                return OpResult.Fail(layerError);
            if (!TryReadFloat(args["normalizedTime"], 0f, 0f, 1f, "normalizedTime", out var normalizedTime, out var timeError))
                return OpResult.Fail(timeError);

            var resolvedState = ResolveRuntimeStateName(animator, requestedState, layer, out var resolvedLayer);
            if (resolvedState == null)
                return Fail("STATE_NOT_FOUND", $"state '{requestedState}' was not found on layer {(layer < 0 ? "any" : layer.ToString())}");

            animator.Play(resolvedState, resolvedLayer, normalizedTime);
            var result = OpResult.Success(animator.GetMcpInstanceId(), $"playing '{resolvedState}' on layer {resolvedLayer}");
            result.Data = new JObject
            {
                ["instanceId"] = animator.gameObject.GetMcpInstanceId(),
                ["animatorInstanceId"] = animator.GetMcpInstanceId(),
                ["state"] = resolvedState,
                ["layer"] = resolvedLayer,
                ["normalizedTime"] = normalizedTime,
            };
            return result;
        }

        public static OpResult ReadAnimatorState(JObject args)
        {
            if (!EditorApplication.isPlaying)
                return Fail("PLAY_MODE_REQUIRED", "read_animator_state is only available in Play Mode");

            var animator = ResolveAnimator(args, out var error);
            if (animator == null) return OpResult.Fail(error);
            var layers = new JArray();
            for (var index = 0; index < animator.layerCount; index++)
            {
                var info = animator.GetCurrentAnimatorStateInfo(index);
                layers.Add(new JObject
                {
                    ["index"] = index,
                    ["name"] = animator.GetLayerName(index),
                    ["weight"] = animator.GetLayerWeight(index),
                    ["fullPathHash"] = info.fullPathHash,
                    ["shortNameHash"] = info.shortNameHash,
                    ["normalizedTime"] = info.normalizedTime,
                    ["length"] = info.length,
                    ["speed"] = info.speed,
                    ["speedMultiplier"] = info.speedMultiplier,
                    ["loop"] = info.loop,
                });
            }

            var parameters = new JArray(animator.parameters.Select(parameter => RuntimeParameterJson(animator, parameter)));
            var result = OpResult.Success(animator.GetMcpInstanceId(),
                $"Animator on '{animator.gameObject.name}': {layers.Count} layer(s), {parameters.Count} parameter(s)");
            result.Data = new JObject
            {
                ["instanceId"] = animator.gameObject.GetMcpInstanceId(),
                ["animatorInstanceId"] = animator.GetMcpInstanceId(),
                ["controller"] = AssetReference(animator.runtimeAnimatorController),
                ["layers"] = layers,
                ["parameters"] = parameters,
            };
            return result;
        }

        public static OpResult CreateController(JObject args)
        {
            var requestedPath = ((string)args["path"] ?? string.Empty).Trim();
            if (!requestedPath.EndsWith(".controller", StringComparison.OrdinalIgnoreCase))
                requestedPath += ".controller";
            if (!TryNormalizeAssetPath(requestedPath, ".controller", out var path, out var pathError))
                return Fail("BAD_ASSET_PATH", pathError);
            if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                return Fail("ASSET_EXISTS", path);

            EnsureFolderForAsset(path);
            AnimatorController controller;
            try
            {
                controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            }
            catch (Exception exception)
            {
                return Fail("CREATE_CONTROLLER_FAILED", exception.Message);
            }
            if (controller == null) return Fail("CREATE_CONTROLLER_FAILED", path);

            var result = OpResult.Success(controller.GetMcpInstanceId(), $"created AnimatorController '{path}'");
            result.CreatedAssetPath = path;
            result.Data = new JObject
            {
                ["controller"] = AssetReference(controller),
                ["assetCreated"] = true,
                ["savedAssets"] = false,
            };
            return result;
        }

        public static OpResult AddState(JObject args)
        {
            var controller = ResolveController(args, out var error);
            if (controller == null) return OpResult.Fail(error);
            if (!TryResolveLayer(controller, args["layer"], out var layerIndex, out var layer, out var layerError))
                return OpResult.Fail(layerError);
            var stateName = ((string)args["state"] ?? string.Empty).Trim();
            if (stateName.Length == 0) return Fail("BAD_ARGS", "state is required");
            if (FindState(layer.stateMachine, stateName) != null)
                return Fail("STATE_EXISTS", $"state '{stateName}' already exists on layer {layerIndex}");

            AnimationClip clip = null;
            if (args["clipGuidOrPath"] != null)
            {
                clip = LoadClip((string)args["clipGuidOrPath"]);
                if (clip == null) return Fail("CLIP_NOT_FOUND", (string)args["clipGuidOrPath"]);
            }
            if (!TryReadFloat(args["speed"], 1f, null, null, "speed", out var speed, out var speedError))
                return OpResult.Fail(speedError);
            var isDefault = (bool?)args["isDefault"] ?? false;

            using (var mutation = new MutationScope("Add Animator State"))
            {
                mutation.Record(controller, "Add Animator State");
                mutation.Record(layer.stateMachine, "Add Animator State");
                var state = layer.stateMachine.AddState(stateName);
                state.motion = clip;
                state.speed = speed;
                if (isDefault) layer.stateMachine.defaultState = state;
                mutation.Touch(state);
                mutation.Complete();

                var result = OpResult.Success(controller.GetMcpInstanceId(), $"added state '{stateName}' to layer {layerIndex}");
                result.Data = new JObject
                {
                    ["controller"] = AssetReference(controller),
                    ["layer"] = layerIndex,
                    ["state"] = StateJson(state),
                    ["isDefault"] = layer.stateMachine.defaultState == state,
                };
                return result;
            }
        }

        public static OpResult AddTransition(JObject args)
        {
            var controller = ResolveController(args, out var error);
            if (controller == null) return OpResult.Fail(error);
            if (!TryResolveLayer(controller, args["layer"], out var layerIndex, out var layer, out var layerError))
                return OpResult.Fail(layerError);

            var fromName = ((string)args["from"] ?? string.Empty).Trim();
            var anyState = fromName.Length == 0 || fromName == "*" || string.Equals(fromName, "AnyState", StringComparison.OrdinalIgnoreCase);
            var toName = ((string)args["to"] ?? string.Empty).Trim();
            if (toName.Length == 0) return Fail("BAD_ARGS", "to is required");
            var toState = FindState(layer.stateMachine, toName);
            if (toState == null) return Fail("STATE_NOT_FOUND", $"to state '{toName}' was not found on layer {layerIndex}");
            var fromState = anyState ? null : FindState(layer.stateMachine, fromName);
            if (!anyState && fromState == null)
                return Fail("STATE_NOT_FOUND", $"from state '{fromName}' was not found on layer {layerIndex}");

            var hasExitTime = (bool?)args["hasExitTime"] ?? false;
            var fixedDuration = (bool?)args["fixedDuration"] ?? false;
            if (!TryReadFloat(args["exitTime"], 0.85f, 0f, null, "exitTime", out var exitTime, out var exitError))
                return OpResult.Fail(exitError);
            if (!TryReadFloat(args["duration"], 0.25f, 0f, null, "duration", out var duration, out var durationError))
                return OpResult.Fail(durationError);
            if (!TryReadFloat(args["offset"], 0f, 0f, 1f, "offset", out var offset, out var offsetError))
                return OpResult.Fail(offsetError);
            if (!TryParseConditions(controller, args["conditions"] as JArray, out var conditions, out var conditionError))
                return OpResult.Fail(conditionError);

            using (var mutation = new MutationScope("Add Animator Transition"))
            {
                mutation.Record(controller, "Add Animator Transition");
                mutation.Record(layer.stateMachine, "Add Animator Transition");
                if (fromState != null) mutation.Record(fromState, "Add Animator Transition");
                var transition = anyState
                    ? layer.stateMachine.AddAnyStateTransition(toState)
                    : fromState.AddTransition(toState);
                transition.hasExitTime = hasExitTime;
                transition.exitTime = exitTime;
                transition.duration = duration;
                transition.hasFixedDuration = fixedDuration;
                transition.offset = offset;
                foreach (var condition in conditions)
                    transition.AddCondition(condition.Mode, condition.Threshold, condition.Parameter);
                mutation.Touch(transition);
                mutation.Complete();

                var result = OpResult.Success(controller.GetMcpInstanceId(),
                    $"added transition {(anyState ? "AnyState" : fromName)} -> {toName} on layer {layerIndex}");
                result.Data = new JObject
                {
                    ["controller"] = AssetReference(controller),
                    ["layer"] = layerIndex,
                    ["transition"] = TransitionJson(transition),
                };
                return result;
            }
        }

        public static OpResult AddParameter(JObject args)
        {
            var controller = ResolveController(args, out var error);
            if (controller == null) return OpResult.Fail(error);
            var name = ((string)args["name"] ?? string.Empty).Trim();
            if (name.Length == 0) return Fail("BAD_ARGS", "name is required");
            if (controller.parameters.Any(parameter => parameter.name == name))
                return Fail("PARAMETER_EXISTS", $"parameter '{name}' already exists");
            if (!Enum.TryParse((string)args["type"] ?? "Float", true, out AnimatorControllerParameterType parameterType)
                || parameterType == AnimatorControllerParameterType.Trigger && !string.Equals((string)args["type"] ?? "Float", "Trigger", StringComparison.OrdinalIgnoreCase)
                || !Enum.IsDefined(typeof(AnimatorControllerParameterType), parameterType))
                return Fail("BAD_PARAMETER_TYPE", (string)args["type"] ?? "Float");

            var defaultValue = args["defaultValue"];
            float defaultFloat = 0f;
            int defaultInt = 0;
            bool defaultBool = false;
            switch (parameterType)
            {
                case AnimatorControllerParameterType.Float:
                    if (!TryReadFloat(defaultValue, 0f, null, null, "defaultValue", out defaultFloat, out var floatError))
                        return OpResult.Fail(floatError);
                    break;
                case AnimatorControllerParameterType.Int:
                    if (!TryReadInt(defaultValue, 0, int.MinValue, int.MaxValue, "defaultValue", out defaultInt, out var intError))
                        return OpResult.Fail(intError);
                    break;
                case AnimatorControllerParameterType.Bool:
                    if (defaultValue != null && defaultValue.Type != JTokenType.Boolean)
                        return Fail("BAD_ARGS", "defaultValue must be boolean for a Bool parameter");
                    defaultBool = (bool?)defaultValue ?? false;
                    break;
                case AnimatorControllerParameterType.Trigger:
                    if (defaultValue != null)
                        return Fail("BAD_ARGS", "Trigger parameters do not have a defaultValue");
                    break;
            }

            using (var mutation = new MutationScope("Add Animator Parameter"))
            {
                mutation.Record(controller, "Add Animator Parameter");
                controller.AddParameter(name, parameterType);
                var parameters = controller.parameters;
                var index = Array.FindIndex(parameters, parameter => parameter.name == name);
                if (index >= 0)
                {
                    switch (parameterType)
                    {
                        case AnimatorControllerParameterType.Float: parameters[index].defaultFloat = defaultFloat; break;
                        case AnimatorControllerParameterType.Int: parameters[index].defaultInt = defaultInt; break;
                        case AnimatorControllerParameterType.Bool: parameters[index].defaultBool = defaultBool; break;
                    }
                    controller.parameters = parameters;
                }
                mutation.Complete();

                var parameter = controller.parameters.Single(candidate => candidate.name == name);
                var result = OpResult.Success(controller.GetMcpInstanceId(), $"added {parameterType} parameter '{name}'");
                result.Data = new JObject
                {
                    ["controller"] = AssetReference(controller),
                    ["parameter"] = ParameterJson(parameter),
                };
                return result;
            }
        }

        public static OpResult AssignControllerToAnimator(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var gameObject = UnityObjectIds.Resolve(id) as GameObject;
            if (gameObject == null) return Fail("TARGET_NOT_FOUND", $"GameObject not found: instanceId={id}");
            var animator = gameObject.GetComponent<Animator>();
            if (animator == null) return Fail("ANIMATOR_NOT_FOUND", "no Animator on GameObject");
            var controller = LoadRuntimeController((string)args["controllerGuidOrPath"]);
            if (controller == null) return Fail("CONTROLLER_NOT_FOUND", (string)args["controllerGuidOrPath"]);

            var serializedAnimator = new SerializedObject(animator);
            serializedAnimator.UpdateIfRequiredOrScript();
            var controllerProperty = serializedAnimator.FindProperty("m_Controller");
            if (controllerProperty == null) return Fail("CONTROLLER_PROPERTY_NOT_FOUND", "Animator.m_Controller is unavailable");

            using (var mutation = new MutationScope("Assign Animator Controller"))
            {
                controllerProperty.objectReferenceValue = controller;
                if (!mutation.Apply(serializedAnimator, animator))
                    return Fail("ASSIGNMENT_NOT_APPLIED", "Animator controller assignment produced no serialized change");
                mutation.Complete();
                var result = OpResult.Success(id, $"assigned controller '{controller.name}' to '{gameObject.name}'");
                result.Data = new JObject
                {
                    ["instanceId"] = id,
                    ["animatorInstanceId"] = animator.GetMcpInstanceId(),
                    ["controller"] = AssetReference(controller),
                    ["target"] = ToolTargetRef.Capture(gameObject)?.ToJson(),
                };
                return result;
            }
        }

        public static OpResult AddLayer(JObject args)
        {
            var controller = ResolveController(args, out var error);
            if (controller == null) return OpResult.Fail(error);
            var requestedName = ((string)args["name"] ?? string.Empty).Trim();
            var name = requestedName.Length > 0 ? requestedName : NextLayerName(controller);
            if (controller.layers.Any(layer => layer.name == name))
                return Fail("LAYER_EXISTS", $"layer '{name}' already exists");
            if (!TryReadFloat(args["weight"], 1f, 0f, 1f, "weight", out var weight, out var weightError))
                return OpResult.Fail(weightError);
            if (!Enum.TryParse((string)args["blendingMode"] ?? "Override", true, out AnimatorLayerBlendingMode blendingMode)
                || !Enum.IsDefined(typeof(AnimatorLayerBlendingMode), blendingMode))
                return Fail("BAD_BLENDING_MODE", (string)args["blendingMode"]);

            AvatarMask avatarMask = null;
            if (args["avatarMaskGuidOrPath"] != null)
            {
                avatarMask = LoadAsset<AvatarMask>((string)args["avatarMaskGuidOrPath"]);
                if (avatarMask == null) return Fail("AVATAR_MASK_NOT_FOUND", (string)args["avatarMaskGuidOrPath"]);
            }

            using (var mutation = new MutationScope("Add Animator Layer"))
            {
                mutation.Record(controller, "Add Animator Layer");
                controller.AddLayer(name);
                var layers = controller.layers;
                var index = layers.Length - 1;
                layers[index].defaultWeight = weight;
                layers[index].blendingMode = blendingMode;
                layers[index].avatarMask = avatarMask;
                controller.layers = layers;
                if (layers[index].stateMachine != null) mutation.Touch(layers[index].stateMachine);
                mutation.Complete();

                var result = OpResult.Success(controller.GetMcpInstanceId(), $"added layer '{name}' at index {index}");
                result.Data = new JObject
                {
                    ["controller"] = AssetReference(controller),
                    ["layer"] = new JObject
                    {
                        ["index"] = index,
                        ["name"] = layers[index].name,
                        ["defaultWeight"] = layers[index].defaultWeight,
                        ["blendingMode"] = layers[index].blendingMode.ToString(),
                        ["avatarMask"] = AssetReference(layers[index].avatarMask),
                        ["stateMachineInstanceId"] = layers[index].stateMachine != null ? layers[index].stateMachine.GetMcpInstanceId() : 0,
                    },
                };
                return result;
            }
        }

        public static OpResult CreateBlendTree(JObject args)
        {
            var controller = ResolveController(args, out var error);
            if (controller == null) return OpResult.Fail(error);
            if (!TryResolveLayer(controller, args["layer"], out var layerIndex, out var layer, out var layerError))
                return OpResult.Fail(layerError);
            var stateName = ((string)args["state"] ?? string.Empty).Trim();
            if (stateName.Length == 0) return Fail("BAD_ARGS", "state is required");
            if (FindState(layer.stateMachine, stateName) != null)
                return Fail("STATE_EXISTS", $"state '{stateName}' already exists on layer {layerIndex}");
            if (!Enum.TryParse((string)args["blendType"] ?? "Simple1D", true, out BlendTreeType blendType)
                || !Enum.IsDefined(typeof(BlendTreeType), blendType))
                return Fail("BAD_BLEND_TYPE", (string)args["blendType"]);

            var blendParameter = ((string)args["blendParameter"] ?? string.Empty).Trim();
            var blendParameterY = ((string)args["blendParameterY"] ?? string.Empty).Trim();
            if (!ValidateBlendParameters(controller, blendType, blendParameter, blendParameterY, out var parameterError))
                return OpResult.Fail(parameterError);

            using (var mutation = new MutationScope("Create Blend Tree"))
            {
                mutation.Record(controller, "Create Blend Tree");
                mutation.Record(layer.stateMachine, "Create Blend Tree");
                var state = controller.CreateBlendTreeInController(stateName, out var tree, layerIndex);
                tree.blendType = blendType;
                tree.useAutomaticThresholds = false;
                if (blendParameter.Length > 0) tree.blendParameter = blendParameter;
                if (blendParameterY.Length > 0) tree.blendParameterY = blendParameterY;
                mutation.Touch(state);
                mutation.Touch(tree);
                mutation.Complete();

                var result = OpResult.Success(controller.GetMcpInstanceId(), $"created {blendType} BlendTree state '{stateName}'");
                result.Data = new JObject
                {
                    ["controller"] = AssetReference(controller),
                    ["layer"] = layerIndex,
                    ["blendTree"] = BlendTreeJson(tree, stateName),
                };
                return result;
            }
        }

        public static OpResult AddBlendTreeMotion(JObject args)
        {
            var controller = ResolveController(args, out var error);
            if (controller == null) return OpResult.Fail(error);
            if (!TryResolveLayer(controller, args["layer"], out var layerIndex, out var layer, out var layerError))
                return OpResult.Fail(layerError);
            var stateName = ((string)args["state"] ?? string.Empty).Trim();
            var state = FindState(layer.stateMachine, stateName);
            if (state == null) return Fail("STATE_NOT_FOUND", $"state '{stateName}' was not found on layer {layerIndex}");
            var tree = state.motion as BlendTree;
            if (tree == null) return Fail("NOT_A_BLEND_TREE", $"state '{stateName}' does not use a BlendTree");
            var clip = LoadClip((string)args["clipGuidOrPath"]);
            if (clip == null) return Fail("CLIP_NOT_FOUND", (string)args["clipGuidOrPath"]);

            float threshold = 0f;
            Vector2 position = Vector2.zero;
            string directBlendParameter = null;
            switch (tree.blendType)
            {
                case BlendTreeType.Simple1D:
                    if (args["position"] != null || args["directBlendParameter"] != null || args["threshold"] == null)
                        return Fail("BAD_BLEND_CHILD", "Simple1D requires threshold and does not accept position or directBlendParameter");
                    if (!TryReadFloat(args["threshold"], 0f, null, null, "threshold", out threshold, out var thresholdError))
                        return OpResult.Fail(thresholdError);
                    break;
                case BlendTreeType.SimpleDirectional2D:
                case BlendTreeType.FreeformDirectional2D:
                case BlendTreeType.FreeformCartesian2D:
                    if (args["threshold"] != null || args["directBlendParameter"] != null)
                        return Fail("BAD_BLEND_CHILD", "2D BlendTrees require position and do not accept threshold or directBlendParameter");
                    if (!TryReadPosition(args["position"], out position, out var positionError))
                        return Fail("BAD_BLEND_CHILD", positionError);
                    break;
                case BlendTreeType.Direct:
                    if (args["threshold"] != null || args["position"] != null)
                        return Fail("BAD_BLEND_CHILD", "Direct BlendTrees require directBlendParameter and do not accept threshold or position");
                    directBlendParameter = ((string)args["directBlendParameter"] ?? string.Empty).Trim();
                    if (!TryFindParameter(controller, directBlendParameter, AnimatorControllerParameterType.Float, out var directError))
                        return OpResult.Fail(directError);
                    break;
                default:
                    return Fail("BAD_BLEND_TYPE", tree.blendType.ToString());
            }

            using (var mutation = new MutationScope("Add Blend Tree Motion"))
            {
                mutation.Record(controller, "Add Blend Tree Motion");
                mutation.Record(tree, "Add Blend Tree Motion");
                if (tree.blendType == BlendTreeType.Simple1D)
                {
                    tree.useAutomaticThresholds = false;
                    tree.AddChild(clip, threshold);
                }
                else if (tree.blendType == BlendTreeType.Direct)
                {
                    tree.AddChild(clip, 0f);
                    var children = tree.children;
                    children[children.Length - 1].directBlendParameter = directBlendParameter;
                    tree.children = children;
                }
                else tree.AddChild(clip, position);
                mutation.Complete();

                var child = tree.children[tree.children.Length - 1];
                var result = OpResult.Success(controller.GetMcpInstanceId(), $"added '{clip.name}' to BlendTree '{stateName}'");
                result.Data = new JObject
                {
                    ["controller"] = AssetReference(controller),
                    ["layer"] = layerIndex,
                    ["state"] = stateName,
                    ["child"] = ChildMotionJson(child, tree.children.Length - 1),
                    ["childCount"] = tree.children.Length,
                };
                return result;
            }
        }

        public static OpResult AddAnimationEvent(JObject args)
        {
            var clip = ResolveClip(args, out var error, true);
            if (clip == null) return OpResult.Fail(error);
            var functionName = ((string)args["functionName"] ?? string.Empty).Trim();
            if (functionName.Length == 0) return Fail("BAD_ARGS", "functionName is required");
            if (!TryReadFloat(args["time"], 0f, 0f, clip.length, "time", out var time, out var timeError))
                return OpResult.Fail(timeError);
            if (!TryReadFloat(args["floatParameter"], 0f, null, null, "floatParameter", out var floatParameter, out var floatError))
                return OpResult.Fail(floatError);
            if (!TryReadInt(args["intParameter"], 0, int.MinValue, int.MaxValue, "intParameter", out var intParameter, out var intError))
                return OpResult.Fail(intError);

            var animationEvent = new AnimationEvent
            {
                time = time,
                functionName = functionName,
                stringParameter = (string)args["stringParameter"] ?? string.Empty,
                floatParameter = floatParameter,
                intParameter = intParameter,
                messageOptions = SendMessageOptions.DontRequireReceiver,
            };
            var events = AnimationUtility.GetAnimationEvents(clip).ToList();
            events.Add(animationEvent);
            events = events.OrderBy(item => item.time).ThenBy(item => item.functionName, StringComparer.Ordinal).ToList();

            using (var mutation = new MutationScope("Add Animation Event"))
            {
                mutation.Record(clip, "Add Animation Event");
                AnimationUtility.SetAnimationEvents(clip, events.ToArray());
                mutation.Complete();
                var result = OpResult.Success(clip.GetMcpInstanceId(), $"added event '{functionName}' at {time:F3}s to '{clip.name}'");
                result.Data = new JObject
                {
                    ["clip"] = AssetReference(clip),
                    ["event"] = AnimationEventJson(animationEvent),
                    ["eventCount"] = events.Count,
                    ["savedAssets"] = false,
                };
                return result;
            }
        }

        public static OpResult ReadAvatar(JObject args)
        {
            var avatarReference = ((string)args["guidOrPath"] ?? string.Empty).Trim();
            var avatar = LoadAsset<Avatar>(avatarReference);
            if (avatar == null) return Fail("AVATAR_NOT_FOUND", avatarReference);

            var data = AssetReference(avatar);
            data["isHuman"] = avatar.isHuman;
            data["isValid"] = avatar.isValid;
            var humanBones = new JArray();
            var skeleton = new JArray();
            if (avatar.isHuman)
            {
                var description = avatar.humanDescription;
                if (description.human != null)
                    foreach (var bone in description.human)
                        humanBones.Add(new JObject { ["humanName"] = bone.humanName, ["boneName"] = bone.boneName, ["limitUseDefaultValues"] = bone.limit.useDefaultValues });
                if (description.skeleton != null)
                    foreach (var transform in description.skeleton)
                        skeleton.Add(new JObject { ["name"] = transform.name, ["position"] = Vector3Json(transform.position), ["rotation"] = QuaternionJson(transform.rotation), ["scale"] = Vector3Json(transform.scale) });
                data["upperArmTwist"] = description.upperArmTwist;
                data["lowerArmTwist"] = description.lowerArmTwist;
                data["upperLegTwist"] = description.upperLegTwist;
                data["lowerLegTwist"] = description.lowerLegTwist;
            }
            data["humanBones"] = humanBones;
            data["skeleton"] = skeleton;

            var result = OpResult.Success(avatar.GetMcpInstanceId(),
                $"Avatar '{avatar.name}': valid={avatar.isValid}, human={avatar.isHuman}, bones={humanBones.Count}");
            result.Data = data;
            return result;
        }

        private static OpResult Fail(string code, string message) => OpResult.Fail($"{code}: {message}");

        private static AnimatorController ResolveController(JObject args, out string error, bool requireEditable = true)
        {
            error = null;
            var idToken = args["controllerInstanceId"];
            var reference = ((string)args["controllerGuidOrPath"] ?? string.Empty).Trim();
            var hasId = idToken != null;
            var hasReference = reference.Length > 0;
            if (hasId == hasReference)
            {
                error = "BAD_ARGS: exactly one of controllerInstanceId or controllerGuidOrPath is required";
                return null;
            }

            AnimatorController controller;
            if (hasId)
            {
                if (idToken.Type != JTokenType.Integer)
                {
                    error = "BAD_ARGS: controllerInstanceId must be an integer";
                    return null;
                }
                controller = UnityObjectIds.Resolve((int)idToken) as AnimatorController;
            }
            else controller = LoadAsset<AnimatorController>(reference);

            if (controller == null)
            {
                error = hasId
                    ? $"CONTROLLER_NOT_FOUND: instanceId={(int)idToken}"
                    : $"CONTROLLER_NOT_FOUND: {reference}";
                return null;
            }
            var path = AssetDatabase.GetAssetPath(controller);
            if (requireEditable && !PathIsUnderAssets(path))
            {
                error = $"CONTROLLER_NOT_EDITABLE: controller must be under Assets/: {path}";
                return null;
            }
            return controller;
        }

        private static AnimationClip ResolveClip(JObject args, out string error, bool requireStandalone)
        {
            error = null;
            var reference = ((string)args["clipGuidOrPath"] ?? (string)args["guidOrPath"] ?? string.Empty).Trim();
            var clip = LoadClip(reference);
            if (clip == null)
            {
                error = $"CLIP_NOT_FOUND: {reference}";
                return null;
            }
            if (requireStandalone && !IsStandaloneAnimationClip(clip, out var editableError))
            {
                error = $"CLIP_NOT_EDITABLE: {editableError}";
                return null;
            }
            return clip;
        }

        private static AnimationClip LoadClip(string reference) => LoadAsset<AnimationClip>(reference);

        private static RuntimeAnimatorController LoadRuntimeController(string reference) => LoadAsset<RuntimeAnimatorController>(reference);

        private static T LoadAsset<T>(string reference) where T : Object
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            var normalized = reference.Replace('\\', '/').Trim();
            var path = normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
                ? normalized
                : AssetDatabase.GUIDToAssetPath(normalized);
            if (string.IsNullOrEmpty(path)) return null;
            return AssetDatabase.LoadAssetAtPath<T>(path);
        }

        private static Animator ResolveAnimator(JObject args, out string error)
        {
            error = null;
            var id = (int?)args["instanceId"] ?? 0;
            var gameObject = UnityObjectIds.Resolve(id) as GameObject;
            if (gameObject == null)
            {
                error = $"TARGET_NOT_FOUND: GameObject not found: instanceId={id}";
                return null;
            }
            var animator = gameObject.GetComponent<Animator>();
            if (animator == null)
            {
                error = "ANIMATOR_NOT_FOUND: no Animator on GameObject";
                return null;
            }
            if (animator.runtimeAnimatorController == null)
            {
                error = "CONTROLLER_NOT_FOUND: Animator has no RuntimeAnimatorController";
                return null;
            }
            return animator;
        }

        private static bool TryResolveLayer(
            AnimatorController controller,
            JToken token,
            out int layerIndex,
            out AnimatorControllerLayer layer,
            out string error)
        {
            layerIndex = token == null ? 0 : token.Type == JTokenType.Integer ? (int)token : -1;
            layer = null;
            error = null;
            if (token != null && token.Type != JTokenType.Integer)
            {
                error = "BAD_ARGS: layer must be an integer";
                return false;
            }
            if (layerIndex < 0 || layerIndex >= controller.layers.Length)
            {
                error = $"LAYER_OUT_OF_RANGE: layer {layerIndex} is outside [0,{controller.layers.Length - 1}]";
                return false;
            }
            layer = controller.layers[layerIndex];
            if (layer.stateMachine == null)
            {
                error = $"STATE_MACHINE_MISSING: layer {layerIndex} has no state machine";
                return false;
            }
            return true;
        }

        private static AnimatorState FindState(AnimatorStateMachine stateMachine, string name)
        {
            if (stateMachine == null || string.IsNullOrEmpty(name)) return null;
            foreach (var child in stateMachine.states)
                if (child.state != null && string.Equals(child.state.name, name, StringComparison.Ordinal))
                    return child.state;
            return null;
        }

        private static bool TryParseConditions(
            AnimatorController controller,
            JArray tokens,
            out List<ConditionSpec> conditions,
            out string error)
        {
            conditions = new List<ConditionSpec>();
            error = null;
            if (tokens == null) return true;
            foreach (var token in tokens)
            {
                if (!(token is JObject value))
                {
                    error = "BAD_CONDITION: each condition must be an object";
                    return false;
                }
                var parameterName = ((string)value["parameter"] ?? string.Empty).Trim();
                var modeText = ((string)value["mode"] ?? string.Empty).Trim();
                if (parameterName.Length == 0 || modeText.Length == 0)
                {
                    error = "BAD_CONDITION: parameter and mode are required";
                    return false;
                }
                var parameter = controller.parameters.FirstOrDefault(candidate => candidate.name == parameterName);
                if (parameter == null)
                {
                    error = $"PARAMETER_NOT_FOUND: parameter '{parameterName}' is not on the controller";
                    return false;
                }
                if (!Enum.TryParse(modeText, true, out AnimatorConditionMode mode)
                    || !Enum.IsDefined(typeof(AnimatorConditionMode), mode))
                {
                    error = $"BAD_CONDITION_MODE: {modeText}";
                    return false;
                }
                if (!ConditionModeSupports(parameter.type, mode))
                {
                    error = $"CONDITION_TYPE_MISMATCH: {mode} is not valid for {parameter.type} parameter '{parameterName}'";
                    return false;
                }

                float threshold = 0f;
                if (mode != AnimatorConditionMode.If && mode != AnimatorConditionMode.IfNot)
                {
                    if (value["threshold"] == null)
                    {
                        error = $"BAD_CONDITION: threshold is required for {mode}";
                        return false;
                    }
                    if (!TryReadFloat(value["threshold"], 0f, null, null, "threshold", out threshold, out error))
                        return false;
                    if ((mode == AnimatorConditionMode.Equals || mode == AnimatorConditionMode.NotEqual)
                        && (value["threshold"].Type != JTokenType.Integer))
                    {
                        error = $"BAD_CONDITION: {mode} threshold must be an integer";
                        return false;
                    }
                }
                else if (value["threshold"] != null)
                {
                    error = $"BAD_CONDITION: {mode} does not use threshold";
                    return false;
                }

                conditions.Add(new ConditionSpec
                {
                    Parameter = parameterName,
                    Mode = mode,
                    Threshold = threshold,
                });
            }
            return true;
        }

        private static bool ConditionModeSupports(AnimatorControllerParameterType type, AnimatorConditionMode mode)
        {
            switch (type)
            {
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    return mode == AnimatorConditionMode.If || mode == AnimatorConditionMode.IfNot;
                case AnimatorControllerParameterType.Float:
                    return mode == AnimatorConditionMode.Greater || mode == AnimatorConditionMode.Less;
                case AnimatorControllerParameterType.Int:
                    return mode == AnimatorConditionMode.Greater || mode == AnimatorConditionMode.Less
                        || mode == AnimatorConditionMode.Equals || mode == AnimatorConditionMode.NotEqual;
                default:
                    return false;
            }
        }

        private static bool ValidateBlendParameters(
            AnimatorController controller,
            BlendTreeType blendType,
            string x,
            string y,
            out string error)
        {
            error = null;
            switch (blendType)
            {
                case BlendTreeType.Simple1D:
                    if (y.Length > 0)
                    {
                        error = "BAD_BLEND_PARAMETERS: Simple1D does not use blendParameterY";
                        return false;
                    }
                    return TryFindParameter(controller, x, AnimatorControllerParameterType.Float, out error);
                case BlendTreeType.SimpleDirectional2D:
                case BlendTreeType.FreeformDirectional2D:
                case BlendTreeType.FreeformCartesian2D:
                    if (!TryFindParameter(controller, x, AnimatorControllerParameterType.Float, out error)) return false;
                    if (!TryFindParameter(controller, y, AnimatorControllerParameterType.Float, out error)) return false;
                    if (x == y)
                    {
                        error = "BAD_BLEND_PARAMETERS: blendParameter and blendParameterY must be different";
                        return false;
                    }
                    return true;
                case BlendTreeType.Direct:
                    if (x.Length > 0 || y.Length > 0)
                    {
                        error = "BAD_BLEND_PARAMETERS: Direct BlendTrees use directBlendParameter on each child";
                        return false;
                    }
                    return true;
                default:
                    error = $"BAD_BLEND_TYPE: {blendType}";
                    return false;
            }
        }

        private static bool TryFindParameter(
            AnimatorController controller,
            string name,
            AnimatorControllerParameterType expectedType,
            out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "BAD_BLEND_PARAMETERS: parameter name is required";
                return false;
            }
            var parameter = controller.parameters.FirstOrDefault(candidate => candidate.name == name);
            if (parameter == null)
            {
                error = $"PARAMETER_NOT_FOUND: parameter '{name}' is not on the controller";
                return false;
            }
            if (parameter.type != expectedType)
            {
                error = $"PARAMETER_TYPE_MISMATCH: parameter '{name}' must be {expectedType}, not {parameter.type}";
                return false;
            }
            return true;
        }

        private static string ResolveRuntimeStateName(Animator animator, string requestedState, int requestedLayer, out int resolvedLayer)
        {
            resolvedLayer = requestedLayer;
            if (requestedLayer >= 0)
            {
                var directHash = Animator.StringToHash(requestedState);
                if (animator.HasState(requestedLayer, directHash)) return requestedState;
                var qualified = $"{animator.GetLayerName(requestedLayer)}.{requestedState}";
                if (animator.HasState(requestedLayer, Animator.StringToHash(qualified))) return qualified;
                return null;
            }

            for (var layer = 0; layer < animator.layerCount; layer++)
            {
                var directHash = Animator.StringToHash(requestedState);
                if (animator.HasState(layer, directHash))
                {
                    resolvedLayer = layer;
                    return requestedState;
                }
                var qualified = $"{animator.GetLayerName(layer)}.{requestedState}";
                if (animator.HasState(layer, Animator.StringToHash(qualified)))
                {
                    resolvedLayer = layer;
                    return qualified;
                }
            }
            return null;
        }

        private static bool TryReadPosition(JToken token, out Vector2 position, out string error)
        {
            position = Vector2.zero;
            error = null;
            if (!(token is JArray array) || array.Count != 2)
            {
                error = "position must contain exactly [x,y]";
                return false;
            }
            if (!TryReadFloat(array[0], 0f, null, null, "position[0]", out var x, out error)
                || !TryReadFloat(array[1], 0f, null, null, "position[1]", out var y, out error))
                return false;
            position = new Vector2(x, y);
            return true;
        }

        private static bool TryReadFloat(
            JToken token,
            float fallback,
            float? minimum,
            float? maximum,
            string name,
            out float value,
            out string error)
        {
            value = fallback;
            error = null;
            if (token == null) return true;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
            {
                error = $"BAD_ARGS: {name} must be a number";
                return false;
            }
            var number = (double)token;
            if (double.IsNaN(number) || double.IsInfinity(number) || number < -float.MaxValue || number > float.MaxValue)
            {
                error = $"BAD_ARGS: {name} must be finite";
                return false;
            }
            value = (float)number;
            if (minimum.HasValue && value < minimum.Value)
            {
                error = $"BAD_ARGS: {name} must be at least {minimum.Value}";
                return false;
            }
            if (maximum.HasValue && value > maximum.Value)
            {
                error = $"BAD_ARGS: {name} must be at most {maximum.Value}";
                return false;
            }
            return true;
        }

        private static bool TryReadInt(
            JToken token,
            int fallback,
            int minimum,
            int maximum,
            string name,
            out int value,
            out string error,
            bool required = false)
        {
            value = fallback;
            error = null;
            if (token == null)
            {
                if (required)
                {
                    error = $"BAD_ARGS: {name} is required";
                    return false;
                }
                return true;
            }
            if (token.Type != JTokenType.Integer)
            {
                error = $"BAD_ARGS: {name} must be an integer";
                return false;
            }
            try { value = (int)token; }
            catch
            {
                error = $"BAD_ARGS: {name} is outside Int32 range";
                return false;
            }
            if (value < minimum || value > maximum)
            {
                error = $"BAD_ARGS: {name} must be between {minimum} and {maximum}";
                return false;
            }
            return true;
        }

        private static bool IsStandaloneAnimationClip(AnimationClip clip, out string error)
        {
            error = null;
            if (clip == null)
            {
                error = "clip is null";
                return false;
            }
            var path = AssetDatabase.GetAssetPath(clip);
            if (!PathIsUnderAssets(path) || !path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)
                || AssetDatabase.LoadMainAssetAtPath(path) != clip)
            {
                error = "persistent event/curve writes require a standalone .anim main asset under Assets/";
                return false;
            }
            return true;
        }

        private static bool TryNormalizeAssetPath(string requestedPath, string extension, out string path, out string error)
        {
            path = (requestedPath ?? string.Empty).Replace('\\', '/').Trim();
            error = null;
            if (!PathIsUnderAssets(path) || path.Contains("../") || path.EndsWith("/..", StringComparison.Ordinal))
            {
                error = "path must stay under Assets/";
                return false;
            }
            if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                error = $"path must end with {extension}";
                return false;
            }
            return true;
        }

        private static bool PathIsUnderAssets(string path) =>
            !string.IsNullOrEmpty(path)
            && path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("../")
            && !path.EndsWith("/..", StringComparison.Ordinal);

        private static string NormalizeFolder(string folder)
        {
            if (folder == null) return null;
            var normalized = folder.Replace('\\', '/').Trim().TrimEnd('/');
            if (string.Equals(normalized, "Assets", StringComparison.OrdinalIgnoreCase)) return "Assets";
            return PathIsUnderAssets(normalized) ? normalized : null;
        }

        private static bool PathIsUnderFolder(string path, string folder) =>
            string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

        private static void EnsureFolderForAsset(string assetPath)
        {
            var folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            EnsureFolder(folder);
        }

        private static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || folder == "Assets" || AssetDatabase.IsValidFolder(folder)) return;
            var parent = Path.GetDirectoryName(folder)?.Replace('\\', '/') ?? "Assets";
            EnsureFolder(parent);
            var guid = AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
            if (string.IsNullOrEmpty(guid) && !AssetDatabase.IsValidFolder(folder))
                throw new InvalidOperationException($"could not create folder '{folder}'");
        }

        private static string NextLayerName(AnimatorController controller)
        {
            var index = controller.layers.Length;
            string name;
            do name = $"Layer {index++}";
            while (controller.layers.Any(layer => layer.name == name));
            return name;
        }

        private static JObject AssetReference(Object target)
        {
            if (target == null) return null;
            var path = AssetDatabase.GetAssetPath(target);
            return new JObject
            {
                ["name"] = target.name,
                ["type"] = target.GetType().FullName,
                ["instanceId"] = target.GetMcpInstanceId(),
                ["assetPath"] = string.IsNullOrEmpty(path) ? null : path,
                ["guid"] = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path),
                ["target"] = ToolTargetRef.Capture(target)?.ToJson(),
            };
        }

        private static JObject ParameterJson(AnimatorControllerParameter parameter)
        {
            var value = new JObject
            {
                ["name"] = parameter.name,
                ["type"] = parameter.type.ToString(),
                ["nameHash"] = parameter.nameHash,
            };
            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Float: value["defaultValue"] = parameter.defaultFloat; break;
                case AnimatorControllerParameterType.Int: value["defaultValue"] = parameter.defaultInt; break;
                case AnimatorControllerParameterType.Bool: value["defaultValue"] = parameter.defaultBool; break;
                default: value["defaultValue"] = JValue.CreateNull(); break;
            }
            return value;
        }

        private static JObject RuntimeParameterJson(Animator animator, AnimatorControllerParameter parameter)
        {
            var value = ParameterJson(parameter);
            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Float: value["value"] = animator.GetFloat(parameter.name); break;
                case AnimatorControllerParameterType.Int: value["value"] = animator.GetInteger(parameter.name); break;
                case AnimatorControllerParameterType.Bool: value["value"] = animator.GetBool(parameter.name); break;
                default: value["value"] = JValue.CreateNull(); break;
            }
            return value;
        }

        private static JObject StateJson(AnimatorState state)
        {
            return new JObject
            {
                ["name"] = state.name,
                ["instanceId"] = state.GetMcpInstanceId(),
                ["speed"] = state.speed,
                ["tag"] = state.tag,
                ["writeDefaultValues"] = state.writeDefaultValues,
                ["motion"] = MotionJson(state.motion),
                ["transitions"] = new JArray(state.transitions.Select(TransitionJson)),
            };
        }

        private static JObject MotionJson(Motion motion)
        {
            if (motion == null) return null;
            var value = AssetReference(motion);
            value["duration"] = motion.averageDuration;
            value["loop"] = motion.isLooping;
            if (motion is BlendTree tree)
            {
                value["blendType"] = tree.blendType.ToString();
                value["blendParameter"] = tree.blendParameter;
                value["blendParameterY"] = tree.blendParameterY;
                value["childCount"] = tree.children.Length;
            }
            return value;
        }

        private static JObject TransitionJson(AnimatorStateTransition transition)
        {
            if (transition == null) return null;
            return new JObject
            {
                ["instanceId"] = transition.GetMcpInstanceId(),
                ["to"] = transition.destinationState != null ? transition.destinationState.name : null,
                ["toStateMachine"] = transition.destinationStateMachine != null ? transition.destinationStateMachine.name : null,
                ["isExit"] = transition.isExit,
                ["hasExitTime"] = transition.hasExitTime,
                ["exitTime"] = transition.exitTime,
                ["duration"] = transition.duration,
                ["fixedDuration"] = transition.hasFixedDuration,
                ["offset"] = transition.offset,
                ["canTransitionToSelf"] = transition.canTransitionToSelf,
                ["conditions"] = new JArray(transition.conditions.Select(condition => new JObject
                {
                    ["parameter"] = condition.parameter,
                    ["mode"] = condition.mode.ToString(),
                    ["threshold"] = condition.threshold,
                })),
            };
        }

        private static JObject BlendTreeJson(BlendTree tree, string stateName)
        {
            var value = AssetReference(tree);
            value["state"] = stateName;
            value["blendType"] = tree.blendType.ToString();
            value["blendParameter"] = tree.blendParameter;
            value["blendParameterY"] = tree.blendParameterY;
            value["useAutomaticThresholds"] = tree.useAutomaticThresholds;
            value["children"] = new JArray(tree.children.Select(ChildMotionJson));
            return value;
        }

        private static JObject ChildMotionJson(ChildMotion child, int index = -1)
        {
            return new JObject
            {
                ["index"] = index,
                ["motion"] = MotionJson(child.motion),
                ["threshold"] = child.threshold,
                ["position"] = new JObject { ["x"] = child.position.x, ["y"] = child.position.y },
                ["directBlendParameter"] = child.directBlendParameter,
                ["timeScale"] = child.timeScale,
                ["cycleOffset"] = child.cycleOffset,
                ["mirror"] = child.mirror,
            };
        }

        private static JObject BindingJson(EditorCurveBinding binding, string kind) => new JObject
        {
            ["kind"] = kind,
            ["path"] = binding.path,
            ["componentType"] = binding.type != null ? binding.type.FullName : null,
            ["propertyName"] = binding.propertyName,
            ["isDiscreteCurve"] = binding.isDiscreteCurve,
            ["isSerializeReferenceCurve"] = binding.isSerializeReferenceCurve,
        };

        private static JObject AnimationEventJson(AnimationEvent animationEvent) => new JObject
        {
            ["time"] = animationEvent.time,
            ["functionName"] = animationEvent.functionName,
            ["stringParameter"] = animationEvent.stringParameter,
            ["floatParameter"] = animationEvent.floatParameter,
            ["intParameter"] = animationEvent.intParameter,
            ["objectReferenceParameter"] = AssetReference(animationEvent.objectReferenceParameter),
            ["messageOptions"] = animationEvent.messageOptions.ToString(),
        };

        private static JObject Vector3Json(Vector3 value) => new JObject
        {
            ["x"] = value.x,
            ["y"] = value.y,
            ["z"] = value.z,
        };

        private static JObject QuaternionJson(Quaternion value) => new JObject
        {
            ["x"] = value.x,
            ["y"] = value.y,
            ["z"] = value.z,
            ["w"] = value.w,
        };

        private sealed class ConditionSpec
        {
            public string Parameter;
            public AnimatorConditionMode Mode;
            public float Threshold;
        }
    }
}
