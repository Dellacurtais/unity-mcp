using System;
using System.IO;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Maicon.SceneAnalyzer.Editor.Timeline
{
    /// All Timeline (UnityEngine.Timeline) authoring + runtime helpers.
    /// Lives in a secondary asmdef gated by SCENE_ANALYZER_HAS_TIMELINE so
    /// projects without `com.unity.timeline` aren't broken by missing types.
    public static class TimelineAuthor
    {
        public static OpResult CreateTimeline(JObject args)
        {
            var path = (string)args["path"];
            if (string.IsNullOrEmpty(path)) return OpResult.Fail("path is required (e.g. Assets/Timelines/Intro.playable)");
            if (!path.EndsWith(".playable") && !path.EndsWith(".asset")) path += ".playable";
            EnsureFolder(Path.GetDirectoryName(path));

            var asset = ScriptableObject.CreateInstance<TimelineAsset>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();

            return new OpResult
            {
                Ok = true,
                InstanceID = asset.GetInstanceID(),
                Summary = $"created TimelineAsset at {path}",
                CreatedAssetPath = path,
            };
        }

        public static OpResult AddTrack(JObject args)
        {
            var asset = ResolveTimeline(args, out var err);
            if (asset == null) return OpResult.Fail(err);
            var trackKind = (string)args["trackType"] ?? "Animation";
            var trackName = (string)args["name"] ?? trackKind + " Track";

            var trackType = ResolveTrackType(trackKind);
            if (trackType == null) return OpResult.Fail($"unknown track type '{trackKind}' (Animation/Audio/Activation/Control/Signal/Marker, or full type name)");

            Undo.RecordObject(asset, "Add Timeline Track");
            var track = asset.CreateTrack(trackType, null, trackName);
            EditorUtility.SetDirty(asset);
            return OpResult.Success(asset.GetInstanceID(), $"added {trackType.Name} '{trackName}'");
        }

        public static OpResult AddClip(JObject args)
        {
            var asset = ResolveTimeline(args, out var err);
            if (asset == null) return OpResult.Fail(err);
            var trackName = (string)args["track"];
            if (string.IsNullOrEmpty(trackName)) return OpResult.Fail("track name is required");

            var track = asset.GetOutputTracks().FirstOrDefault(t => t.name == trackName);
            if (track == null) return OpResult.Fail($"track '{trackName}' not found");

            Undo.RecordObject(asset, "Add Timeline Clip");
            Undo.RecordObject(track, "Add Timeline Clip");
            var clip = track.CreateDefaultClip();
            if (args["start"] != null) clip.start = (double)args["start"];
            if (args["duration"] != null) clip.duration = (double)args["duration"];
            if (args["name"] != null) clip.displayName = (string)args["name"];

            // Try to assign the source via the track's playable asset shape.
            var srcRef = (string)args["sourceGuidOrPath"];
            if (!string.IsNullOrEmpty(srcRef))
            {
                var srcPath = srcRef.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    ? srcRef
                    : AssetDatabase.GUIDToAssetPath(srcRef);

                // Animation track wants an AnimationClip plugged into the playable asset's `clip` field
                if (track is AnimationTrack && clip.asset is AnimationPlayableAsset apa)
                {
                    apa.clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(srcPath);
                }
                else
                {
                    // Generic: reflect for a UnityEngine.Object-typed property/field
                    // and assign the loaded asset. Works for most playable assets.
                    var asset2 = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(srcPath);
                    if (asset2 != null) AssignSource(clip.asset as PlayableAsset, asset2);
                }
            }

            EditorUtility.SetDirty(asset);
            return OpResult.Success(asset.GetInstanceID(),
                $"added clip on '{trackName}' start={clip.start:F2} dur={clip.duration:F2}");
        }

        public static OpResult AssignToDirector(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");

            var director = go.GetComponent<PlayableDirector>();
            if (director == null) director = Undo.AddComponent<PlayableDirector>(go);

            var asset = ResolveTimeline(args, out var err);
            if (asset == null) return OpResult.Fail(err);

            Undo.RecordObject(director, "Assign Timeline");
            director.playableAsset = asset;
            return OpResult.Success(id, $"assigned timeline '{asset.name}' to director on '{go.name}'");
        }

        public static OpResult Play(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            var director = go.GetComponent<PlayableDirector>();
            if (director == null) return OpResult.Fail("no PlayableDirector on GameObject");

            var action = ((string)args["action"] ?? "play").ToLowerInvariant();
            switch (action)
            {
                case "play": director.Play(); break;
                case "stop": director.Stop(); break;
                case "pause": director.Pause(); break;
                case "resume": director.Resume(); break;
                case "evaluate":
                    var t = (double?)args["time"] ?? director.time;
                    director.time = t;
                    director.Evaluate();
                    break;
                default: return OpResult.Fail($"unknown action '{action}' (play/stop/pause/resume/evaluate)");
            }
            return OpResult.Success(id, $"timeline director: {action}");
        }

        // ---- helpers ----

        private static TimelineAsset ResolveTimeline(JObject args, out string err)
        {
            err = null;
            var idObj = args["timelineInstanceId"];
            if (idObj != null && idObj.Type == JTokenType.Integer)
            {
                var id = (int)idObj;
                var ta = EditorUtility.InstanceIDToObject(id) as TimelineAsset;
                if (ta == null) { err = $"timeline not found: instanceId={id}"; return null; }
                return ta;
            }
            var refStr = (string)args["timelineGuidOrPath"];
            if (string.IsNullOrEmpty(refStr)) { err = "timelineInstanceId or timelineGuidOrPath required"; return null; }
            var path = refStr.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? refStr : AssetDatabase.GUIDToAssetPath(refStr);
            var t = AssetDatabase.LoadAssetAtPath<TimelineAsset>(path);
            if (t == null) { err = $"timeline not found: {refStr}"; return null; }
            return t;
        }

        private static Type ResolveTrackType(string kind)
        {
            switch (kind.ToLowerInvariant())
            {
                case "animation": return typeof(AnimationTrack);
                case "audio": return typeof(AudioTrack);
                case "activation": return typeof(ActivationTrack);
                case "control": return typeof(ControlTrack);
                case "signal": return typeof(SignalTrack);
                case "marker": return typeof(MarkerTrack);
            }
            // Custom — try to resolve full name across loaded assemblies.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(kind);
                if (t != null && typeof(TrackAsset).IsAssignableFrom(t)) return t;
            }
            return null;
        }

        private static void AssignSource(PlayableAsset playable, UnityEngine.Object src)
        {
            if (playable == null || src == null) return;
            var type = playable.GetType();
            var srcType = src.GetType();
            foreach (var f in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (f.FieldType.IsAssignableFrom(srcType)) { f.SetValue(playable, src); return; }
            }
            foreach (var p in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!p.CanWrite) continue;
                if (p.PropertyType.IsAssignableFrom(srcType)) { p.SetValue(playable, src); return; }
            }
        }

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
