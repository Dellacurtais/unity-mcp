using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;
using Object = UnityEngine.Object;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    /// All audio authoring + discovery helpers — AudioSource (3D point sources,
    /// generic prop setter, runtime play), AudioMixer (list/read/route/exposed
    /// params/snapshots), and convenience tools (background music, reverb
    /// zone, listener management, audio audit).
    public static class AudioAuthor
    {
        // ============== Tier A — Core authoring ==============

        public static OpResult Create3DSource(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            GameObject go;
            bool createdNew = false;

            if (id != 0)
            {
                go = EditorUtility.InstanceIDToObject(id) as GameObject;
                if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            }
            else
            {
                var name = (string)args["name"] ?? "3D Audio Source";
                go = new GameObject(name);
                createdNew = true;
                Undo.RegisterCreatedObjectUndo(go, "Create 3D AudioSource");
                var parent = JsonHelpers.ResolveParent(args["parent"]);
                if (parent != null) Undo.SetTransformParent(go.transform, parent.transform, "Parent");
                go.transform.localPosition = JsonHelpers.ParseVector3(args["position"]);
            }

            var src = go.GetComponent<AudioSource>();
            if (src == null) src = Undo.AddComponent<AudioSource>(go);
            else Undo.RecordObject(src, "Configure 3D AudioSource");

            // 3D defaults — caller can override individual fields
            src.spatialBlend = 1.0f;
            src.loop = (bool?)args["loop"] ?? true;
            src.playOnAwake = (bool?)args["playOnAwake"] ?? true;
            src.volume = (float?)args["volume"] ?? 1.0f;
            src.pitch = (float?)args["pitch"] ?? 1.0f;
            src.minDistance = (float?)args["minDistance"] ?? 1.0f;
            src.maxDistance = (float?)args["maxDistance"] ?? 50.0f;
            src.dopplerLevel = (float?)args["dopplerLevel"] ?? 1.0f;
            src.spread = (float?)args["spread"] ?? 0f;
            src.priority = (int?)args["priority"] ?? 128;
            src.reverbZoneMix = (float?)args["reverbZoneMix"] ?? 1.0f;

            if (args["rolloffMode"] != null
                && Enum.TryParse((string)args["rolloffMode"], true, out AudioRolloffMode mode))
                src.rolloffMode = mode;

            var clipRef = (string)args["clipGuidOrPath"];
            if (!string.IsNullOrEmpty(clipRef))
            {
                var clip = LoadClip(clipRef);
                if (clip != null) src.clip = clip;
            }

            var mixerGroupRef = (string)args["mixerGroup"];
            if (!string.IsNullOrEmpty(mixerGroupRef))
            {
                var group = ResolveMixerGroup(mixerGroupRef, (string)args["mixerGuidOrPath"]);
                if (group != null) src.outputAudioMixerGroup = group;
            }

            return OpResult.Success(go.GetInstanceID(),
                $"{(createdNew ? "created" : "configured")} 3D AudioSource on '{go.name}'  " +
                $"min={src.minDistance} max={src.maxDistance} rolloff={src.rolloffMode}");
        }

        public static OpResult SetSourceProps(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            var src = go.GetComponent<AudioSource>();
            if (src == null) return OpResult.Fail("no AudioSource on GameObject");

            Undo.RecordObject(src, "Set AudioSource Props");
            var changes = new List<string>();

            if (args["volume"] != null) { src.volume = (float)args["volume"]; changes.Add("volume"); }
            if (args["pitch"] != null) { src.pitch = (float)args["pitch"]; changes.Add("pitch"); }
            if (args["spatialBlend"] != null) { src.spatialBlend = (float)args["spatialBlend"]; changes.Add("spatialBlend"); }
            if (args["minDistance"] != null) { src.minDistance = (float)args["minDistance"]; changes.Add("minDistance"); }
            if (args["maxDistance"] != null) { src.maxDistance = (float)args["maxDistance"]; changes.Add("maxDistance"); }
            if (args["dopplerLevel"] != null) { src.dopplerLevel = (float)args["dopplerLevel"]; changes.Add("dopplerLevel"); }
            if (args["spread"] != null) { src.spread = (float)args["spread"]; changes.Add("spread"); }
            if (args["panStereo"] != null) { src.panStereo = (float)args["panStereo"]; changes.Add("panStereo"); }
            if (args["reverbZoneMix"] != null) { src.reverbZoneMix = (float)args["reverbZoneMix"]; changes.Add("reverbZoneMix"); }
            if (args["priority"] != null) { src.priority = (int)args["priority"]; changes.Add("priority"); }
            if (args["mute"] != null) { src.mute = (bool)args["mute"]; changes.Add("mute"); }
            if (args["loop"] != null) { src.loop = (bool)args["loop"]; changes.Add("loop"); }
            if (args["playOnAwake"] != null) { src.playOnAwake = (bool)args["playOnAwake"]; changes.Add("playOnAwake"); }
            if (args["bypassEffects"] != null) { src.bypassEffects = (bool)args["bypassEffects"]; changes.Add("bypassEffects"); }
            if (args["bypassListenerEffects"] != null) { src.bypassListenerEffects = (bool)args["bypassListenerEffects"]; changes.Add("bypassListenerEffects"); }
            if (args["bypassReverbZones"] != null) { src.bypassReverbZones = (bool)args["bypassReverbZones"]; changes.Add("bypassReverbZones"); }

            if (args["rolloffMode"] != null
                && Enum.TryParse((string)args["rolloffMode"], true, out AudioRolloffMode mode))
            { src.rolloffMode = mode; changes.Add("rolloffMode"); }

            if (args["clip"] != null)
            {
                var clip = LoadClip((string)args["clip"]);
                if (clip != null) { src.clip = clip; changes.Add("clip"); }
            }

            if (changes.Count == 0) return OpResult.Success(id, "no fields changed");
            return OpResult.Success(id, $"AudioSource: {string.Join(", ", changes)}");
        }

        public static OpResult PlayAudio(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            var src = go.GetComponent<AudioSource>();
            if (src == null) return OpResult.Fail("no AudioSource");

            var action = ((string)args["action"] ?? "play").ToLowerInvariant();
            switch (action)
            {
                case "play": src.Play(); break;
                case "stop": src.Stop(); break;
                case "pause": src.Pause(); break;
                case "unpause": case "resume": src.UnPause(); break;
                case "playoneshot":
                    var clipRef = (string)args["clip"];
                    if (string.IsNullOrEmpty(clipRef)) return OpResult.Fail("clip required for PlayOneShot");
                    var clip = LoadClip(clipRef);
                    if (clip == null) return OpResult.Fail($"clip not found: {clipRef}");
                    var vol = (float?)args["volumeScale"] ?? 1f;
                    src.PlayOneShot(clip, vol);
                    break;
                default: return OpResult.Fail($"unknown action '{action}' (play/stop/pause/unpause/playOneShot)");
            }

            string note = "";
            if (!Application.isPlaying && (action == "play" || action == "playoneshot"))
                note = " (note: AudioSource doesn't actually emit in edit mode — enter Play Mode to hear it)";
            return OpResult.Success(id, $"AudioSource {action}{note}");
        }

        // ============== Tier B — AudioMixer ==============

        public static OpResult ListMixers(JObject args)
        {
            var nameContains = (string)args["nameContains"];
            var folder = (string)args["folder"];
            var limit = (int?)args["limit"] ?? 100;

            var sb = new StringBuilder();
            var guids = AssetDatabase.FindAssets("t:AudioMixer");
            int count = 0;
            foreach (var g in guids)
            {
                if (count >= limit) break;
                var path = AssetDatabase.GUIDToAssetPath(g);
                if (!string.IsNullOrEmpty(folder) && !path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) continue;
                var nameOnly = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrEmpty(nameContains) && nameOnly.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                sb.AppendLine($"  {nameOnly}  guid={g}  path={path}");
                count++;
            }
            return new OpResult { Ok = true, Summary = $"{count} AudioMixer(s):\n{sb}", InstanceID = 0 };
        }

        public static OpResult ReadMixer(JObject args)
        {
            var mixer = ResolveMixer(args, out var err);
            if (mixer == null) return OpResult.Fail(err);

            var sb = new StringBuilder();
            sb.AppendLine($"AudioMixer: {mixer.name} ({AssetDatabase.GetAssetPath(mixer)})");

            // Groups via FindMatchingGroups("") — empty matches all
            var groups = mixer.FindMatchingGroups("");
            sb.AppendLine($"  groups ({groups.Length}):");
            foreach (var gp in groups)
                sb.AppendLine($"    {gp.name}");

            // Snapshots + exposed parameters via SerializedObject (no public API)
            var so = new SerializedObject(mixer);
            var snapsProp = so.FindProperty("m_Snapshots");
            if (snapsProp != null && snapsProp.isArray)
            {
                sb.AppendLine($"  snapshots ({snapsProp.arraySize}):");
                for (int i = 0; i < snapsProp.arraySize; i++)
                {
                    var snap = snapsProp.GetArrayElementAtIndex(i).objectReferenceValue;
                    if (snap != null) sb.AppendLine($"    {snap.name}");
                }
            }
            var exposedProp = so.FindProperty("m_ExposedParameters");
            if (exposedProp != null && exposedProp.isArray)
            {
                sb.AppendLine($"  exposed parameters ({exposedProp.arraySize}):");
                for (int i = 0; i < exposedProp.arraySize; i++)
                {
                    var pp = exposedProp.GetArrayElementAtIndex(i);
                    var nameProp = pp.FindPropertyRelative("name");
                    var pname = nameProp?.stringValue ?? "?";
                    if (mixer.GetFloat(pname, out var v))
                        sb.AppendLine($"    {pname} = {v:F3}");
                    else
                        sb.AppendLine($"    {pname} (current value not readable)");
                }
            }

            return new OpResult { Ok = true, Summary = sb.ToString(), InstanceID = mixer.GetInstanceID() };
        }

        public static OpResult AssignToMixerGroup(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            var src = go.GetComponent<AudioSource>();
            if (src == null) return OpResult.Fail("no AudioSource on GameObject");

            var groupRef = (string)args["mixerGroup"];
            if (string.IsNullOrEmpty(groupRef)) return OpResult.Fail("mixerGroup is required");

            var group = ResolveMixerGroup(groupRef, (string)args["mixerGuidOrPath"]);
            if (group == null) return OpResult.Fail($"mixer group not found: {groupRef}");

            Undo.RecordObject(src, "Assign Mixer Group");
            src.outputAudioMixerGroup = group;
            return OpResult.Success(id, $"routed AudioSource to '{group.audioMixer.name}/{group.name}'");
        }

        public static OpResult SetMixerParameter(JObject args)
        {
            var mixer = ResolveMixer(args, out var err);
            if (mixer == null) return OpResult.Fail(err);

            var pname = (string)args["name"];
            if (string.IsNullOrEmpty(pname)) return OpResult.Fail("parameter name is required");
            var value = (float?)args["value"] ?? 0f;

            if (!mixer.SetFloat(pname, value))
                return OpResult.Fail(
                    $"parameter '{pname}' not exposed on '{mixer.name}'. " +
                    "Right-click the parameter in the AudioMixer window → 'Expose [name] to script'.");

            return OpResult.Success(mixer.GetInstanceID(), $"{mixer.name}.{pname} = {value:F3}");
        }

        public static OpResult PlayMixerSnapshot(JObject args)
        {
            var mixer = ResolveMixer(args, out var err);
            if (mixer == null) return OpResult.Fail(err);

            var snapName = (string)args["snapshot"];
            if (string.IsNullOrEmpty(snapName)) return OpResult.Fail("snapshot name is required");

            var snap = mixer.FindSnapshot(snapName);
            if (snap == null) return OpResult.Fail($"snapshot '{snapName}' not found on '{mixer.name}'");

            var transition = (float?)args["transitionSeconds"] ?? 0.5f;
            snap.TransitionTo(transition);
            return OpResult.Success(mixer.GetInstanceID(),
                $"transitioning '{mixer.name}' → snapshot '{snapName}' over {transition:F2}s");
        }

        // ============== Tier C — Convenience ==============

        public const string BgmGameObjectName = "__BGM__";

        public static OpResult SetupBackgroundMusic(JObject args)
        {
            var clipRef = (string)args["clipGuidOrPath"];
            if (string.IsNullOrEmpty(clipRef)) return OpResult.Fail("clipGuidOrPath is required");
            var clip = LoadClip(clipRef);
            if (clip == null) return OpResult.Fail($"clip not found: {clipRef}");

            var replaceExisting = (bool?)args["replaceExisting"] ?? true;
            var bgmGo = GameObject.Find(BgmGameObjectName);
            if (bgmGo != null && replaceExisting)
            {
                var existing = bgmGo.GetComponent<AudioSource>();
                if (existing != null && existing.isPlaying) existing.Stop();
            }
            if (bgmGo == null)
            {
                bgmGo = new GameObject(BgmGameObjectName);
                Undo.RegisterCreatedObjectUndo(bgmGo, "Create BGM");
            }

            var src = bgmGo.GetComponent<AudioSource>();
            if (src == null) src = Undo.AddComponent<AudioSource>(bgmGo);
            else Undo.RecordObject(src, "Setup BGM");

            src.clip = clip;
            src.loop = true;
            src.playOnAwake = true;
            src.spatialBlend = 0f; // 2D — heard everywhere
            src.volume = (float?)args["volume"] ?? 0.5f;
            src.pitch = (float?)args["pitch"] ?? 1.0f;
            src.priority = 0; // highest

            var mixerGroupRef = (string)args["mixerGroup"];
            if (!string.IsNullOrEmpty(mixerGroupRef))
            {
                var group = ResolveMixerGroup(mixerGroupRef, null);
                if (group != null) src.outputAudioMixerGroup = group;
            }

            return OpResult.Success(bgmGo.GetInstanceID(),
                $"BGM '{clip.name}' configured on {BgmGameObjectName} (volume={src.volume}, " +
                $"mixer={(src.outputAudioMixerGroup != null ? src.outputAudioMixerGroup.name : "none")})");
        }

        public static OpResult CreateReverbZone(JObject args)
        {
            var name = (string)args["name"] ?? "Reverb Zone";
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create Reverb Zone");

            var parent = JsonHelpers.ResolveParent(args["parent"]);
            if (parent != null) Undo.SetTransformParent(go.transform, parent.transform, "Parent");
            go.transform.localPosition = JsonHelpers.ParseVector3(args["position"]);

            var zone = Undo.AddComponent<AudioReverbZone>(go);
            zone.minDistance = (float?)args["minDistance"] ?? 10f;
            zone.maxDistance = (float?)args["maxDistance"] ?? 15f;

            if (args["preset"] != null
                && Enum.TryParse((string)args["preset"], true, out AudioReverbPreset preset))
                zone.reverbPreset = preset;

            return OpResult.Success(go.GetInstanceID(),
                $"reverb zone '{name}' preset={zone.reverbPreset} min={zone.minDistance} max={zone.maxDistance}");
        }

        public static OpResult AddListener(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");

            var removeOthers = (bool?)args["removeOthers"] ?? true;
            int removed = 0;
            if (removeOthers)
            {
                foreach (var existing in Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
                {
                    if (existing.gameObject != go)
                    {
                        Undo.DestroyObjectImmediate(existing);
                        removed++;
                    }
                }
            }

            var listener = go.GetComponent<AudioListener>();
            bool created = listener == null;
            if (created) listener = Undo.AddComponent<AudioListener>(go);
            return OpResult.Success(id,
                $"AudioListener {(created ? "added" : "already present")} on '{go.name}'" +
                (removed > 0 ? $" (removed {removed} on other GO[s])" : ""));
        }

        public static OpResult InspectAudioSetup(JObject args)
        {
            var sb = new StringBuilder();

            // Listeners
            var listeners = Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None);
            sb.AppendLine($"AudioListeners: {listeners.Length}");
            foreach (var l in listeners)
                sb.AppendLine($"  on '{l.gameObject.name}' (active={l.gameObject.activeInHierarchy}, enabled={l.enabled})");
            if (listeners.Length == 0)
                sb.AppendLine("  ⚠ NO AudioListener — sound won't be heard. Add one to your main camera.");
            else if (listeners.Length > 1)
                sb.AppendLine("  ⚠ MULTIPLE AudioListeners — Unity warns and only one renders.");

            // Sources
            var sources = Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None);
            sb.AppendLine();
            sb.AppendLine($"AudioSources: {sources.Length}");

            int withClip = 0, withoutClip = 0, spatial2D = 0, spatial3D = 0, mixed = 0;
            int loopAndAwake = 0, loopOnly = 0, awakeOnly = 0;
            var byMixer = new Dictionary<string, int>();

            foreach (var s in sources)
            {
                if (s.clip != null) withClip++; else withoutClip++;
                if (s.spatialBlend < 0.05f) spatial2D++;
                else if (s.spatialBlend > 0.95f) spatial3D++;
                else mixed++;
                if (s.loop && s.playOnAwake) loopAndAwake++;
                else if (s.loop) loopOnly++;
                else if (s.playOnAwake) awakeOnly++;
                var groupName = s.outputAudioMixerGroup != null ? s.outputAudioMixerGroup.name : "(no mixer)";
                byMixer[groupName] = byMixer.GetValueOrDefault(groupName) + 1;
            }
            sb.AppendLine($"  with clip: {withClip}, WITHOUT clip: {withoutClip}");
            sb.AppendLine($"  2D: {spatial2D}, 3D: {spatial3D}, mixed: {mixed}");
            sb.AppendLine($"  loop+playOnAwake: {loopAndAwake}, loop only: {loopOnly}, playOnAwake only (one-shot ambience): {awakeOnly}");
            sb.AppendLine($"  by mixer group:");
            foreach (var kv in byMixer.OrderByDescending(k => k.Value))
                sb.AppendLine($"    {kv.Key}: {kv.Value}");

            // Reverb zones
            var zones = Object.FindObjectsByType<AudioReverbZone>(FindObjectsSortMode.None);
            sb.AppendLine();
            sb.AppendLine($"AudioReverbZones: {zones.Length}");
            foreach (var z in zones)
                sb.AppendLine($"  on '{z.gameObject.name}' preset={z.reverbPreset} min={z.minDistance} max={z.maxDistance}");

            // Issues
            sb.AppendLine();
            sb.AppendLine("Issues:");
            int issues = 0;
            if (withoutClip > 0) { sb.AppendLine($"  ⚠ {withoutClip} AudioSource(s) have no clip — likely useless."); issues++; }
            if (listeners.Length == 0) { sb.AppendLine($"  ⚠ no AudioListener in scene."); issues++; }
            if (listeners.Length > 1) { sb.AppendLine($"  ⚠ {listeners.Length} AudioListeners — Unity expects exactly 1."); issues++; }
            if (issues == 0) sb.AppendLine("  none ✓");

            return new OpResult { Ok = true, Summary = sb.ToString(), InstanceID = 0 };
        }

        // ============== Helpers ==============

        private static AudioMixer ResolveMixer(JObject args, out string err)
        {
            err = null;
            var idObj = args["mixerInstanceId"];
            if (idObj != null && idObj.Type == JTokenType.Integer)
            {
                var mixerId = (int)idObj;
                var m = EditorUtility.InstanceIDToObject(mixerId) as AudioMixer;
                if (m == null) { err = $"mixer not found: instanceId={mixerId}"; return null; }
                return m;
            }
            var refStr = (string)args["mixerGuidOrPath"];
            if (string.IsNullOrEmpty(refStr))
            {
                err = "either mixerInstanceId or mixerGuidOrPath required";
                return null;
            }
            var path = refStr.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? refStr : AssetDatabase.GUIDToAssetPath(refStr);
            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(path);
            if (mixer == null) { err = $"mixer not found: {refStr}"; return null; }
            return mixer;
        }

        private static AudioMixerGroup ResolveMixerGroup(string groupRef, string mixerHint)
        {
            if (string.IsNullOrEmpty(groupRef)) return null;

            // Support "MixerName/GroupName" syntax
            string mixerName = null;
            string groupName = groupRef;
            if (groupRef.Contains("/"))
            {
                var parts = groupRef.Split('/');
                if (parts.Length >= 2) { mixerName = parts[0]; groupName = parts[parts.Length - 1]; }
            }

            string hintPath = null;
            if (!string.IsNullOrEmpty(mixerHint))
            {
                hintPath = mixerHint.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    ? mixerHint : AssetDatabase.GUIDToAssetPath(mixerHint);
            }

            var mixerGuids = AssetDatabase.FindAssets("t:AudioMixer");
            foreach (var g in mixerGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                if (!string.IsNullOrEmpty(hintPath) && !string.Equals(path, hintPath, StringComparison.OrdinalIgnoreCase)) continue;
                var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(path);
                if (mixer == null) continue;
                if (!string.IsNullOrEmpty(mixerName) && mixer.name != mixerName) continue;

                var matches = mixer.FindMatchingGroups(groupName);
                if (matches.Length == 0) continue;
                // Prefer exact-name match
                foreach (var m in matches)
                    if (m.name == groupName) return m;
                return matches[0];
            }
            return null;
        }

        private static AudioClip LoadClip(string refStr)
        {
            if (string.IsNullOrEmpty(refStr)) return null;
            var path = refStr.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? refStr : AssetDatabase.GUIDToAssetPath(refStr);
            return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        }
    }

    // Tiny GetValueOrDefault polyfill for net standard 2.0 dictionary
    internal static class _AudioDictExt
    {
        public static int GetValueOrDefault(this IDictionary<string, int> d, string key)
            => d.TryGetValue(key, out var v) ? v : 0;
    }
}
