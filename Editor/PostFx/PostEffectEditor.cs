using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    /// Adds, removes and tweaks VolumeComponent post-processing effects (Bloom,
    /// Tonemapping, ColorAdjustments, Vignette, …) on a Volume's profile.
    /// Resolves effect type names across pipeline namespaces (URP/HDRP/SRP Core).
    public static class PostEffectEditor
    {
        public static OpResult RemoveEffect(JObject args)
        {
            var (volume, err) = ResolveVolume(args);
            if (err != null) return OpResult.Fail(err);
            var profile = volume.sharedProfile;
            if (profile == null) return OpResult.Fail("volume has no profile");

            var typeName = (string)args["effect"];
            if (string.IsNullOrEmpty(typeName)) return OpResult.Fail("effect type name is required");
            var effectType = ResolveEffectType(typeName);
            if (effectType == null) return OpResult.Fail($"unknown effect type '{typeName}'");

            var existing = profile.components.Find(c => c != null && c.GetType() == effectType);
            if (existing == null) return OpResult.Fail($"effect {effectType.Name} not present on volume");

            profile.Remove(effectType);
            EditorUtility.SetDirty(profile);
            return OpResult.Success(volume.GetInstanceID(), $"removed {effectType.Name}");
        }

        public static OpResult ListEffects(JObject args)
        {
            var (volume, err) = ResolveVolume(args);
            if (err != null) return OpResult.Fail(err);
            var profile = volume.sharedProfile;
            if (profile == null) return OpResult.Fail("volume has no profile");

            var sb = new System.Text.StringBuilder();
            sb.Append($"volume #{volume.GetInstanceID()} '{volume.name}' has {profile.components.Count} effect(s):");
            foreach (var c in profile.components)
            {
                if (c == null) continue;
                int overrides = CountActiveOverrides(c);
                sb.Append($"\n  {c.GetType().Name}  ({overrides} active override(s))");
            }
            return OpResult.Success(volume.GetInstanceID(), sb.ToString());
        }

        public static IEnumerable<string> ListAvailableEffectTypes()
        {
            var seen = new HashSet<string>();
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    if (t == null || t.IsAbstract) continue;
                    if (!typeof(VolumeComponent).IsAssignableFrom(t)) continue;
                    if (seen.Add(t.FullName)) yield return t.FullName;
                }
            }
        }

        public static OpResult DescribeEffectType(JObject args)
        {
            var typeName = (string)args["effect"];
            if (string.IsNullOrEmpty(typeName)) return OpResult.Fail("effect type name is required");
            var effectType = ResolveEffectType(typeName);
            if (effectType == null) return OpResult.Fail($"unknown effect type '{typeName}'");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{effectType.FullName}");
            foreach (var f in effectType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!typeof(VolumeParameter).IsAssignableFrom(f.FieldType)) continue;

                Type valueType = null;
                var pt = f.FieldType;
                while (pt != null && pt != typeof(object))
                {
                    if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(VolumeParameter<>))
                    { valueType = pt.GetGenericArguments()[0]; break; }
                    pt = pt.BaseType;
                }

                var line = $"  {f.Name}: {f.FieldType.Name}" +
                    (valueType != null ? $" → {valueType.Name}" : "");

                // Crucial: enumerate enum values so the agent doesn't chuck "Thin"
                // when the actual values are "Thin1"/"Thin2"/etc. Same for ranges.
                if (valueType != null && valueType.IsEnum)
                {
                    var names = Enum.GetNames(valueType);
                    line += "  enum [" + string.Join(", ", names) + "]";
                }
                else if (valueType == typeof(bool))
                {
                    line += "  values [true, false]";
                }

                // Highlight clamped parameters (MinFloatParameter, ClampedFloatParameter, etc.)
                var paramTypeName = f.FieldType.Name;
                if (paramTypeName.Contains("Clamped") || paramTypeName.Contains("Min") || paramTypeName.Contains("NoInterp"))
                    line += $"  ({paramTypeName} — check pipeline docs for valid range)";

                sb.AppendLine(line);
            }
            return OpResult.Success(0, sb.ToString());
        }

        private static int CountActiveOverrides(VolumeComponent effect)
        {
            int count = 0;
            foreach (var f in effect.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!typeof(VolumeParameter).IsAssignableFrom(f.FieldType)) continue;
                var p = f.GetValue(effect);
                if (p == null) continue;
                var ofMember = (MemberInfo)p.GetType().GetField("overrideState") ?? p.GetType().GetProperty("overrideState");
                if (ofMember == null) continue;
                bool active = ofMember is FieldInfo fi
                    ? (bool)fi.GetValue(p)
                    : (bool)((PropertyInfo)ofMember).GetValue(p);
                if (active) count++;
            }
            return count;
        }

        public static OpResult AddEffect(JObject args)
        {
            var (volume, err) = ResolveVolume(args);
            if (err != null) return OpResult.Fail(err);
            var profile = volume.sharedProfile;
            if (profile == null) return OpResult.Fail("volume has no profile");

            var typeName = (string)args["effect"];
            if (string.IsNullOrEmpty(typeName)) return OpResult.Fail("effect type name is required");
            var effectType = ResolveEffectType(typeName);
            if (effectType == null) return OpResult.Fail($"unknown effect type '{typeName}'");

            // Add<T>() requires a generic call; use reflection over the instance method.
            var addMethod = typeof(VolumeProfile)
                .GetMethod(nameof(VolumeProfile.Add), new[] { typeof(bool) })
                ?.MakeGenericMethod(effectType);
            if (addMethod == null) return OpResult.Fail("Volume API mismatch: Add<T>(bool) not found");

            // Don't duplicate: if the profile already has the component, return it.
            var existing = profile.components.Find(c => c != null && c.GetType() == effectType);
            if (existing == null)
                addMethod.Invoke(profile, new object[] { true });

            EditorUtility.SetDirty(profile);
            return OpResult.Success(volume.GetInstanceID(),
                existing != null
                    ? $"effect {effectType.Name} already present on volume"
                    : $"added {effectType.Name} to volume profile");
        }

        public static OpResult SetEffectProperty(JObject args)
        {
            var (volume, err) = ResolveVolume(args);
            if (err != null) return OpResult.Fail(err);
            var profile = volume.sharedProfile;
            if (profile == null) return OpResult.Fail("volume has no profile");

            var typeName = (string)args["effect"];
            if (string.IsNullOrEmpty(typeName)) return OpResult.Fail("effect type name is required");
            var effectType = ResolveEffectType(typeName);
            if (effectType == null) return OpResult.Fail($"unknown effect type '{typeName}'");

            var effect = profile.components.Find(c => c != null && c.GetType() == effectType);
            if (effect == null) return OpResult.Fail($"effect {effectType.Name} not present on volume — call add_post_effect first");

            var prop = (string)args["property"];
            var value = args["value"];
            if (string.IsNullOrEmpty(prop)) return OpResult.Fail("property name is required");
            if (!TrySetParameter(effect, prop, value, out var setErr))
                return OpResult.Fail(setErr);

            EditorUtility.SetDirty(profile);
            return OpResult.Success(volume.GetInstanceID(), $"set {effectType.Name}.{prop}");
        }

        public static OpResult ApplyColorGrading(JObject args)
        {
            var (volume, err) = ResolveVolume(args);
            if (err != null) return OpResult.Fail(err);
            var profile = volume.sharedProfile;
            if (profile == null) return OpResult.Fail("volume has no profile");

            var caType = ResolveEffectType("ColorAdjustments");
            if (caType == null) return OpResult.Fail("ColorAdjustments not available — install URP or HDRP package");

            var ca = profile.components.Find(c => c != null && c.GetType() == caType);
            if (ca == null)
            {
                var addMethod = typeof(VolumeProfile)
                    .GetMethod(nameof(VolumeProfile.Add), new[] { typeof(bool) })
                    ?.MakeGenericMethod(caType);
                ca = (VolumeComponent)addMethod.Invoke(profile, new object[] { true });
            }

            var changes = new List<string>();
            void Set(string field, JToken v)
            {
                if (v == null) return;
                if (TrySetParameter(ca, field, v, out _)) changes.Add(field);
            }

            Set("postExposure", args["exposure"]);
            Set("contrast", args["contrast"]);
            Set("saturation", args["saturation"]);
            Set("hueShift", args["hueShift"]);
            Set("colorFilter", args["colorFilter"]);

            EditorUtility.SetDirty(profile);
            return OpResult.Success(volume.GetInstanceID(),
                changes.Count > 0
                    ? $"applied color grading ({string.Join(", ", changes)})"
                    : "no color grading params provided");
        }

        // ---- helpers ----

        private static (Volume volume, string error) ResolveVolume(JObject args)
        {
            var id = (int?)args["volumeInstanceId"] ?? (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return (null, $"GameObject not found: instanceId={id}");
            var v = go.GetComponent<Volume>();
            if (v == null) return (null, "GameObject has no Volume component");
            return (v, null);
        }

        private static Type ResolveEffectType(string name)
        {
            var candidates = new[]
            {
                $"UnityEngine.Rendering.Universal.{name}",
                $"UnityEngine.Rendering.HighDefinition.{name}",
                $"UnityEngine.Rendering.{name}",
                name,
            };
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var c in candidates)
                {
                    var t = asm.GetType(c);
                    if (t != null && typeof(VolumeComponent).IsAssignableFrom(t)) return t;
                }
            }
            return null;
        }

        private static bool TrySetParameter(VolumeComponent effect, string propName, JToken value, out string error)
        {
            error = null;
            var member = (MemberInfo)effect.GetType().GetField(propName) ?? effect.GetType().GetProperty(propName);
            if (member == null) { error = $"effect has no parameter '{propName}'"; return false; }

            var paramObj = (member is FieldInfo fi)
                ? fi.GetValue(effect)
                : ((PropertyInfo)member).GetValue(effect);
            if (paramObj == null) { error = $"parameter '{propName}' is null"; return false; }

            var paramType = paramObj.GetType();
            var valueMember = (MemberInfo)paramType.GetField("value") ?? paramType.GetProperty("value");
            if (valueMember == null) { error = "parameter has no 'value' member"; return false; }

            var valueType = valueMember is FieldInfo vfi ? vfi.FieldType : ((PropertyInfo)valueMember).PropertyType;
            object converted;
            try { converted = ConvertToType(value, valueType); }
            catch (Exception e) { error = e.Message; return false; }

            if (valueMember is FieldInfo vfi2) vfi2.SetValue(paramObj, converted);
            else ((PropertyInfo)valueMember).SetValue(paramObj, converted);

            // VolumeParameter has a settable 'overrideState' bool — flip it on so
            // the value actually shows up in the volume blend.
            var overrideMember = (MemberInfo)paramType.GetField("overrideState") ?? paramType.GetProperty("overrideState");
            if (overrideMember is FieldInfo ofi) ofi.SetValue(paramObj, true);
            else if (overrideMember is PropertyInfo opi) opi.SetValue(paramObj, true);
            return true;
        }

        private static object ConvertToType(JToken value, Type target)
        {
            if (target == typeof(float)) return (float)value;
            if (target == typeof(int)) return (int)value;
            if (target == typeof(bool)) return (bool)value;
            if (target == typeof(string)) return (string)value;
            if (target == typeof(Color)) return JsonHelpers.ParseColor(value);
            if (target == typeof(Vector2))
            {
                var v = JsonHelpers.ParseVector3(value);
                return new Vector2(v.x, v.y);
            }
            if (target == typeof(Vector3)) return JsonHelpers.ParseVector3(value);
            if (target == typeof(Vector4))
            {
                if (value is JArray arr && arr.Count >= 4)
                    return new Vector4((float)arr[0], (float)arr[1], (float)arr[2], (float)arr[3]);
                var v3 = JsonHelpers.ParseVector3(value);
                return new Vector4(v3.x, v3.y, v3.z, 0f);
            }
            if (target.IsEnum)
            {
                if (value.Type == JTokenType.Integer) return Enum.ToObject(target, (int)value);
                return Enum.Parse(target, (string)value, ignoreCase: true);
            }
            if (typeof(UnityEngine.Object).IsAssignableFrom(target))
            {
                var s = (string)value;
                if (string.IsNullOrEmpty(s)) return null;
                var path = s.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ? s : AssetDatabase.GUIDToAssetPath(s);
                return AssetDatabase.LoadAssetAtPath(path, target);
            }
            throw new NotSupportedException($"can't convert JSON value to {target.Name}");
        }
    }
}
