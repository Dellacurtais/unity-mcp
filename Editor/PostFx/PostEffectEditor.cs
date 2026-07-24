using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    /// Adds, removes, inspects, and edits VolumeComponent overrides on a VolumeProfile.
    public static class PostEffectEditor
    {
        private sealed class ParameterChange
        {
            public string Name;
            public VolumeParameter Parameter;
            public MemberInfo ValueMember;
            public object ConvertedValue;
            public bool OverrideState;
        }

        public static OpResult RemoveEffect(JObject args)
        {
            var (volume, resolveError) = ResolveVolume(args);
            if (resolveError != null) return OpResult.Fail(resolveError);
            var profile = volume.sharedProfile;
            if (profile == null) return OpResult.Fail("volume has no shared profile");

            if (!TryResolveEffectType((string)args["effect"], out var effectType, out var typeError))
                return OpResult.Fail(typeError);

            var existing = FindEffect(profile, effectType);
            if (existing == null) return OpResult.Fail($"effect {effectType.FullName} is not present on the volume profile");

            var serializedProfile = new SerializedObject(profile);
            serializedProfile.Update();
            var components = serializedProfile.FindProperty("components");
            var componentIndex = -1;
            for (var index = 0; index < components.arraySize; index++)
            {
                if (components.GetArrayElementAtIndex(index).objectReferenceValue != existing) continue;
                componentIndex = index;
                break;
            }
            if (componentIndex < 0) return OpResult.Fail($"effect {effectType.FullName} could not be located in the serialized profile");

            var componentProperty = components.GetArrayElementAtIndex(componentIndex);
            componentProperty.objectReferenceValue = null;
            components.DeleteArrayElementAtIndex(componentIndex);
            serializedProfile.ApplyModifiedProperties();
            Undo.DestroyObjectImmediate(existing);
            SaveProfile(profile);

            return new OpResult
            {
                Ok = true,
                InstanceID = volume.gameObject.GetInstanceID(),
                Summary = $"removed {effectType.Name}",
                Data = new JObject
                {
                    ["volumeInstanceId"] = volume.gameObject.GetInstanceID(),
                    ["effect"] = effectType.FullName,
                    ["profile"] = ProfileJson(profile),
                },
            };
        }

        public static OpResult ListEffects(JObject args)
        {
            var (volume, resolveError) = ResolveVolume(args);
            if (resolveError != null) return OpResult.Fail(resolveError);
            var profile = volume.sharedProfile;
            if (profile == null) return OpResult.Fail("volume has no shared profile");

            var effects = new JArray();
            var summary = new System.Text.StringBuilder();
            summary.Append($"volume #{volume.gameObject.GetInstanceID()} '{volume.name}' has {profile.components.Count} effect(s):");
            foreach (var component in profile.components.Where(item => item != null))
            {
                var activeOverrides = CountActiveOverrides(component);
                summary.Append($"\n  {component.GetType().Name} ({activeOverrides} active override(s))");
                effects.Add(EffectJson(component));
            }

            return new OpResult
            {
                Ok = true,
                InstanceID = volume.gameObject.GetInstanceID(),
                Summary = summary.ToString(),
                Data = new JObject
                {
                    ["volumeInstanceId"] = volume.gameObject.GetInstanceID(),
                    ["profile"] = ProfileJson(profile),
                    ["count"] = effects.Count,
                    ["effects"] = effects,
                },
            };
        }

        public static IEnumerable<string> ListAvailableEffectTypes()
        {
            return GetVolumeComponentTypes()
                .Where(IsSupportedByActivePipeline)
                .Select(type => type.FullName)
                .Where(name => !string.IsNullOrEmpty(name))
                .OrderBy(name => name, StringComparer.Ordinal);
        }

        public static OpResult DescribeEffectType(JObject args)
        {
            if (!TryResolveEffectType((string)args["effect"], out var effectType, out var typeError))
                return OpResult.Fail(typeError);

            VolumeComponent instance;
            try
            {
                instance = ScriptableObject.CreateInstance(effectType) as VolumeComponent;
            }
            catch (Exception exception)
            {
                return OpResult.Fail($"could not instantiate {effectType.FullName}: {exception.GetBaseException().Message}");
            }

            if (instance == null) return OpResult.Fail($"could not instantiate {effectType.FullName}");

            try
            {
                var parameters = new JArray();
                var summary = new System.Text.StringBuilder();
                summary.AppendLine(effectType.FullName);

                foreach (var field in effectType
                    .GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .Where(field => typeof(VolumeParameter).IsAssignableFrom(field.FieldType))
                    .OrderBy(field => field.Name, StringComparer.Ordinal))
                {
                    var parameter = field.GetValue(instance) as VolumeParameter;
                    if (parameter == null) continue;
                    var data = ParameterJson(field.Name, parameter);
                    parameters.Add(data);

                    summary.Append($"  {field.Name}: {field.FieldType.Name}");
                    if (data["valueType"] != null) summary.Append($" -> {(string)data["valueType"]}");
                    if (data["enumValues"] is JArray enumValues)
                        summary.Append($" enum [{string.Join(", ", enumValues.Values<string>())}]");
                    if (data["minimum"] != null || data["maximum"] != null)
                        summary.Append($" range [{data["minimum"] ?? "-inf"}, {data["maximum"] ?? "+inf"}]");
                    summary.AppendLine();
                }

                return new OpResult
                {
                    Ok = true,
                    Summary = summary.ToString(),
                    Data = new JObject
                    {
                        ["name"] = effectType.Name,
                        ["type"] = effectType.FullName,
                        ["pipeline"] = ActivePipelineName(),
                        ["parameterCount"] = parameters.Count,
                        ["parameters"] = parameters,
                    },
                };
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        public static OpResult AddEffect(JObject args)
        {
            var (volume, resolveError) = ResolveVolume(args);
            if (resolveError != null) return OpResult.Fail(resolveError);
            var profile = volume.sharedProfile;
            if (profile == null) return OpResult.Fail("volume has no shared profile");

            if (!TryResolveEffectType((string)args["effect"], out var effectType, out var typeError))
                return OpResult.Fail(typeError);

            var existing = FindEffect(profile, effectType);
            if (existing != null)
            {
                return new OpResult
                {
                    Ok = true,
                    InstanceID = volume.gameObject.GetInstanceID(),
                    Summary = $"effect {effectType.Name} already present on volume",
                    Data = new JObject
                    {
                        ["volumeInstanceId"] = volume.gameObject.GetInstanceID(),
                        ["created"] = false,
                        ["effect"] = EffectJson(existing),
                        ["profile"] = ProfileJson(profile),
                    },
                };
            }

            var overrideAll = (bool?)args["overrideAll"] ?? false;
            Undo.RecordObject(profile, "Add Post Effect");
            VolumeComponent created;
            try
            {
                created = profile.Add(effectType, overrideAll);
                Undo.RegisterCreatedObjectUndo(created, "Add Post Effect");
                if (EditorUtility.IsPersistent(profile)) AssetDatabase.AddObjectToAsset(created, profile);
            }
            catch (Exception exception)
            {
                return OpResult.Fail($"could not add {effectType.FullName}: {exception.GetBaseException().Message}");
            }

            SaveProfile(profile, created);
            return new OpResult
            {
                Ok = true,
                InstanceID = volume.gameObject.GetInstanceID(),
                Summary = $"added {effectType.Name} to volume profile",
                Data = new JObject
                {
                    ["volumeInstanceId"] = volume.gameObject.GetInstanceID(),
                    ["created"] = true,
                    ["overrideAll"] = overrideAll,
                    ["effect"] = EffectJson(created),
                    ["profile"] = ProfileJson(profile),
                },
            };
        }

        public static OpResult SetEffectProperty(JObject args)
        {
            var (volume, resolveError) = ResolveVolume(args);
            if (resolveError != null) return OpResult.Fail(resolveError);
            var profile = volume.sharedProfile;
            if (profile == null) return OpResult.Fail("volume has no shared profile");

            if (!TryResolveEffectType((string)args["effect"], out var effectType, out var typeError))
                return OpResult.Fail(typeError);

            var effect = FindEffect(profile, effectType);
            if (effect == null)
                return OpResult.Fail($"effect {effectType.FullName} is not present on the volume profile; call add_post_effect first");

            var propertyName = (string)args["property"];
            if (string.IsNullOrWhiteSpace(propertyName)) return OpResult.Fail("property name is required");
            if (!args.TryGetValue("value", out var value)) return OpResult.Fail("value is required");
            var overrideState = (bool?)args["overrideState"] ?? true;

            if (!TryPrepareParameterChange(effect, propertyName, value, overrideState, out var change, out var changeError))
                return OpResult.Fail(changeError);

            Undo.RecordObject(effect, "Set Post Effect Property");
            ApplyParameterChange(change);
            SaveProfile(profile, effect);

            return new OpResult
            {
                Ok = true,
                InstanceID = volume.gameObject.GetInstanceID(),
                Summary = $"set {effectType.Name}.{propertyName}",
                Data = new JObject
                {
                    ["volumeInstanceId"] = volume.gameObject.GetInstanceID(),
                    ["effect"] = effectType.FullName,
                    ["property"] = propertyName,
                    ["value"] = ReadParameterValue(change),
                    ["overrideState"] = change.Parameter.overrideState,
                    ["profile"] = ProfileJson(profile),
                },
            };
        }

        public static OpResult ApplyColorGrading(JObject args)
        {
            var requested = new List<(string argument, string parameter, JToken value)>();
            AddRequested("exposure", "postExposure");
            AddRequested("contrast", "contrast");
            AddRequested("saturation", "saturation");
            AddRequested("hueShift", "hueShift");
            AddRequested("colorFilter", "colorFilter");

            if (requested.Count == 0)
                return OpResult.Fail("at least one color grading value is required");

            var (volume, resolveError) = ResolveVolume(args);
            if (resolveError != null) return OpResult.Fail(resolveError);
            var profile = volume.sharedProfile;
            if (profile == null) return OpResult.Fail("volume has no shared profile");

            if (!TryResolveEffectType("ColorAdjustments", out var colorAdjustmentsType, out var typeError))
                return OpResult.Fail($"ColorAdjustments is not available for the active pipeline: {typeError}");

            var colorAdjustments = FindEffect(profile, colorAdjustmentsType);
            VolumeComponent validationTarget = colorAdjustments;
            var destroyValidationTarget = false;
            if (validationTarget == null)
            {
                validationTarget = ScriptableObject.CreateInstance(colorAdjustmentsType) as VolumeComponent;
                destroyValidationTarget = true;
            }

            if (validationTarget == null) return OpResult.Fail($"could not instantiate {colorAdjustmentsType.FullName}");
            try
            {
                foreach (var item in requested)
                {
                    if (!TryPrepareParameterChange(validationTarget, item.parameter, item.value, true, out _, out var validationError))
                        return OpResult.Fail($"invalid {item.argument}: {validationError}");
                }
            }
            finally
            {
                if (destroyValidationTarget) UnityEngine.Object.DestroyImmediate(validationTarget);
            }

            var created = false;
            if (colorAdjustments == null)
            {
                Undo.RecordObject(profile, "Apply Color Grading");
                colorAdjustments = profile.Add(colorAdjustmentsType, false);
                Undo.RegisterCreatedObjectUndo(colorAdjustments, "Apply Color Grading");
                if (EditorUtility.IsPersistent(profile)) AssetDatabase.AddObjectToAsset(colorAdjustments, profile);
                created = true;
            }

            Undo.RecordObject(colorAdjustments, "Apply Color Grading");
            var changes = new JArray();
            foreach (var item in requested)
            {
                if (!TryPrepareParameterChange(colorAdjustments, item.parameter, item.value, true, out var change, out var changeError))
                    return OpResult.Fail(changeError);
                ApplyParameterChange(change);
                changes.Add(new JObject
                {
                    ["argument"] = item.argument,
                    ["property"] = item.parameter,
                    ["value"] = ReadParameterValue(change),
                    ["overrideState"] = change.Parameter.overrideState,
                });
            }

            SaveProfile(profile, colorAdjustments);
            return new OpResult
            {
                Ok = true,
                InstanceID = volume.gameObject.GetInstanceID(),
                Summary = $"applied color grading ({string.Join(", ", requested.Select(item => item.parameter))})",
                Data = new JObject
                {
                    ["volumeInstanceId"] = volume.gameObject.GetInstanceID(),
                    ["effect"] = colorAdjustmentsType.FullName,
                    ["created"] = created,
                    ["changes"] = changes,
                    ["profile"] = ProfileJson(profile),
                },
            };

            void AddRequested(string argument, string parameter)
            {
                if (!args.TryGetValue(argument, out var token)) return;
                if (token == null || token.Type == JTokenType.Null)
                    requested.Add((argument, parameter, JValue.CreateNull()));
                else
                    requested.Add((argument, parameter, token));
            }
        }

        private static (Volume volume, string error) ResolveVolume(JObject args)
        {
            var id = (int?)args["volumeInstanceId"] ?? (int?)args["instanceId"] ?? 0;
            if (id == 0) return (null, "volumeInstanceId is required");
            var target = EditorUtility.InstanceIDToObject(id);
            var volume = target as Volume;
            if (volume == null && target is GameObject gameObject) volume = gameObject.GetComponent<Volume>();
            if (volume == null) return (null, $"Volume not found: instanceId={id}");
            return (volume, null);
        }

        private static bool TryResolveEffectType(string requestedName, out Type effectType, out string error)
        {
            effectType = null;
            error = null;
            if (string.IsNullOrWhiteSpace(requestedName))
            {
                error = "effect type name is required";
                return false;
            }

            requestedName = requestedName.Trim();
            var allTypes = GetVolumeComponentTypes().ToArray();
            var exact = allTypes.Where(type =>
                    string.Equals(type.FullName, requestedName, StringComparison.Ordinal) ||
                    string.Equals(type.AssemblyQualifiedName, requestedName, StringComparison.Ordinal))
                .ToArray();
            if (exact.Length == 1)
            {
                if (!IsSupportedByActivePipeline(exact[0]))
                {
                    error = $"effect type '{requestedName}' is not supported by active pipeline {ActivePipelineName()}";
                    return false;
                }
                effectType = exact[0];
                return true;
            }

            var matches = allTypes
                .Where(type => string.Equals(type.Name, requestedName, StringComparison.Ordinal))
                .ToArray();
            var supported = matches.Where(IsSupportedByActivePipeline).ToArray();
            if (supported.Length > 0) matches = supported;

            if (matches.Length == 0)
            {
                error = $"unknown effect type '{requestedName}' for active pipeline {ActivePipelineName()}";
                return false;
            }
            if (matches.Length > 1)
            {
                error = $"ambiguous effect type '{requestedName}'; use a fully-qualified name: " +
                    string.Join(", ", matches.Select(type => type.FullName).OrderBy(name => name, StringComparer.Ordinal));
                return false;
            }

            effectType = matches[0];
            return true;
        }

        private static IEnumerable<Type> GetVolumeComponentTypes()
        {
            return TypeCache.GetTypesDerivedFrom<VolumeComponent>()
                .Where(type => type != null && !type.IsAbstract && !type.ContainsGenericParameters)
                .Distinct();
        }

        private static bool IsSupportedByActivePipeline(Type type)
        {
            var pipelineAssetType = GraphicsSettings.currentRenderPipelineAssetType;
            if (pipelineAssetType == null) return true;
            var attribute = type.GetCustomAttribute<SupportedOnRenderPipelineAttribute>();
            if (attribute == null) return true;
            return !string.Equals(attribute.GetSupportedMode(pipelineAssetType).ToString(), "Unsupported", StringComparison.Ordinal);
        }

        private static string ActivePipelineName()
        {
            return GraphicsSettings.currentRenderPipelineAssetType?.FullName ?? "none";
        }

        private static VolumeComponent FindEffect(VolumeProfile profile, Type type)
        {
            return profile.components.Find(component => component != null && component.GetType() == type);
        }

        private static int CountActiveOverrides(VolumeComponent effect)
        {
            return effect.parameters.Count(parameter => parameter != null && parameter.overrideState);
        }

        private static bool TryPrepareParameterChange(
            VolumeComponent effect,
            string propertyName,
            JToken value,
            bool overrideState,
            out ParameterChange change,
            out string error)
        {
            change = null;
            error = null;
            var field = effect.GetType().GetField(propertyName, BindingFlags.Public | BindingFlags.Instance);
            var property = effect.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            object rawParameter = field != null ? field.GetValue(effect) : property?.GetValue(effect);
            if (!(rawParameter is VolumeParameter parameter))
            {
                error = $"effect {effect.GetType().Name} has no public VolumeParameter named '{propertyName}'";
                return false;
            }

            var parameterType = parameter.GetType();
            var valueField = parameterType.GetField("value", BindingFlags.Public | BindingFlags.Instance);
            var valueProperty = parameterType.GetProperty("value", BindingFlags.Public | BindingFlags.Instance);
            MemberInfo valueMember = valueField ?? (MemberInfo)valueProperty;
            if (valueMember == null || (valueProperty != null && !valueProperty.CanWrite))
            {
                error = $"parameter '{propertyName}' has no writable value";
                return false;
            }

            var valueType = valueField != null ? valueField.FieldType : valueProperty.PropertyType;
            object converted;
            try
            {
                converted = ConvertToType(value, valueType);
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }

            change = new ParameterChange
            {
                Name = propertyName,
                Parameter = parameter,
                ValueMember = valueMember,
                ConvertedValue = converted,
                OverrideState = overrideState,
            };
            return true;
        }

        private static void ApplyParameterChange(ParameterChange change)
        {
            if (change.ValueMember is FieldInfo field) field.SetValue(change.Parameter, change.ConvertedValue);
            else ((PropertyInfo)change.ValueMember).SetValue(change.Parameter, change.ConvertedValue);
            change.Parameter.overrideState = change.OverrideState;
        }

        private static JToken ReadParameterValue(ParameterChange change)
        {
            var value = change.ValueMember is FieldInfo field
                ? field.GetValue(change.Parameter)
                : ((PropertyInfo)change.ValueMember).GetValue(change.Parameter);
            return ValueJson(value);
        }

        private static object ConvertToType(JToken value, Type target)
        {
            if (value == null || value.Type == JTokenType.Null)
            {
                if (!target.IsValueType || typeof(UnityEngine.Object).IsAssignableFrom(target)) return null;
                throw new FormatException($"null is not valid for {target.Name}");
            }

            if (target == typeof(float)) return ReadFloat(value, target.Name);
            if (target == typeof(double)) return ReadDouble(value, target.Name);
            if (target == typeof(int)) return ReadInt(value, target.Name);
            if (target == typeof(uint)) return checked((uint)ReadLong(value, target.Name));
            if (target == typeof(long)) return ReadLong(value, target.Name);
            if (target == typeof(bool))
            {
                if (value.Type != JTokenType.Boolean) throw new FormatException("expected a boolean value");
                return (bool)value;
            }
            if (target == typeof(string))
            {
                if (value.Type != JTokenType.String) throw new FormatException("expected a string value");
                return (string)value;
            }
            if (target == typeof(Color)) return ReadColor(value);
            if (target == typeof(Vector2)) return ReadVector2(value);
            if (target == typeof(Vector3)) return ReadVector3(value);
            if (target == typeof(Vector4)) return ReadVector4(value);
            if (target == typeof(Vector2Int))
            {
                var vector = ReadVector2(value);
                return new Vector2Int(checked((int)vector.x), checked((int)vector.y));
            }
            if (target == typeof(Vector3Int))
            {
                var vector = ReadVector3(value);
                return new Vector3Int(checked((int)vector.x), checked((int)vector.y), checked((int)vector.z));
            }
            if (target == typeof(Quaternion)) return ReadQuaternion(value);
            if (target == typeof(LayerMask)) return new LayerMask { value = ReadInt(value, target.Name) };
            if (target.IsEnum) return ReadEnum(value, target);
            if (typeof(UnityEngine.Object).IsAssignableFrom(target)) return ReadUnityObject(value, target);
            throw new NotSupportedException($"can't convert JSON value to {target.Name}");
        }

        private static float ReadFloat(JToken token, string label)
        {
            var number = ReadDouble(token, label);
            if (number < -float.MaxValue || number > float.MaxValue) throw new OverflowException($"{label} is outside the float range");
            return (float)number;
        }

        private static double ReadDouble(JToken token, string label)
        {
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                throw new FormatException($"expected a number for {label}");
            var number = (double)token;
            if (double.IsNaN(number) || double.IsInfinity(number)) throw new FormatException($"{label} must be finite");
            return number;
        }

        private static int ReadInt(JToken token, string label)
        {
            var number = ReadLong(token, label);
            return checked((int)number);
        }

        private static long ReadLong(JToken token, string label)
        {
            if (token.Type != JTokenType.Integer) throw new FormatException($"expected an integer for {label}");
            return checked((long)token);
        }

        private static Color ReadColor(JToken token)
        {
            if (token.Type == JTokenType.String)
            {
                if (ColorUtility.TryParseHtmlString((string)token, out var color)) return color;
                throw new FormatException("expected an HTML color such as '#RRGGBB' or '#RRGGBBAA'");
            }
            if (token is JArray array && (array.Count == 3 || array.Count == 4))
            {
                return new Color(
                    ReadFloat(array[0], "color.r"),
                    ReadFloat(array[1], "color.g"),
                    ReadFloat(array[2], "color.b"),
                    array.Count == 4 ? ReadFloat(array[3], "color.a") : 1f);
            }
            if (token is JObject obj && obj["r"] != null && obj["g"] != null && obj["b"] != null)
            {
                return new Color(
                    ReadFloat(obj["r"], "color.r"),
                    ReadFloat(obj["g"], "color.g"),
                    ReadFloat(obj["b"], "color.b"),
                    obj["a"] != null ? ReadFloat(obj["a"], "color.a") : 1f);
            }
            throw new FormatException("expected color as '#RRGGBB', [r,g,b,a], or {r,g,b,a}");
        }

        private static Vector2 ReadVector2(JToken token)
        {
            if (token is JArray array && array.Count == 2)
                return new Vector2(ReadFloat(array[0], "x"), ReadFloat(array[1], "y"));
            if (token is JObject obj && obj["x"] != null && obj["y"] != null)
                return new Vector2(ReadFloat(obj["x"], "x"), ReadFloat(obj["y"], "y"));
            throw new FormatException("expected Vector2 as [x,y] or {x,y}");
        }

        private static Vector3 ReadVector3(JToken token)
        {
            if (token is JArray array && array.Count == 3)
                return new Vector3(ReadFloat(array[0], "x"), ReadFloat(array[1], "y"), ReadFloat(array[2], "z"));
            if (token is JObject obj && obj["x"] != null && obj["y"] != null && obj["z"] != null)
                return new Vector3(ReadFloat(obj["x"], "x"), ReadFloat(obj["y"], "y"), ReadFloat(obj["z"], "z"));
            throw new FormatException("expected Vector3 as [x,y,z] or {x,y,z}");
        }

        private static Vector4 ReadVector4(JToken token)
        {
            if (token is JArray array && array.Count == 4)
                return new Vector4(ReadFloat(array[0], "x"), ReadFloat(array[1], "y"), ReadFloat(array[2], "z"), ReadFloat(array[3], "w"));
            if (token is JObject obj && obj["x"] != null && obj["y"] != null && obj["z"] != null && obj["w"] != null)
                return new Vector4(ReadFloat(obj["x"], "x"), ReadFloat(obj["y"], "y"), ReadFloat(obj["z"], "z"), ReadFloat(obj["w"], "w"));
            throw new FormatException("expected Vector4 as [x,y,z,w] or {x,y,z,w}");
        }

        private static Quaternion ReadQuaternion(JToken token)
        {
            var vector = ReadVector4(token);
            return new Quaternion(vector.x, vector.y, vector.z, vector.w);
        }

        private static object ReadEnum(JToken token, Type enumType)
        {
            object parsed;
            if (token.Type == JTokenType.Integer)
            {
                parsed = Enum.ToObject(enumType, ReadInt(token, enumType.Name));
            }
            else if (token.Type == JTokenType.String && Enum.TryParse(enumType, (string)token, true, out parsed))
            {
            }
            else
            {
                throw new FormatException($"expected one of [{string.Join(", ", Enum.GetNames(enumType))}] for {enumType.Name}");
            }

            if (!enumType.IsDefined(typeof(FlagsAttribute), false) && !Enum.IsDefined(enumType, parsed))
                throw new FormatException($"invalid {enumType.Name} value '{token}'");
            return parsed;
        }

        private static UnityEngine.Object ReadUnityObject(JToken token, Type target)
        {
            if (token.Type == JTokenType.Integer)
            {
                var instance = EditorUtility.InstanceIDToObject((int)token);
                if (instance == null || !target.IsInstanceOfType(instance))
                    throw new FormatException($"instanceId {(int)token} is not a {target.Name}");
                return instance;
            }

            string requested = null;
            if (token.Type == JTokenType.String) requested = (string)token;
            else if (token is JObject obj)
            {
                if (obj["instanceId"] != null) return ReadUnityObject(obj["instanceId"], target);
                requested = (string)obj["guidOrPath"] ?? (string)obj["path"] ?? (string)obj["guid"];
            }

            if (string.IsNullOrWhiteSpace(requested)) throw new FormatException($"expected an asset path, GUID, or instanceId for {target.Name}");
            var path = requested.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? requested.Replace('\\', '/')
                : AssetDatabase.GUIDToAssetPath(requested);
            var asset = AssetDatabase.LoadAssetAtPath(path, target);
            if (asset == null) throw new FormatException($"{target.Name} asset not found: {requested}");
            return asset;
        }

        private static JObject ParameterJson(string name, VolumeParameter parameter)
        {
            var parameterType = parameter.GetType();
            var valueField = parameterType.GetField("value", BindingFlags.Public | BindingFlags.Instance);
            var valueProperty = parameterType.GetProperty("value", BindingFlags.Public | BindingFlags.Instance);
            var valueType = valueField != null ? valueField.FieldType : valueProperty?.PropertyType;
            var value = valueField != null ? valueField.GetValue(parameter) : valueProperty?.GetValue(parameter);
            var result = new JObject
            {
                ["name"] = name,
                ["parameterType"] = parameterType.FullName,
                ["valueType"] = valueType?.Name,
                ["defaultValue"] = ValueJson(value),
                ["overrideState"] = parameter.overrideState,
                ["noInterp"] = parameterType.Name.StartsWith("NoInterp", StringComparison.Ordinal),
            };
            if (valueType != null && valueType.IsEnum)
                result["enumValues"] = new JArray(Enum.GetNames(valueType));
            if (TryReadBound(parameter, "min", out var minimum)) result["minimum"] = minimum;
            if (TryReadBound(parameter, "max", out var maximum)) result["maximum"] = maximum;
            return result;
        }

        private static bool TryReadBound(VolumeParameter parameter, string name, out JToken value)
        {
            value = null;
            var type = parameter.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            var raw = field != null ? field.GetValue(parameter) : property?.GetValue(parameter);
            if (raw == null) return false;
            if (!(raw is byte || raw is sbyte || raw is short || raw is ushort || raw is int || raw is uint || raw is long || raw is ulong || raw is float || raw is double || raw is decimal))
                return false;
            value = JToken.FromObject(raw);
            return true;
        }

        private static JObject EffectJson(VolumeComponent component)
        {
            var result = new JObject
            {
                ["name"] = component.GetType().Name,
                ["type"] = component.GetType().FullName,
                ["active"] = component.active,
                ["parameterCount"] = component.parameters.Count,
                ["activeOverrides"] = CountActiveOverrides(component),
            };
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(component, out var guid, out long localFileId))
            {
                result["guid"] = guid;
                result["localFileId"] = localFileId;
            }
            return result;
        }

        private static JObject ProfileJson(VolumeProfile profile)
        {
            var path = AssetDatabase.GetAssetPath(profile);
            return new JObject
            {
                ["name"] = profile.name,
                ["path"] = string.IsNullOrEmpty(path) ? null : path,
                ["guid"] = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path),
            };
        }

        private static JToken ValueJson(object value)
        {
            if (value == null) return JValue.CreateNull();
            if (value is Color color)
                return new JObject { ["r"] = color.r, ["g"] = color.g, ["b"] = color.b, ["a"] = color.a };
            if (value is Vector2 vector2)
                return new JObject { ["x"] = vector2.x, ["y"] = vector2.y };
            if (value is Vector3 vector3)
                return new JObject { ["x"] = vector3.x, ["y"] = vector3.y, ["z"] = vector3.z };
            if (value is Vector4 vector4)
                return new JObject { ["x"] = vector4.x, ["y"] = vector4.y, ["z"] = vector4.z, ["w"] = vector4.w };
            if (value is Vector2Int vector2Int)
                return new JObject { ["x"] = vector2Int.x, ["y"] = vector2Int.y };
            if (value is Vector3Int vector3Int)
                return new JObject { ["x"] = vector3Int.x, ["y"] = vector3Int.y, ["z"] = vector3Int.z };
            if (value is Quaternion quaternion)
                return new JObject { ["x"] = quaternion.x, ["y"] = quaternion.y, ["z"] = quaternion.z, ["w"] = quaternion.w };
            if (value is LayerMask layerMask) return layerMask.value;
            if (value is Enum enumValue) return enumValue.ToString();
            if (value is UnityEngine.Object unityObject)
            {
                var path = AssetDatabase.GetAssetPath(unityObject);
                return new JObject
                {
                    ["instanceId"] = unityObject.GetInstanceID(),
                    ["name"] = unityObject.name,
                    ["path"] = string.IsNullOrEmpty(path) ? null : path,
                    ["guid"] = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path),
                };
            }
            if (value is string || value is bool || value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong || value is float || value is double || value is decimal)
                return JToken.FromObject(value);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static void SaveProfile(VolumeProfile profile, VolumeComponent component = null)
        {
            if (component != null) EditorUtility.SetDirty(component);
            EditorUtility.SetDirty(profile);
            if (EditorUtility.IsPersistent(profile)) AssetDatabase.SaveAssets();
        }
    }
}
