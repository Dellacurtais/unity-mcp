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
    public static class AudioAuthor
    {
        public const string BgmGameObjectName = "__BGM__";

        public static OpResult Create3DSource(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var existing = id == 0 ? null : EditorUtility.InstanceIDToObject(id) as GameObject;
            if (id != 0 && existing == null)
                return OpResult.Fail($"GameObject not found: instanceId={id}");

            var creating = existing == null;
            GameObject parent = null;
            var position = Vector3.zero;
            if (creating)
            {
                if (!TryResolveParent(args["parent"], out parent, out var parentError))
                    return OpResult.Fail(parentError);
                if (!TryParsePosition(args["position"], out position, out var positionError))
                    return OpResult.Fail(positionError);
            }

            AudioClip clip = null;
            var clipRef = (string)args["clipGuidOrPath"];
            if (!string.IsNullOrEmpty(clipRef) && !TryLoadClip(clipRef, out clip, out var clipError))
                return OpResult.Fail(clipError);

            AudioMixerGroup mixerGroup = null;
            var mixerGroupRef = (string)args["mixerGroup"];
            if (!string.IsNullOrEmpty(mixerGroupRef)
                && !TryResolveMixerGroup(mixerGroupRef, (string)args["mixerGuidOrPath"], out mixerGroup, out var mixerError))
                return OpResult.Fail(mixerError);
            if (string.IsNullOrEmpty(mixerGroupRef) && args["mixerGuidOrPath"] != null)
                return OpResult.Fail("mixerGroup is required when mixerGuidOrPath is supplied");

            if (!TryReadFloat(args, "volume", 1f, 0f, 1f, out var volume, out var volumeError))
                return OpResult.Fail(volumeError);
            if (!TryReadFloat(args, "pitch", 1f, -3f, 3f, out var pitch, out var pitchError))
                return OpResult.Fail(pitchError);
            if (!TryReadFloat(args, "minDistance", 1f, 0f, float.MaxValue, out var minDistance, out var minError))
                return OpResult.Fail(minError);
            if (!TryReadFloat(args, "maxDistance", 50f, 0f, float.MaxValue, out var maxDistance, out var maxError))
                return OpResult.Fail(maxError);
            if (maxDistance < minDistance)
                return OpResult.Fail("maxDistance must be greater than or equal to minDistance");
            if (!TryReadFloat(args, "dopplerLevel", 1f, 0f, 5f, out var dopplerLevel, out var dopplerError))
                return OpResult.Fail(dopplerError);
            if (!TryReadFloat(args, "spread", 0f, 0f, 360f, out var spread, out var spreadError))
                return OpResult.Fail(spreadError);
            if (!TryReadInt(args, "priority", 128, 0, 256, out var priority, out var priorityError))
                return OpResult.Fail(priorityError);
            if (!TryReadFloat(args, "reverbZoneMix", 1f, 0f, 1.1f, out var reverbZoneMix, out var reverbError))
                return OpResult.Fail(reverbError);
            if (!TryReadEnum(args, "rolloffMode", AudioRolloffMode.Logarithmic, out AudioRolloffMode rolloffMode, out var rolloffError))
                return OpResult.Fail(rolloffError);

            using (var mutation = new MutationScope(creating ? "Create 3D AudioSource" : "Configure 3D AudioSource"))
            {
                var go = existing;
                if (go == null)
                {
                    go = new GameObject((string)args["name"] ?? "3D Audio Source");
                    mutation.RegisterCreated(go, "Create 3D AudioSource");
                    if (parent != null) mutation.SetParent(go.transform, parent.transform, "Parent 3D AudioSource");
                    go.transform.localPosition = position;
                }

                var source = go.GetComponent<AudioSource>();
                if (source == null) source = mutation.AddComponent<AudioSource>(go);
                else mutation.Record(source, "Configure 3D AudioSource");

                source.spatialBlend = 1f;
                source.loop = (bool?)args["loop"] ?? true;
                source.playOnAwake = (bool?)args["playOnAwake"] ?? true;
                source.volume = volume;
                source.pitch = pitch;
                source.minDistance = minDistance;
                source.maxDistance = maxDistance;
                source.dopplerLevel = dopplerLevel;
                source.spread = spread;
                source.priority = priority;
                source.reverbZoneMix = reverbZoneMix;
                source.rolloffMode = rolloffMode;
                if (!string.IsNullOrEmpty(clipRef)) source.clip = clip;
                if (mixerGroup != null) source.outputAudioMixerGroup = mixerGroup;

                mutation.Complete();
                var result = OpResult.Success(go.GetInstanceID(),
                    $"{(creating ? "created" : "configured")} 3D AudioSource on '{go.name}' min={source.minDistance} max={source.maxDistance} rolloff={source.rolloffMode}");
                result.Data = new JObject
                {
                    ["instanceId"] = go.GetInstanceID(),
                    ["created"] = creating,
                    ["sourceState"] = SourceState(source),
                    ["clip"] = ClipReference(source.clip),
                    ["mixer"] = MixerReference(source.outputAudioMixerGroup != null ? source.outputAudioMixerGroup.audioMixer : null),
                };
                return result;
            }
        }

        public static OpResult SetSourceProps(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            var source = go.GetComponent<AudioSource>();
            if (source == null) return OpResult.Fail("no AudioSource on GameObject");

            var nextMinDistance = source.minDistance;
            var nextMaxDistance = source.maxDistance;
            if (!TryReadOptionalFloat(args, "volume", 0f, 1f, out var volume, out var hasVolume, out var volumeError))
                return OpResult.Fail(volumeError);
            if (!TryReadOptionalFloat(args, "pitch", -3f, 3f, out var pitch, out var hasPitch, out var pitchError))
                return OpResult.Fail(pitchError);
            if (!TryReadOptionalFloat(args, "spatialBlend", 0f, 1f, out var spatialBlend, out var hasSpatialBlend, out var spatialError))
                return OpResult.Fail(spatialError);
            if (!TryReadOptionalFloat(args, "minDistance", 0f, float.MaxValue, out var minDistance, out var hasMinDistance, out var minError))
                return OpResult.Fail(minError);
            if (!TryReadOptionalFloat(args, "maxDistance", 0f, float.MaxValue, out var maxDistance, out var hasMaxDistance, out var maxError))
                return OpResult.Fail(maxError);
            if (hasMinDistance) nextMinDistance = minDistance;
            if (hasMaxDistance) nextMaxDistance = maxDistance;
            if (nextMaxDistance < nextMinDistance)
                return OpResult.Fail("maxDistance must be greater than or equal to minDistance");
            if (!TryReadOptionalFloat(args, "dopplerLevel", 0f, 5f, out var dopplerLevel, out var hasDopplerLevel, out var dopplerError))
                return OpResult.Fail(dopplerError);
            if (!TryReadOptionalFloat(args, "spread", 0f, 360f, out var spread, out var hasSpread, out var spreadError))
                return OpResult.Fail(spreadError);
            if (!TryReadOptionalFloat(args, "panStereo", -1f, 1f, out var panStereo, out var hasPanStereo, out var panError))
                return OpResult.Fail(panError);
            if (!TryReadOptionalFloat(args, "reverbZoneMix", 0f, 1.1f, out var reverbZoneMix, out var hasReverbZoneMix, out var reverbError))
                return OpResult.Fail(reverbError);
            if (!TryReadOptionalInt(args, "priority", 0, 256, out var priority, out var hasPriority, out var priorityError))
                return OpResult.Fail(priorityError);

            AudioRolloffMode rolloffMode = source.rolloffMode;
            var hasRolloffMode = args["rolloffMode"] != null;
            if (hasRolloffMode && !TryReadEnum(args, "rolloffMode", source.rolloffMode, out rolloffMode, out var rolloffError))
                return OpResult.Fail(rolloffError);

            AudioClip clip = null;
            var hasClip = args["clip"] != null;
            if (hasClip && !TryLoadClip((string)args["clip"], out clip, out var clipError))
                return OpResult.Fail(clipError);

            var changes = new List<string>();
            AddIfPresent(args, changes, "volume");
            AddIfPresent(args, changes, "pitch");
            AddIfPresent(args, changes, "spatialBlend");
            AddIfPresent(args, changes, "minDistance");
            AddIfPresent(args, changes, "maxDistance");
            AddIfPresent(args, changes, "dopplerLevel");
            AddIfPresent(args, changes, "spread");
            AddIfPresent(args, changes, "panStereo");
            AddIfPresent(args, changes, "reverbZoneMix");
            AddIfPresent(args, changes, "priority");
            AddIfPresent(args, changes, "mute");
            AddIfPresent(args, changes, "loop");
            AddIfPresent(args, changes, "playOnAwake");
            AddIfPresent(args, changes, "bypassEffects");
            AddIfPresent(args, changes, "bypassListenerEffects");
            AddIfPresent(args, changes, "bypassReverbZones");
            AddIfPresent(args, changes, "rolloffMode");
            AddIfPresent(args, changes, "clip");

            if (changes.Count == 0)
            {
                var noChange = OpResult.Success(id, "no fields changed");
                noChange.Data = new JObject
                {
                    ["instanceId"] = id,
                    ["changed"] = new JArray(),
                    ["sourceState"] = SourceState(source),
                };
                return noChange;
            }

            using (var mutation = new MutationScope("Set AudioSource Props"))
            {
                mutation.Record(source, "Set AudioSource Props");
                if (hasVolume) source.volume = volume;
                if (hasPitch) source.pitch = pitch;
                if (hasSpatialBlend) source.spatialBlend = spatialBlend;
                if (hasMinDistance) source.minDistance = minDistance;
                if (hasMaxDistance) source.maxDistance = maxDistance;
                if (hasDopplerLevel) source.dopplerLevel = dopplerLevel;
                if (hasSpread) source.spread = spread;
                if (hasPanStereo) source.panStereo = panStereo;
                if (hasReverbZoneMix) source.reverbZoneMix = reverbZoneMix;
                if (hasPriority) source.priority = priority;
                if (args["mute"] != null) source.mute = (bool)args["mute"];
                if (args["loop"] != null) source.loop = (bool)args["loop"];
                if (args["playOnAwake"] != null) source.playOnAwake = (bool)args["playOnAwake"];
                if (args["bypassEffects"] != null) source.bypassEffects = (bool)args["bypassEffects"];
                if (args["bypassListenerEffects"] != null) source.bypassListenerEffects = (bool)args["bypassListenerEffects"];
                if (args["bypassReverbZones"] != null) source.bypassReverbZones = (bool)args["bypassReverbZones"];
                if (hasRolloffMode) source.rolloffMode = rolloffMode;
                if (hasClip) source.clip = clip;
                mutation.Complete();

                var result = OpResult.Success(id, $"AudioSource: {string.Join(", ", changes)}");
                result.Data = new JObject
                {
                    ["instanceId"] = id,
                    ["changed"] = new JArray(changes),
                    ["sourceState"] = SourceState(source),
                };
                return result;
            }
        }

        public static OpResult PlayAudio(JObject args)
        {
            if (!EditorApplication.isPlaying)
                return OpResult.Fail("PLAY_MODE_REQUIRED: play_audio_source is only available in Play Mode");

            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            var source = go.GetComponent<AudioSource>();
            if (source == null) return OpResult.Fail("no AudioSource on GameObject");

            var action = ((string)args["action"] ?? "play").ToLowerInvariant();
            switch (action)
            {
                case "play": source.Play(); break;
                case "stop": source.Stop(); break;
                case "pause": source.Pause(); break;
                case "unpause":
                case "resume": source.UnPause(); break;
                case "playoneshot":
                    var clipRef = (string)args["clip"];
                    if (string.IsNullOrEmpty(clipRef)) return OpResult.Fail("clip is required for playOneShot");
                    if (!TryLoadClip(clipRef, out var clip, out var clipError)) return OpResult.Fail(clipError);
                    if (!TryReadFloat(args, "volumeScale", 1f, 0f, float.MaxValue, out var volumeScale, out var volumeError))
                        return OpResult.Fail(volumeError);
                    source.PlayOneShot(clip, volumeScale);
                    break;
                default:
                    return OpResult.Fail($"unknown action '{action}' (play/stop/pause/unpause/playOneShot)");
            }

            var result = OpResult.Success(id, $"AudioSource {action}");
            result.Data = new JObject
            {
                ["instanceId"] = id,
                ["action"] = action,
                ["isPlaying"] = source.isPlaying,
                ["time"] = source.time,
            };
            return result;
        }

        public static OpResult ListMixers(JObject args)
        {
            var nameContains = (string)args["nameContains"];
            var folder = NormalizeAssetFolder((string)args["folder"]);
            if (args["folder"] != null && folder == null)
                return OpResult.Fail("folder must be an Assets-relative folder path");
            if (!TryReadInt(args, "limit", 100, 1, 500, out var limit, out var limitError))
                return OpResult.Fail(limitError);

            var paths = AssetDatabase.FindAssets("t:AudioMixer")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .Where(path => folder == null || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
                .Where(path => string.IsNullOrEmpty(nameContains)
                    || Path.GetFileNameWithoutExtension(path).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var mixers = new JArray();
            foreach (var path in paths.Take(limit))
            {
                var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(path);
                if (mixer != null) mixers.Add(MixerReference(mixer));
            }

            var summary = new StringBuilder();
            summary.AppendLine($"{mixers.Count} of {paths.Length} AudioMixer(s):");
            foreach (var item in mixers) summary.AppendLine($"  {(string)item["name"]}  guid={(string)item["guid"]}  path={(string)item["assetPath"]}");
            var result = OpResult.Success(0, summary.ToString());
            result.Data = new JObject
            {
                ["mixers"] = mixers,
                ["count"] = mixers.Count,
                ["total"] = paths.Length,
                ["limit"] = limit,
            };
            return result;
        }

        public static OpResult ReadMixer(JObject args)
        {
            var mixer = ResolveMixer(args, out var error);
            if (mixer == null) return OpResult.Fail(error);

            var groups = new JArray();
            foreach (var group in mixer.FindMatchingGroups("").OrderBy(group => group.name, StringComparer.OrdinalIgnoreCase))
                groups.Add(GroupReference(group));

            var snapshots = new JArray();
            var exposedParameters = new JArray();
            var serialized = new SerializedObject(mixer);
            serialized.UpdateIfRequiredOrScript();
            var snapshotsProperty = serialized.FindProperty("m_Snapshots");
            if (snapshotsProperty != null && snapshotsProperty.isArray)
            {
                for (var i = 0; i < snapshotsProperty.arraySize; i++)
                {
                    var snapshot = snapshotsProperty.GetArrayElementAtIndex(i).objectReferenceValue as AudioMixerSnapshot;
                    if (snapshot == null) continue;
                    snapshots.Add(new JObject
                    {
                        ["name"] = snapshot.name,
                        ["instanceId"] = snapshot.GetInstanceID(),
                    });
                }
            }

            var exposedProperty = serialized.FindProperty("m_ExposedParameters");
            if (exposedProperty != null && exposedProperty.isArray)
            {
                for (var i = 0; i < exposedProperty.arraySize; i++)
                {
                    var property = exposedProperty.GetArrayElementAtIndex(i);
                    var nameProperty = property.FindPropertyRelative("name");
                    var parameterName = nameProperty != null ? nameProperty.stringValue : null;
                    if (string.IsNullOrEmpty(parameterName)) continue;
                    var readable = mixer.GetFloat(parameterName, out var value);
                    exposedParameters.Add(new JObject
                    {
                        ["name"] = parameterName,
                        ["readable"] = readable,
                        ["value"] = readable ? new JValue(value) : JValue.CreateNull(),
                    });
                }
            }

            var mixerJson = MixerReference(mixer);
            mixerJson["groups"] = groups;
            mixerJson["snapshots"] = snapshots;
            mixerJson["exposedParameters"] = exposedParameters;
            var result = OpResult.Success(mixer.GetInstanceID(),
                $"AudioMixer '{mixer.name}': {groups.Count} group(s), {snapshots.Count} snapshot(s), {exposedParameters.Count} exposed parameter(s)");
            result.Data = mixerJson;
            return result;
        }

        public static OpResult AssignToMixerGroup(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            var source = go.GetComponent<AudioSource>();
            if (source == null) return OpResult.Fail("no AudioSource on GameObject");

            var groupRef = (string)args["mixerGroup"];
            if (string.IsNullOrWhiteSpace(groupRef)) return OpResult.Fail("mixerGroup is required");
            if (!TryResolveMixerGroup(groupRef, (string)args["mixerGuidOrPath"], out var group, out var groupError))
                return OpResult.Fail(groupError);

            using (var mutation = new MutationScope("Assign Mixer Group"))
            {
                mutation.Record(source, "Assign Mixer Group");
                source.outputAudioMixerGroup = group;
                mutation.Complete();
                var result = OpResult.Success(id, $"routed AudioSource to '{group.audioMixer.name}/{group.name}'");
                result.Data = new JObject
                {
                    ["instanceId"] = id,
                    ["group"] = GroupReference(group),
                    ["mixer"] = MixerReference(group.audioMixer),
                    ["sourceState"] = SourceState(source),
                };
                return result;
            }
        }

        public static OpResult SetMixerParameter(JObject args)
        {
            if (!EditorApplication.isPlaying)
                return OpResult.Fail("PLAY_MODE_REQUIRED: set_mixer_parameter is only available in Play Mode");

            var mixer = ResolveMixer(args, out var error);
            if (mixer == null) return OpResult.Fail(error);
            var parameterName = (string)args["name"];
            if (string.IsNullOrWhiteSpace(parameterName)) return OpResult.Fail("parameter name is required");
            if (!TryReadFloat(args, "value", 0f, float.MinValue, float.MaxValue, out var value, out var valueError))
                return OpResult.Fail(valueError);
            if (!mixer.SetFloat(parameterName, value))
                return OpResult.Fail($"parameter '{parameterName}' is not exposed on '{mixer.name}' or cannot be set while snapshots are being edited");

            var result = OpResult.Success(mixer.GetInstanceID(), $"{mixer.name}.{parameterName} = {value:F3}");
            result.Data = new JObject
            {
                ["mixer"] = MixerReference(mixer),
                ["name"] = parameterName,
                ["value"] = value,
            };
            return result;
        }

        public static OpResult PlayMixerSnapshot(JObject args)
        {
            if (!EditorApplication.isPlaying)
                return OpResult.Fail("PLAY_MODE_REQUIRED: play_mixer_snapshot is only available in Play Mode");

            var mixer = ResolveMixer(args, out var error);
            if (mixer == null) return OpResult.Fail(error);
            var snapshotName = (string)args["snapshot"];
            if (string.IsNullOrWhiteSpace(snapshotName)) return OpResult.Fail("snapshot name is required");
            var snapshot = mixer.FindSnapshot(snapshotName);
            if (snapshot == null) return OpResult.Fail($"snapshot '{snapshotName}' not found on '{mixer.name}'");
            if (!TryReadFloat(args, "transitionSeconds", 0.5f, 0f, float.MaxValue, out var transition, out var transitionError))
                return OpResult.Fail(transitionError);

            snapshot.TransitionTo(transition);
            var result = OpResult.Success(mixer.GetInstanceID(),
                $"transitioning '{mixer.name}' to snapshot '{snapshotName}' over {transition:F2}s");
            result.Data = new JObject
            {
                ["mixer"] = MixerReference(mixer),
                ["snapshot"] = snapshotName,
                ["snapshotInstanceId"] = snapshot.GetInstanceID(),
                ["transitionSeconds"] = transition,
            };
            return result;
        }

        public static OpResult SetupBackgroundMusic(JObject args)
        {
            var clipRef = (string)args["clipGuidOrPath"];
            if (string.IsNullOrWhiteSpace(clipRef)) return OpResult.Fail("clipGuidOrPath is required");
            if (!TryLoadClip(clipRef, out var clip, out var clipError)) return OpResult.Fail(clipError);
            if (!TryReadFloat(args, "volume", 0.5f, 0f, 1f, out var volume, out var volumeError))
                return OpResult.Fail(volumeError);
            if (!TryReadFloat(args, "pitch", 1f, -3f, 3f, out var pitch, out var pitchError))
                return OpResult.Fail(pitchError);

            AudioMixerGroup mixerGroup = null;
            var groupRef = (string)args["mixerGroup"];
            if (!string.IsNullOrEmpty(groupRef)
                && !TryResolveMixerGroup(groupRef, (string)args["mixerGuidOrPath"], out mixerGroup, out var mixerError))
                return OpResult.Fail(mixerError);
            if (string.IsNullOrEmpty(groupRef) && args["mixerGuidOrPath"] != null)
                return OpResult.Fail("mixerGroup is required when mixerGuidOrPath is supplied");

            var existingObjects = Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(candidate => candidate.scene.IsValid() && candidate.name == BgmGameObjectName)
                .ToArray();
            if (existingObjects.Length > 1)
                return OpResult.Fail($"multiple GameObjects named '{BgmGameObjectName}' exist; resolve the singleton conflict first");

            var replaceExisting = (bool?)args["replaceExisting"] ?? true;
            var existing = existingObjects.FirstOrDefault();
            if (existing != null && !replaceExisting)
                return OpResult.Fail($"background music already exists on '{BgmGameObjectName}'; set replaceExisting=true to update it");

            using (var mutation = new MutationScope("Setup Background Music"))
            {
                var gameObject = existing;
                if (gameObject == null)
                {
                    gameObject = new GameObject(BgmGameObjectName);
                    mutation.RegisterCreated(gameObject, "Create BGM");
                }

                if (!gameObject.activeSelf)
                {
                    Undo.RecordObject(gameObject, "Activate BGM");
                    gameObject.SetActive(true);
                    mutation.Touch(gameObject);
                }

                var source = gameObject.GetComponent<AudioSource>();
                if (source == null) source = mutation.AddComponent<AudioSource>(gameObject);
                else mutation.Record(source, "Setup BGM");
                source.clip = clip;
                source.loop = true;
                source.playOnAwake = true;
                source.spatialBlend = 0f;
                source.volume = volume;
                source.pitch = pitch;
                source.priority = 0;
                source.outputAudioMixerGroup = mixerGroup;
                mutation.Complete();

                var result = OpResult.Success(gameObject.GetInstanceID(),
                    $"BGM '{clip.name}' configured on {BgmGameObjectName} (volume={source.volume}, mixer={(mixerGroup != null ? mixerGroup.name : "none")})");
                result.Data = new JObject
                {
                    ["instanceId"] = gameObject.GetInstanceID(),
                    ["created"] = existing == null,
                    ["clip"] = ClipReference(clip),
                    ["mixer"] = MixerReference(mixerGroup != null ? mixerGroup.audioMixer : null),
                    ["sourceState"] = SourceState(source),
                };
                return result;
            }
        }

        public static OpResult CreateReverbZone(JObject args)
        {
            if (!TryResolveParent(args["parent"], out var parent, out var parentError))
                return OpResult.Fail(parentError);
            if (!TryParsePosition(args["position"], out var position, out var positionError))
                return OpResult.Fail(positionError);
            if (!TryReadFloat(args, "minDistance", 10f, 0f, float.MaxValue, out var minDistance, out var minError))
                return OpResult.Fail(minError);
            if (!TryReadFloat(args, "maxDistance", 15f, 0f, float.MaxValue, out var maxDistance, out var maxError))
                return OpResult.Fail(maxError);
            if (maxDistance < minDistance)
                return OpResult.Fail("maxDistance must be greater than or equal to minDistance");
            if (!TryReadEnum(args, "preset", AudioReverbPreset.Generic, out AudioReverbPreset preset, out var presetError))
                return OpResult.Fail(presetError);

            var name = (string)args["name"] ?? "Reverb Zone";
            using (var mutation = new MutationScope("Create Reverb Zone"))
            {
                var gameObject = new GameObject(name);
                mutation.RegisterCreated(gameObject, "Create Reverb Zone");
                if (parent != null) mutation.SetParent(gameObject.transform, parent.transform, "Parent Reverb Zone");
                gameObject.transform.localPosition = position;
                var zone = mutation.AddComponent<AudioReverbZone>(gameObject);
                zone.minDistance = minDistance;
                zone.maxDistance = maxDistance;
                zone.reverbPreset = preset;
                mutation.Complete();

                var result = OpResult.Success(gameObject.GetInstanceID(),
                    $"reverb zone '{name}' preset={zone.reverbPreset} min={zone.minDistance} max={zone.maxDistance}");
                result.Data = new JObject
                {
                    ["instanceId"] = gameObject.GetInstanceID(),
                    ["name"] = gameObject.name,
                    ["preset"] = zone.reverbPreset.ToString(),
                    ["minDistance"] = zone.minDistance,
                    ["maxDistance"] = zone.maxDistance,
                    ["position"] = JsonHelpers.Vec3(gameObject.transform.localPosition),
                };
                return result;
            }
        }

        public static OpResult AddListener(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var gameObject = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (gameObject == null) return OpResult.Fail($"GameObject not found: instanceId={id}");

            var removeOthers = (bool?)args["removeOthers"] ?? true;
            var others = removeOthers
                ? Object.FindObjectsByType<AudioListener>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .Where(listener => listener.gameObject != gameObject)
                    .ToArray()
                : Array.Empty<AudioListener>();

            using (var mutation = new MutationScope("Add AudioListener"))
            {
                foreach (var other in others) mutation.Destroy(other);
                var listener = gameObject.GetComponent<AudioListener>();
                var created = listener == null;
                if (created) listener = mutation.AddComponent<AudioListener>(gameObject);
                mutation.Complete();

                var result = OpResult.Success(id,
                    $"AudioListener {(created ? "added" : "already present")} on '{gameObject.name}'" +
                    (others.Length > 0 ? $" (removed {others.Length} from other GameObjects)" : string.Empty));
                result.Data = new JObject
                {
                    ["instanceId"] = id,
                    ["listenerInstanceId"] = listener.GetInstanceID(),
                    ["created"] = created,
                    ["removedCount"] = others.Length,
                    ["activeInHierarchy"] = gameObject.activeInHierarchy,
                    ["enabled"] = listener.enabled,
                };
                return result;
            }
        }

        public static OpResult InspectAudioSetup(JObject args)
        {
            var listeners = Object.FindObjectsByType<AudioListener>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .OrderBy(listener => HierarchyPath(listener.gameObject), StringComparer.Ordinal)
                .ToArray();
            var sources = Object.FindObjectsByType<AudioSource>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .OrderBy(source => HierarchyPath(source.gameObject), StringComparer.Ordinal)
                .ToArray();
            var zones = Object.FindObjectsByType<AudioReverbZone>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .OrderBy(zone => HierarchyPath(zone.gameObject), StringComparer.Ordinal)
                .ToArray();

            var listenerItems = new JArray();
            foreach (var listener in listeners)
            {
                listenerItems.Add(new JObject
                {
                    ["instanceId"] = listener.GetInstanceID(),
                    ["gameObjectInstanceId"] = listener.gameObject.GetInstanceID(),
                    ["name"] = listener.gameObject.name,
                    ["hierarchyPath"] = HierarchyPath(listener.gameObject),
                    ["activeInHierarchy"] = listener.gameObject.activeInHierarchy,
                    ["enabled"] = listener.enabled,
                    ["activeListener"] = listener.enabled && listener.gameObject.activeInHierarchy,
                });
            }

            var sourceItems = new JArray();
            foreach (var source in sources)
            {
                var state = SourceState(source);
                state["hierarchyPath"] = HierarchyPath(source.gameObject);
                state["activeInHierarchy"] = source.gameObject.activeInHierarchy;
                sourceItems.Add(state);
            }

            var zoneItems = new JArray();
            foreach (var zone in zones)
            {
                zoneItems.Add(new JObject
                {
                    ["instanceId"] = zone.GetInstanceID(),
                    ["gameObjectInstanceId"] = zone.gameObject.GetInstanceID(),
                    ["name"] = zone.gameObject.name,
                    ["hierarchyPath"] = HierarchyPath(zone.gameObject),
                    ["activeInHierarchy"] = zone.gameObject.activeInHierarchy,
                    ["enabled"] = zone.enabled,
                    ["preset"] = zone.reverbPreset.ToString(),
                    ["minDistance"] = zone.minDistance,
                    ["maxDistance"] = zone.maxDistance,
                });
            }

            var withClip = sources.Count(source => source.clip != null);
            var withoutClip = sources.Length - withClip;
            var spatial2D = sources.Count(source => source.spatialBlend < 0.05f);
            var spatial3D = sources.Count(source => source.spatialBlend > 0.95f);
            var mixed = sources.Length - spatial2D - spatial3D;
            var activeListenerCount = listeners.Count(listener => listener.enabled && listener.gameObject.activeInHierarchy);
            var issues = new JArray();
            if (withoutClip > 0)
                issues.Add(new JObject { ["code"] = "AUDIO_SOURCE_WITHOUT_CLIP", ["count"] = withoutClip, ["severity"] = "warning" });
            if (activeListenerCount == 0)
                issues.Add(new JObject { ["code"] = "NO_ACTIVE_AUDIO_LISTENER", ["count"] = 0, ["severity"] = "warning" });
            if (activeListenerCount > 1)
                issues.Add(new JObject { ["code"] = "MULTIPLE_ACTIVE_AUDIO_LISTENERS", ["count"] = activeListenerCount, ["severity"] = "warning" });

            var byMixer = new JArray();
            foreach (var bucket in sources
                .GroupBy(source => source.outputAudioMixerGroup != null
                    ? source.outputAudioMixerGroup.GetInstanceID()
                    : 0)
                .Select(grouping => new
                {
                    Group = grouping.First().outputAudioMixerGroup,
                    Count = grouping.Count(),
                })
                .OrderByDescending(bucket => bucket.Count)
                .ThenBy(bucket => bucket.Group != null ? AssetDatabase.GetAssetPath(bucket.Group.audioMixer) : string.Empty, StringComparer.Ordinal)
                .ThenBy(bucket => bucket.Group != null ? bucket.Group.name : string.Empty, StringComparer.Ordinal)
                .ThenBy(bucket => bucket.Group != null ? bucket.Group.GetInstanceID() : 0))
            {
                var groupLabel = bucket.Group != null
                    ? bucket.Group.audioMixer.name + "/" + bucket.Group.name
                    : "(no mixer)";
                byMixer.Add(new JObject
                {
                    ["group"] = groupLabel,
                    ["mixer"] = MixerReference(bucket.Group != null ? bucket.Group.audioMixer : null),
                    ["mixerGroup"] = GroupReference(bucket.Group),
                    ["count"] = bucket.Count,
                });
            }

            var data = new JObject
            {
                ["listenerCount"] = listeners.Length,
                ["activeListenerCount"] = activeListenerCount,
                ["sourceCount"] = sources.Length,
                ["reverbZoneCount"] = zones.Length,
                ["listeners"] = listenerItems,
                ["sources"] = sourceItems,
                ["reverbZones"] = zoneItems,
                ["withClip"] = withClip,
                ["withoutClip"] = withoutClip,
                ["spatial2D"] = spatial2D,
                ["spatial3D"] = spatial3D,
                ["spatialMixed"] = mixed,
                ["byMixerGroup"] = byMixer,
                ["issues"] = issues,
                ["issueCount"] = issues.Count,
            };

            var summary = new StringBuilder();
            summary.AppendLine($"AudioListeners: {listeners.Length} ({activeListenerCount} active)");
            summary.AppendLine($"AudioSources: {sources.Length} ({withClip} with clip, {withoutClip} without clip; {spatial2D} 2D, {spatial3D} 3D, {mixed} mixed)");
            summary.AppendLine($"AudioReverbZones: {zones.Length}");
            summary.AppendLine($"Issues: {issues.Count}");
            var result = OpResult.Success(0, summary.ToString());
            result.Data = data;
            return result;
        }

        private static AudioMixer ResolveMixer(JObject args, out string error)
        {
            error = null;
            var hasInstanceId = args["mixerInstanceId"] != null;
            var hasReference = args["mixerGuidOrPath"] != null;
            if (hasInstanceId == hasReference)
            {
                error = "provide exactly one of mixerInstanceId or mixerGuidOrPath";
                return null;
            }

            if (hasInstanceId)
            {
                var id = (int)args["mixerInstanceId"];
                var byId = EditorUtility.InstanceIDToObject(id) as AudioMixer;
                if (byId == null) error = $"mixer not found: instanceId={id}";
                return byId;
            }

            return TryLoadMixer((string)args["mixerGuidOrPath"], out var mixer, out error) ? mixer : null;
        }

        private static bool TryResolveMixerGroup(
            string groupReference,
            string mixerHint,
            out AudioMixerGroup group,
            out string error)
        {
            group = null;
            error = null;
            if (string.IsNullOrWhiteSpace(groupReference))
            {
                error = "mixerGroup is required";
                return false;
            }

            AudioMixer hintedMixer = null;
            if (!string.IsNullOrEmpty(mixerHint) && !TryLoadMixer(mixerHint, out hintedMixer, out error))
                return false;

            var mixerQualifier = default(string);
            var groupQuery = groupReference.Trim();
            var slash = groupQuery.IndexOf('/');
            if (slash > 0)
            {
                var possibleMixer = groupQuery.Substring(0, slash);
                var mixerNameExists = AssetDatabase.FindAssets("t:AudioMixer")
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Select(AssetDatabase.LoadAssetAtPath<AudioMixer>)
                    .Any(mixer => mixer != null && string.Equals(mixer.name, possibleMixer, StringComparison.OrdinalIgnoreCase));
                if (mixerNameExists)
                {
                    mixerQualifier = possibleMixer;
                    groupQuery = groupQuery.Substring(slash + 1);
                }
            }

            if (hintedMixer != null && mixerQualifier != null
                && !string.Equals(hintedMixer.name, mixerQualifier, StringComparison.OrdinalIgnoreCase))
            {
                error = $"mixerGroup qualifier '{mixerQualifier}' conflicts with mixerGuidOrPath '{hintedMixer.name}'";
                return false;
            }

            var mixers = hintedMixer != null
                ? new[] { hintedMixer }
                : AssetDatabase.FindAssets("t:AudioMixer")
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(AssetDatabase.LoadAssetAtPath<AudioMixer>)
                    .Where(mixer => mixer != null)
                    .Where(mixer => mixerQualifier == null || string.Equals(mixer.name, mixerQualifier, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            if (mixers.Length == 0)
            {
                error = mixerQualifier == null ? "no AudioMixer assets found" : $"mixer not found: {mixerQualifier}";
                return false;
            }

            var targetName = groupQuery.Split('/').Last();
            var matches = new List<AudioMixerGroup>();
            foreach (var mixer in mixers)
            {
                var candidates = mixer.FindMatchingGroups(groupQuery);
                var exact = candidates.Where(candidate => string.Equals(candidate.name, targetName, StringComparison.OrdinalIgnoreCase)).ToArray();
                matches.AddRange(exact.Length > 0 ? exact : candidates);
            }

            matches = matches.GroupBy(candidate => candidate.GetInstanceID()).Select(grouping => grouping.First()).ToList();
            if (matches.Count == 0)
            {
                error = $"mixer group not found: {groupReference}";
                return false;
            }
            if (matches.Count > 1)
            {
                var choices = string.Join(", ", matches
                    .Select(candidate => candidate.audioMixer.name + "/" + candidate.name)
                    .OrderBy(choice => choice, StringComparer.OrdinalIgnoreCase));
                error = $"mixer group '{groupReference}' is ambiguous; pass mixerGuidOrPath or a qualified MixerName/GroupName. Matches: {choices}";
                return false;
            }

            group = matches[0];
            return true;
        }

        internal static bool TryLoadClip(string reference, out AudioClip clip, out string error)
        {
            clip = null;
            error = null;
            if (string.IsNullOrWhiteSpace(reference))
            {
                error = "audio clip reference is required";
                return false;
            }

            var path = ResolveAssetPath(reference);
            if (string.IsNullOrEmpty(path))
            {
                error = $"audio clip not found: {reference}";
                return false;
            }
            clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null)
            {
                error = $"asset is not an AudioClip or could not be loaded: {reference}";
                return false;
            }
            return true;
        }

        private static bool TryLoadMixer(string reference, out AudioMixer mixer, out string error)
        {
            mixer = null;
            error = null;
            if (string.IsNullOrWhiteSpace(reference))
            {
                error = "mixerGuidOrPath is required";
                return false;
            }
            var path = ResolveAssetPath(reference);
            mixer = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<AudioMixer>(path);
            if (mixer == null)
            {
                error = $"mixer not found: {reference}";
                return false;
            }
            return true;
        }

        internal static bool TryResolveParent(JToken token, out GameObject parent, out string error)
        {
            parent = null;
            error = null;
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token.Type != JTokenType.Integer)
            {
                error = "parent must be a GameObject instanceId";
                return false;
            }
            var id = (int)token;
            parent = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (parent == null)
            {
                error = $"parent GameObject not found: instanceId={id}";
                return false;
            }
            return true;
        }

        internal static bool TryParsePosition(JToken token, out Vector3 position, out string error)
        {
            position = Vector3.zero;
            error = null;
            if (token == null || token.Type == JTokenType.Null) return true;

            if (token is JArray array && array.Count == 3
                && TryNumber(array[0], out var ax)
                && TryNumber(array[1], out var ay)
                && TryNumber(array[2], out var az))
            {
                position = new Vector3(ax, ay, az);
                return true;
            }
            if (token is JObject obj
                && obj["x"] != null && obj["y"] != null && obj["z"] != null
                && TryNumber(obj["x"], out var ox)
                && TryNumber(obj["y"], out var oy)
                && TryNumber(obj["z"], out var oz))
            {
                position = new Vector3(ox, oy, oz);
                return true;
            }

            error = "position must be [x,y,z] or {x,y,z} with finite numeric values";
            return false;
        }

        internal static bool TryReadFloat(
            JObject args,
            string name,
            float fallback,
            float minimum,
            float maximum,
            out float value,
            out string error)
        {
            value = fallback;
            error = null;
            if (args[name] == null) return true;
            if (!TryNumber(args[name], out value))
            {
                error = $"{name} must be a finite number";
                return false;
            }
            if (value < minimum || value > maximum)
            {
                error = $"{name} must be between {minimum} and {maximum}";
                return false;
            }
            return true;
        }

        internal static JObject SourceState(AudioSource source)
        {
            return new JObject
            {
                ["instanceId"] = source.GetInstanceID(),
                ["gameObjectInstanceId"] = source.gameObject.GetInstanceID(),
                ["name"] = source.gameObject.name,
                ["clip"] = ClipReference(source.clip),
                ["loop"] = source.loop,
                ["playOnAwake"] = source.playOnAwake,
                ["volume"] = source.volume,
                ["pitch"] = source.pitch,
                ["spatialBlend"] = source.spatialBlend,
                ["minDistance"] = source.minDistance,
                ["maxDistance"] = source.maxDistance,
                ["rolloffMode"] = source.rolloffMode.ToString(),
                ["dopplerLevel"] = source.dopplerLevel,
                ["spread"] = source.spread,
                ["panStereo"] = source.panStereo,
                ["reverbZoneMix"] = source.reverbZoneMix,
                ["priority"] = source.priority,
                ["mute"] = source.mute,
                ["bypassEffects"] = source.bypassEffects,
                ["bypassListenerEffects"] = source.bypassListenerEffects,
                ["bypassReverbZones"] = source.bypassReverbZones,
                ["mixerGroup"] = GroupReference(source.outputAudioMixerGroup),
            };
        }

        internal static JToken ClipReference(AudioClip clip)
        {
            if (clip == null) return JValue.CreateNull();
            var path = AssetDatabase.GetAssetPath(clip);
            return new JObject
            {
                ["name"] = clip.name,
                ["instanceId"] = clip.GetInstanceID(),
                ["assetPath"] = path,
                ["guid"] = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path),
                ["length"] = clip.length,
                ["channels"] = clip.channels,
                ["frequency"] = clip.frequency,
            };
        }

        private static JObject MixerReference(AudioMixer mixer)
        {
            if (mixer == null) return null;
            var path = AssetDatabase.GetAssetPath(mixer);
            return new JObject
            {
                ["name"] = mixer.name,
                ["instanceId"] = mixer.GetInstanceID(),
                ["assetPath"] = path,
                ["guid"] = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path),
            };
        }

        private static JToken GroupReference(AudioMixerGroup group)
        {
            if (group == null) return JValue.CreateNull();
            return new JObject
            {
                ["name"] = group.name,
                ["instanceId"] = group.GetInstanceID(),
                ["mixerName"] = group.audioMixer != null ? group.audioMixer.name : null,
                ["mixerAssetPath"] = group.audioMixer != null ? AssetDatabase.GetAssetPath(group.audioMixer) : null,
            };
        }

        private static bool TryReadOptionalFloat(
            JObject args,
            string name,
            float minimum,
            float maximum,
            out float value,
            out bool present,
            out string error)
        {
            present = args[name] != null;
            value = 0f;
            error = null;
            return !present || TryReadFloat(args, name, 0f, minimum, maximum, out value, out error);
        }

        private static bool TryReadInt(
            JObject args,
            string name,
            int fallback,
            int minimum,
            int maximum,
            out int value,
            out string error)
        {
            value = fallback;
            error = null;
            var token = args[name];
            if (token == null) return true;
            if (token.Type != JTokenType.Integer)
            {
                error = $"{name} must be an integer";
                return false;
            }
            value = (int)token;
            if (value < minimum || value > maximum)
            {
                error = $"{name} must be between {minimum} and {maximum}";
                return false;
            }
            return true;
        }

        private static bool TryReadOptionalInt(
            JObject args,
            string name,
            int minimum,
            int maximum,
            out int value,
            out bool present,
            out string error)
        {
            present = args[name] != null;
            value = 0;
            error = null;
            return !present || TryReadInt(args, name, 0, minimum, maximum, out value, out error);
        }

        private static bool TryReadEnum<T>(JObject args, string name, T fallback, out T value, out string error)
            where T : struct
        {
            value = fallback;
            error = null;
            var token = args[name];
            if (token == null) return true;
            if (token.Type != JTokenType.String
                || !Enum.TryParse((string)token, true, out value)
                || !Enum.IsDefined(typeof(T), value))
            {
                error = $"{name} must be one of: {string.Join(", ", Enum.GetNames(typeof(T)))}";
                return false;
            }
            return true;
        }

        private static bool TryNumber(JToken token, out float value)
        {
            value = 0f;
            if (token == null || (token.Type != JTokenType.Float && token.Type != JTokenType.Integer)) return false;
            try { value = (float)token; }
            catch { return false; }
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static string ResolveAssetPath(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            return reference.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? reference.Replace('\\', '/')
                : AssetDatabase.GUIDToAssetPath(reference);
        }

        private static string NormalizeAssetFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            var normalized = folder.Replace('\\', '/').TrimEnd('/');
            if (normalized.Equals("Assets", StringComparison.OrdinalIgnoreCase)) return "Assets";
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/../")
                || normalized.EndsWith("/..", StringComparison.Ordinal))
                return null;
            return normalized;
        }

        private static void AddIfPresent(JObject args, ICollection<string> changes, string name)
        {
            if (args[name] != null) changes.Add(name);
        }

        private static string HierarchyPath(GameObject gameObject)
        {
            var names = new Stack<string>();
            for (var current = gameObject.transform; current != null; current = current.parent) names.Push(current.name);
            return string.Join("/", names);
        }
    }
}
