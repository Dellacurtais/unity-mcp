using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    public static class AudioSourceFactory
    {
        public static OpResult Create(JObject args)
        {
            var name = (string)args["name"] ?? "Audio Source";
            var clipRef = (string)args["clipGuidOrPath"]
                ?? (string)args["clipGuid"]
                ?? (string)args["clip"];

            AudioClip clip = null;
            if (!string.IsNullOrEmpty(clipRef)
                && !AudioAuthor.TryLoadClip(clipRef, out clip, out var clipError))
                return OpResult.Fail(clipError);

            if (!AudioAuthor.TryResolveParent(args["parent"], out var parent, out var parentError))
                return OpResult.Fail(parentError);
            if (!AudioAuthor.TryParsePosition(args["position"], out var position, out var positionError))
                return OpResult.Fail(positionError);
            if (!AudioAuthor.TryReadFloat(args, "volume", 1f, 0f, 1f, out var volume, out var volumeError))
                return OpResult.Fail(volumeError);
            if (!AudioAuthor.TryReadFloat(args, "pitch", 1f, -3f, 3f, out var pitch, out var pitchError))
                return OpResult.Fail(pitchError);

            using (var mutation = new MutationScope("Create Audio Source"))
            {
                var go = new GameObject(name);
                mutation.RegisterCreated(go, "Create Audio Source");
                if (parent != null) mutation.SetParent(go.transform, parent.transform, "Parent Audio Source");
                go.transform.localPosition = position;

                var source = mutation.AddComponent<AudioSource>(go);
                source.clip = clip;
                source.loop = (bool?)args["loop"] ?? source.loop;
                source.volume = volume;
                source.pitch = pitch;
                source.spatialBlend = (bool?)args["spatial"] == true ? 1f : 0f;
                source.playOnAwake = (bool?)args["playOnAwake"] ?? source.playOnAwake;
                mutation.Complete();

                var result = OpResult.Success(go.GetInstanceID(),
                    $"created audio source '{name}'" + (clip != null ? $" with clip '{clip.name}'" : string.Empty));
                result.Data = new JObject
                {
                    ["instanceId"] = go.GetInstanceID(),
                    ["source"] = AudioAuthor.SourceState(source),
                    ["clip"] = AudioAuthor.ClipReference(clip),
                };
                return result;
            }
        }
    }
}
