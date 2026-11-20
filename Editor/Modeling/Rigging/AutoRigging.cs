using Maicon.SceneAnalyzer.Editor.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Modeling;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Rigging
{
    public sealed class RigAutoRigProposal
    {
        public GameObject Target;
        public Mesh SourceMesh;
        public Renderer SourceRenderer;
        public string MeshHash;
        public string ProposalHash;
        public float AverageConfidence;
        public readonly List<RigAutoRigLandmark> Landmarks = new List<RigAutoRigLandmark>();
        public string[] BlockingLandmarks => HumanoidAutoRigAnalyzer.RequiredLandmarkIds
            .Where(id => Landmarks.All(value => value.Id != id || value.Confidence < 0.55f || !Finite(value.LocalPosition))).ToArray();
        public bool CanApply => Target != null && SourceMesh != null && BlockingLandmarks.Length == 0
            && HumanoidAutoRigAnalyzer.RequiredLandmarkIds.Count == Landmarks.Select(value => value.Id).Distinct().Count();

        public void Rehash()
        {
            AverageConfidence = Landmarks.Count > 0 ? Landmarks.Average(value => value.Confidence) : 0f;
            using (var sha = SHA256.Create())
            {
                var data = new StringBuilder(MeshHash);
                foreach (var value in Landmarks.OrderBy(value => value.Id)) data.Append('|').Append(value.Id).Append(':').Append(value.ParentId).Append(':')
                    .Append(value.LocalPosition.x.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(value.LocalPosition.y.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(value.LocalPosition.z.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                    .Append(value.Confidence.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(value.Source);
                ProposalHash = Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(data.ToString())));
            }
        }

        internal JObject ToJson() => new JObject
        {
            ["contractVersion"] = "1.0", ["target"] = Target != null ? Target.name : string.Empty, ["meshHash"] = MeshHash,
            ["proposalHash"] = ProposalHash, ["averageConfidence"] = AverageConfidence, ["canApply"] = CanApply,
            ["blockingLandmarks"] = new JArray(BlockingLandmarks),
            ["landmarks"] = new JArray(Landmarks.Select(value => new JObject { ["id"] = value.Id, ["parentId"] = value.ParentId, ["localPosition"] = new JArray(value.LocalPosition.x, value.LocalPosition.y, value.LocalPosition.z), ["confidence"] = value.Confidence, ["source"] = value.Source })),
        };

        private static string Hex(byte[] bytes) { var result = new StringBuilder(bytes.Length * 2); foreach (byte value in bytes) result.Append(value.ToString("x2")); return result.ToString(); }
        private static bool Finite(Vector3 value) => !(float.IsNaN(value.x) || float.IsInfinity(value.x) || float.IsNaN(value.y) || float.IsInfinity(value.y) || float.IsNaN(value.z) || float.IsInfinity(value.z));
    }

    public sealed class RigAutoRigApplyResult
    {
        public SkinnedMeshRenderer Renderer;
        public RigAutoRigProfile Profile;
        public string MeshAssetPath;
        public string ProfileAssetPath;
        public string AvatarAssetPath;
        public string ValidationClipAssetPath;
        public string AvatarStatus;
        public int BoneCount;
        public int VertexCount;
    }

    public static class HumanoidAutoRigAnalyzer
    {
        public static readonly IReadOnlyList<string> RequiredLandmarkIds = new[]
        {
            "Hips", "Spine", "Chest", "Neck", "Head",
            "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand",
            "RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand",
            "LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "RightUpperLeg", "RightLowerLeg", "RightFoot",
        };
        public static RigAutoRigProposal Analyze(GameObject target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            ResolveMesh(target, out var renderer, out var mesh);
            var vertices = mesh.vertices; if (vertices.Length < 3) throw new InvalidOperationException("AUTO_RIG_MESH_TOO_SMALL");
            var root = target.transform; var points = vertices.Select(value => root.InverseTransformPoint(renderer.transform.TransformPoint(value))).ToArray();
            var bounds = new Bounds(points[0], Vector3.zero); for (int i = 1; i < points.Length; i++) bounds.Encapsulate(points[i]);
            if (bounds.size.y <= 1e-5f) throw new InvalidOperationException("AUTO_RIG_HEIGHT_INVALID");
            var geometry = GeometrySummary.Build(points, mesh.triangles, bounds);
            float confidence = GeometryConfidence(bounds, points) * Mathf.Lerp(0.65f, 1f, geometry.LargestComponentRatio);
            var proposal = new RigAutoRigProposal { Target = target, SourceMesh = mesh, SourceRenderer = renderer, MeshHash = MeshHash(mesh) };
            Vector3 At(float x, float y, float z) => geometry.GuidedPoint(x, y, z);
            Add(proposal, "Hips", "", At(.5f, .52f, .5f), confidence * geometry.SliceConfidence(.52f));
            Add(proposal, "Spine", "Hips", At(.5f, .63f, .5f), confidence * geometry.SliceConfidence(.63f)); Add(proposal, "Chest", "Spine", At(.5f, .75f, .5f), confidence * geometry.SliceConfidence(.75f));
            Add(proposal, "Neck", "Chest", At(.5f, .87f, .5f), confidence * geometry.SliceConfidence(.87f)); Add(proposal, "Head", "Neck", At(.5f, .96f, .5f), confidence * geometry.SliceConfidence(.96f));
            Add(proposal, "LeftShoulder", "Chest", At(.40f, .80f, .5f), confidence * geometry.SliceConfidence(.80f)); Add(proposal, "LeftUpperArm", "LeftShoulder", At(.28f, .78f, .5f), confidence * geometry.ExtremityConfidence); Add(proposal, "LeftLowerArm", "LeftUpperArm", At(.16f, .70f, .5f), confidence * geometry.ExtremityConfidence); Add(proposal, "LeftHand", "LeftLowerArm", At(.05f, .63f, .5f), confidence * geometry.ExtremityConfidence);
            Add(proposal, "RightShoulder", "Chest", At(.60f, .80f, .5f), confidence * geometry.SliceConfidence(.80f)); Add(proposal, "RightUpperArm", "RightShoulder", At(.72f, .78f, .5f), confidence * geometry.ExtremityConfidence); Add(proposal, "RightLowerArm", "RightUpperArm", At(.84f, .70f, .5f), confidence * geometry.ExtremityConfidence); Add(proposal, "RightHand", "RightLowerArm", At(.95f, .63f, .5f), confidence * geometry.ExtremityConfidence);
            Add(proposal, "LeftUpperLeg", "Hips", At(.43f, .48f, .5f), confidence * geometry.SliceConfidence(.48f)); Add(proposal, "LeftLowerLeg", "LeftUpperLeg", At(.43f, .25f, .5f), confidence * geometry.SliceConfidence(.25f)); Add(proposal, "LeftFoot", "LeftLowerLeg", At(.43f, .04f, .62f), confidence * geometry.SliceConfidence(.04f));
            Add(proposal, "RightUpperLeg", "Hips", At(.57f, .48f, .5f), confidence * geometry.SliceConfidence(.48f)); Add(proposal, "RightLowerLeg", "RightUpperLeg", At(.57f, .25f, .5f), confidence * geometry.SliceConfidence(.25f)); Add(proposal, "RightFoot", "RightLowerLeg", At(.57f, .04f, .62f), confidence * geometry.SliceConfidence(.04f));
            proposal.Rehash(); RigAutoRigProposalRegistry.Store(proposal); return proposal;
        }

        internal static void ResolveMesh(GameObject target, out Renderer renderer, out Mesh mesh)
        {
            var skinned = target.GetComponentInChildren<SkinnedMeshRenderer>(true); if (skinned != null && skinned.sharedMesh != null) { renderer = skinned; mesh = skinned.sharedMesh; return; }
            var filter = target.GetComponentInChildren<MeshFilter>(true); if (filter != null && filter.sharedMesh != null && filter.GetComponent<Renderer>() != null) { renderer = filter.GetComponent<Renderer>(); mesh = filter.sharedMesh; return; }
            throw new InvalidOperationException("AUTO_RIG_MESH_NOT_FOUND");
        }

        internal static string MeshHash(Mesh mesh)
        {
            using (var sha = SHA256.Create())
            {
                var text = new StringBuilder(mesh.vertexCount * 24); foreach (var value in mesh.vertices) text.Append(value.x.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(value.y.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(value.z.ToString("R", CultureInfo.InvariantCulture)).Append(';');
                foreach (int index in mesh.triangles) text.Append(index).Append(','); return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
        private static float GeometryConfidence(Bounds bounds, Vector3[] points) { float upright = bounds.size.y / Mathf.Max(1e-5f, Mathf.Max(bounds.size.x, bounds.size.z)); float symmetry = 1f - Mathf.Clamp01(Mathf.Abs(Mathf.Abs(bounds.min.x - bounds.center.x) - Mathf.Abs(bounds.max.x - bounds.center.x)) / Mathf.Max(bounds.size.x, 1e-5f)); return Mathf.Clamp01(.55f + Mathf.Clamp01(upright / 2f) * .25f + symmetry * .2f); }
        private static void Add(RigAutoRigProposal proposal, string id, string parent, Vector3 position, float confidence) => proposal.Landmarks.Add(new RigAutoRigLandmark { Id = id, ParentId = parent, LocalPosition = position, Confidence = confidence, Source = "local-heuristic" });

        private sealed class GeometrySummary
        {
            private readonly Vector3[] points;
            private readonly Bounds bounds;
            private readonly Dictionary<int, Vector3[]> slices = new Dictionary<int, Vector3[]>();
            public float LargestComponentRatio;
            public float ExtremityConfidence;

            public static GeometrySummary Build(Vector3[] points, int[] triangles, Bounds bounds)
            {
                var value = new GeometrySummary(points, bounds); value.LargestComponentRatio = points.Length < 100 ? 1f : LargestComponent(points.Length, triangles); value.ExtremityConfidence = points.Length < 100 ? 1f : value.Slice(.70f).Length >= 4 ? 1f : .45f; return value;
            }
            private GeometrySummary(Vector3[] values, Bounds valueBounds) { points = values; bounds = valueBounds; }
            public Vector3 GuidedPoint(float x, float y, float z)
            {
                var slice = Slice(y); if (slice.Length == 0) return bounds.min + Vector3.Scale(bounds.size, new Vector3(x, y, z));
                float minX = slice.Min(value => value.x), maxX = slice.Max(value => value.x); float averageZ = slice.Average(value => value.z);
                return new Vector3(Mathf.Lerp(minX, maxX, x), bounds.min.y + bounds.size.y * y, Mathf.Lerp(averageZ, bounds.min.z + bounds.size.z * z, .35f));
            }
            public float SliceConfidence(float normalizedY) => points.Length < 100 ? 1f : Mathf.Clamp01(Slice(normalizedY).Length / Mathf.Max(8f, points.Length * .002f));
            private Vector3[] Slice(float normalizedY)
            {
                int key = Mathf.RoundToInt(normalizedY * 1000f); if (slices.TryGetValue(key, out var cached)) return cached;
                float y = bounds.min.y + bounds.size.y * normalizedY, half = Mathf.Max(bounds.size.y * .025f, 1e-5f);
                cached = points.Where(value => Mathf.Abs(value.y - y) <= half).ToArray(); slices[key] = cached; return cached;
            }
            private static float LargestComponent(int vertexCount, int[] triangles)
            {
                if (vertexCount == 0) return 0f; var adjacency = new List<int>[vertexCount]; for (int i = 0; i < vertexCount; i++) adjacency[i] = new List<int>();
                for (int i = 0; i + 2 < triangles.Length; i += 3) { int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2]; if (a < 0 || b < 0 || c < 0 || a >= vertexCount || b >= vertexCount || c >= vertexCount) continue; adjacency[a].Add(b); adjacency[a].Add(c); adjacency[b].Add(a); adjacency[b].Add(c); adjacency[c].Add(a); adjacency[c].Add(b); }
                var visited = new bool[vertexCount]; int largest = 0;
                for (int start = 0; start < vertexCount; start++) if (!visited[start]) { int count = 0; var queue = new Queue<int>(); queue.Enqueue(start); visited[start] = true; while (queue.Count > 0) { int current = queue.Dequeue(); count++; foreach (int next in adjacency[current]) if (!visited[next]) { visited[next] = true; queue.Enqueue(next); } } largest = Mathf.Max(largest, count); }
                return largest / (float)vertexCount;
            }
        }
    }

    public static class HumanoidAutoRigService
    {
        public static RigAutoRigApplyResult Apply(RigAutoRigProposal proposal, string proposalHash, string meshHash, string outputFolder = null)
        {
            if (proposal == null) throw new ArgumentNullException(nameof(proposal)); proposal.Rehash();
            if (proposal.ProposalHash != proposalHash || proposal.MeshHash != meshHash) throw new InvalidOperationException("AUTO_RIG_HASH_MISMATCH");
            HumanoidAutoRigAnalyzer.ResolveMesh(proposal.Target, out var sourceRenderer, out var sourceMesh);
            if (HumanoidAutoRigAnalyzer.MeshHash(sourceMesh) != meshHash) throw new InvalidOperationException("AUTO_RIG_MESH_CHANGED");
            if (!proposal.CanApply) throw new InvalidOperationException("AUTO_RIG_LOW_CONFIDENCE: correct landmarks " + string.Join(",", proposal.BlockingLandmarks));
            var existingSkin = proposal.Target.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(value => value != null && value.bones != null && value.bones.Length > 0);
            if (existingSkin != null) throw new InvalidOperationException("AUTO_RIG_TARGET_ALREADY_RIGGED: remove or explicitly reuse the existing skeleton instead of creating a duplicate.");
            var folder = AssetModelerAssetPaths.ValidateAndEnsureFolder(string.IsNullOrWhiteSpace(outputFolder) ? "Assets/AssetModeler/Rigging/AutoRig" : outputFolder);
            using (var mutation = new MutationScope("Apply auto rig"))
            {
                var mesh = UnityEngine.Object.Instantiate(sourceMesh); mesh.name = sourceMesh.name + "_AutoRig"; string meshPath = AssetModelerAssetPaths.UniqueAssetPath(folder, mesh.name, ".asset"); AssetDatabase.CreateAsset(mesh, meshPath); mutation.RegisterCreated(mesh, "Create auto-rig mesh asset");
                var bones = new Dictionary<string, Transform>();
                foreach (var landmark in proposal.Landmarks)
                {
                    Transform parent = !string.IsNullOrEmpty(landmark.ParentId) && bones.TryGetValue(landmark.ParentId, out var value) ? value : proposal.Target.transform;
                    var bone = new GameObject(landmark.Id).transform; mutation.RegisterCreated(bone.gameObject, "Create auto-rig bone"); mutation.SetParent(bone, parent, "Parent auto-rig bone"); bone.position = proposal.Target.transform.TransformPoint(landmark.LocalPosition); bones[landmark.Id] = bone;
                }
                var rendererObject = sourceRenderer.gameObject; var skinned = rendererObject.GetComponent<SkinnedMeshRenderer>();
                if (skinned == null) { skinned = mutation.AddComponent<SkinnedMeshRenderer>(rendererObject); var staticRenderer = rendererObject.GetComponent<MeshRenderer>(); if (staticRenderer != null) { mutation.Record(staticRenderer, "Disable static renderer"); skinned.sharedMaterials = staticRenderer.sharedMaterials; staticRenderer.enabled = false; } }
                mutation.Record(skinned, "Assign auto rig"); skinned.sharedMesh = mesh; skinned.bones = proposal.Landmarks.Select(value => bones[value.Id]).ToArray(); skinned.rootBone = bones["Hips"];
                ApplyInitialWeights(proposal, skinned, mesh); mesh.bindposes = BindPoseService.Calculate(skinned); EditorUtility.SetDirty(mesh); EditorUtility.SetDirty(skinned);
                var profile = ScriptableObject.CreateInstance<RigAutoRigProfile>(); profile.name = proposal.Target.name + "_AutoRigProfile"; profile.MeshHash = meshHash; profile.ProposalHash = proposalHash; profile.AverageConfidence = proposal.AverageConfidence;
                foreach (var value in proposal.Landmarks) profile.Landmarks.Add(new RigAutoRigLandmark { Id = value.Id, ParentId = value.ParentId, LocalPosition = value.LocalPosition, Confidence = value.Confidence, Source = value.Source });
                string profilePath = AssetModelerAssetPaths.UniqueAssetPath(folder, profile.name, ".asset"); AssetDatabase.CreateAsset(profile, profilePath); mutation.RegisterCreated(profile, "Create auto-rig profile");
                TryCreateHumanoidAvatar(proposal.Target, bones, folder, mutation, out var avatar, out string avatarPath, out string avatarStatus);
                var validationClip = CreateValidationClip(proposal.Target.transform, bones, folder, mutation, out string validationPath);
                profile.AvatarAsset = avatar; profile.AvatarStatus = avatarStatus; profile.ValidationClip = validationClip; EditorUtility.SetDirty(profile);
                FitAnimatedBounds(proposal.Target, skinned, validationClip);
                var validation = RigClipValidationService.Validate(proposal.Target, new[] { validationClip }, 8);
                if (!validation.IsValid) throw new InvalidOperationException("AUTO_RIG_VALIDATION_CLIP_FAILED: generated rig produced invalid deformation samples.");
                mutation.Complete();
                return new RigAutoRigApplyResult { Renderer = skinned, Profile = profile, MeshAssetPath = meshPath, ProfileAssetPath = profilePath, AvatarAssetPath = avatarPath, ValidationClipAssetPath = validationPath, AvatarStatus = avatarStatus, BoneCount = bones.Count, VertexCount = mesh.vertexCount };
            }
        }

        private static void ApplyInitialWeights(RigAutoRigProposal proposal, SkinnedMeshRenderer renderer, Mesh mesh)
        {
            var table = new SkinWeightTable(mesh.vertexCount); var vertices = mesh.vertices;
            var landmarks = proposal.Landmarks; var byId = landmarks.ToDictionary(value => value.Id); float centerX = landmarks.Average(value => value.LocalPosition.x);
            for (int vertex = 0; vertex < vertices.Length; vertex++)
            {
                Vector3 rootLocal = proposal.Target.transform.InverseTransformPoint(renderer.transform.TransformPoint(vertices[vertex]));
                var nearest = landmarks.Select((value, index) =>
                {
                    Vector3 parent = !string.IsNullOrEmpty(value.ParentId) && byId.TryGetValue(value.ParentId, out var parentLandmark) ? parentLandmark.LocalPosition : value.LocalPosition;
                    float distance = DistanceToSegment(rootLocal, parent, value.LocalPosition);
                    bool wrongSide = value.Id.StartsWith("Left", StringComparison.Ordinal) ? rootLocal.x > centerX : value.Id.StartsWith("Right", StringComparison.Ordinal) && rootLocal.x < centerX;
                    bool arm = value.Id.Contains("Arm") || value.Id.Contains("Hand") || value.Id.Contains("Shoulder"); bool leg = value.Id.Contains("Leg") || value.Id.Contains("Foot");
                    if (wrongSide) distance *= 4f; if (arm && rootLocal.y < byId["Hips"].LocalPosition.y) distance *= 3f; if (leg && rootLocal.y > byId["Chest"].LocalPosition.y) distance *= 3f;
                    return new { index, distance };
                }).OrderBy(value => value.distance).Take(4).ToArray();
                float sum = nearest.Sum(value => 1f / (value.distance * value.distance + 1e-5f));
                foreach (var value in nearest) table.Vertices[vertex][value.index] = (1f / (value.distance * value.distance + 1e-5f)) / sum;
            }
            DiffuseAlongSurface(mesh, table, 2);
            for (int vertex = 0; vertex < table.Vertices.Count; vertex++) table.NormalizeAndLimit(vertex, 4, 1e-5f);
            table.Write(mesh);
        }

        private static float DistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            var segment = b - a; float denominator = segment.sqrMagnitude; if (denominator <= 1e-12f) return Vector3.Distance(point, b);
            float t = Mathf.Clamp01(Vector3.Dot(point - a, segment) / denominator); return Vector3.Distance(point, a + segment * t);
        }

        private static void DiffuseAlongSurface(Mesh mesh, SkinWeightTable table, int iterations)
        {
            var triangles = mesh.triangles; var adjacency = new HashSet<int>[mesh.vertexCount]; for (int i = 0; i < adjacency.Length; i++) adjacency[i] = new HashSet<int>();
            for (int i = 0; i + 2 < triangles.Length; i += 3) { int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2]; adjacency[a].Add(b); adjacency[a].Add(c); adjacency[b].Add(a); adjacency[b].Add(c); adjacency[c].Add(a); adjacency[c].Add(b); }
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                var next = table.Clone();
                for (int vertex = 0; vertex < table.Vertices.Count; vertex++)
                {
                    if (adjacency[vertex].Count == 0) continue; var aggregate = new Dictionary<int, float>();
                    foreach (var pair in table.Vertices[vertex]) aggregate[pair.Key] = pair.Value * .6f;
                    float neighborScale = .4f / adjacency[vertex].Count;
                    foreach (int neighbor in adjacency[vertex]) foreach (var pair in table.Vertices[neighbor]) { aggregate.TryGetValue(pair.Key, out float current); aggregate[pair.Key] = current + pair.Value * neighborScale; }
                    next.Vertices[vertex].Clear(); foreach (var pair in aggregate) next.Vertices[vertex][pair.Key] = pair.Value;
                }
                for (int vertex = 0; vertex < table.Vertices.Count; vertex++) { table.Vertices[vertex].Clear(); foreach (var pair in next.Vertices[vertex]) table.Vertices[vertex][pair.Key] = pair.Value; }
            }
        }

        private static void TryCreateHumanoidAvatar(GameObject target, IReadOnlyDictionary<string, Transform> bones, string folder, MutationScope mutation, out Avatar avatar, out string avatarPath, out string status)
        {
            avatar = null; avatarPath = string.Empty; status = "Generic fallback";
            try
            {
                var human = new List<HumanBone>();
                foreach (var pair in HumanMap()) if (bones.ContainsKey(pair.Key)) human.Add(new HumanBone { boneName = pair.Key, humanName = pair.Value, limit = new HumanLimit { useDefaultValues = true } });
                var transforms = target.GetComponentsInChildren<Transform>(true);
                var skeleton = transforms.Select(value => new SkeletonBone { name = value.name, position = value.localPosition, rotation = value.localRotation, scale = value.localScale }).ToArray();
                var description = new HumanDescription { human = human.ToArray(), skeleton = skeleton, upperArmTwist = .5f, lowerArmTwist = .5f, upperLegTwist = .5f, lowerLegTwist = .5f, armStretch = .05f, legStretch = .05f, feetSpacing = 0f, hasTranslationDoF = false };
                avatar = AvatarBuilder.BuildHumanAvatar(target, description); avatar.name = target.name + "_Avatar";
                if (!avatar.isValid || !avatar.isHuman) { UnityEngine.Object.DestroyImmediate(avatar); avatar = null; status = "Avatar Humanoid could not be validated; Generic fallback active"; return; }
                avatarPath = AssetModelerAssetPaths.UniqueAssetPath(folder, avatar.name, ".asset"); AssetDatabase.CreateAsset(avatar, avatarPath); mutation.RegisterCreated(avatar, "Create Humanoid Avatar");
                var animator = target.GetComponent<Animator>() ?? mutation.AddComponent<Animator>(target); mutation.Record(animator, "Assign Humanoid Avatar"); animator.avatar = avatar; status = "Humanoid Avatar valid";
            }
            catch (Exception exception) { if (avatar != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(avatar))) UnityEngine.Object.DestroyImmediate(avatar); avatar = null; avatarPath = string.Empty; status = "Generic fallback: " + exception.Message; }
        }

        private static IEnumerable<KeyValuePair<string, string>> HumanMap()
        {
            yield return new KeyValuePair<string, string>("Hips", "Hips"); yield return new KeyValuePair<string, string>("Spine", "Spine"); yield return new KeyValuePair<string, string>("Chest", "Chest"); yield return new KeyValuePair<string, string>("Neck", "Neck"); yield return new KeyValuePair<string, string>("Head", "Head");
            yield return new KeyValuePair<string, string>("LeftShoulder", "LeftShoulder"); yield return new KeyValuePair<string, string>("LeftUpperArm", "LeftUpperArm"); yield return new KeyValuePair<string, string>("LeftLowerArm", "LeftLowerArm"); yield return new KeyValuePair<string, string>("LeftHand", "LeftHand");
            yield return new KeyValuePair<string, string>("RightShoulder", "RightShoulder"); yield return new KeyValuePair<string, string>("RightUpperArm", "RightUpperArm"); yield return new KeyValuePair<string, string>("RightLowerArm", "RightLowerArm"); yield return new KeyValuePair<string, string>("RightHand", "RightHand");
            yield return new KeyValuePair<string, string>("LeftUpperLeg", "LeftUpperLeg"); yield return new KeyValuePair<string, string>("LeftLowerLeg", "LeftLowerLeg"); yield return new KeyValuePair<string, string>("LeftFoot", "LeftFoot"); yield return new KeyValuePair<string, string>("RightUpperLeg", "RightUpperLeg"); yield return new KeyValuePair<string, string>("RightLowerLeg", "RightLowerLeg"); yield return new KeyValuePair<string, string>("RightFoot", "RightFoot");
        }

        private static AnimationClip CreateValidationClip(Transform root, IReadOnlyDictionary<string, Transform> bones, string folder, MutationScope mutation, out string path)
        {
            var clip = new AnimationClip { name = root.name + "_RigValidation", frameRate = 30f };
            foreach (string id in new[] { "LeftUpperArm", "RightUpperArm", "LeftUpperLeg", "RightUpperLeg" })
            {
                if (!bones.TryGetValue(id, out var bone)) continue; string bonePath = RigTargetResolver.RelativePath(bone, root); float sign = id.StartsWith("Left", StringComparison.Ordinal) ? 1f : -1f;
                clip.SetCurve(bonePath, typeof(Transform), "m_LocalRotation.z", new AnimationCurve(new Keyframe(0f, bone.localRotation.z), new Keyframe(.5f, Mathf.Clamp(bone.localRotation.z + sign * .2f, -1f, 1f)), new Keyframe(1f, bone.localRotation.z)));
                clip.SetCurve(bonePath, typeof(Transform), "m_LocalRotation.w", AnimationCurve.Constant(0f, 1f, bone.localRotation.w));
            }
            path = AssetModelerAssetPaths.UniqueAssetPath(folder, clip.name, ".anim"); AssetDatabase.CreateAsset(clip, path); mutation.RegisterCreated(clip, "Create auto-rig validation clip"); return clip;
        }

        private static void FitAnimatedBounds(GameObject root, SkinnedMeshRenderer renderer, AnimationClip clip)
        {
            var pose = RigPoseSnapshot.Capture(root); var bake = new Mesh(); bool started = !AnimationMode.InAnimationMode(); if (started) AnimationMode.StartAnimationMode(); bool initialized = false; var bounds = new Bounds();
            try
            {
                for (int sample = 0; sample < 8; sample++)
                {
                    pose.Restore(); AnimationMode.BeginSampling(); try { AnimationMode.SampleAnimationClip(root, clip, clip.length * sample / 7f); } finally { AnimationMode.EndSampling(); }
                    renderer.BakeMesh(bake); var sampleBounds = bake.bounds; if (!initialized) { bounds = sampleBounds; initialized = true; } else bounds.Encapsulate(sampleBounds);
                }
                if (initialized) { bounds.Expand(Mathf.Max(.001f, bounds.size.magnitude * .01f)); renderer.localBounds = bounds; }
            }
            finally { pose.Restore(); if (started && AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode(); UnityEngine.Object.DestroyImmediate(bake); }
        }
    }

    public static class RigAiProposalService
    {
        public static JObject BuildRequest(GameObject target, IEnumerable<string> viewAssetPaths = null)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            var baseline = HumanoidAutoRigAnalyzer.Analyze(target);
            var mesh = baseline.SourceMesh;
            var renderer = baseline.SourceRenderer;
            var views = new JArray();
            foreach (string rawPath in viewAssetPaths ?? Enumerable.Empty<string>())
            {
                string path = (rawPath ?? string.Empty).Replace('\\', '/').Trim();
                if (string.IsNullOrWhiteSpace(path)) continue;
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                views.Add(new JObject
                {
                    ["assetPath"] = path,
                    ["guid"] = AssetDatabase.AssetPathToGUID(path),
                    ["width"] = texture != null ? texture.width : 0,
                    ["height"] = texture != null ? texture.height : 0,
                });
            }
            var bounds = mesh.bounds;
            return new JObject
            {
                ["contractVersion"] = "1.0",
                ["requestKind"] = "humanoid-auto-rig-landmarks",
                ["target"] = target.name,
                ["meshHash"] = baseline.MeshHash,
                ["consentRequiredBeforeExternalSend"] = true,
                ["assetPayloadEmbedded"] = false,
                ["meshSummary"] = new JObject
                {
                    ["assetPath"] = AssetDatabase.GetAssetPath(mesh),
                    ["rendererPath"] = RigTargetResolver.RelativePath(renderer.transform, target.transform),
                    ["vertexCount"] = mesh.vertexCount,
                    ["triangleCount"] = mesh.triangles.Length / 3,
                    ["subMeshCount"] = mesh.subMeshCount,
                    ["blendShapeCount"] = mesh.blendShapeCount,
                    ["hasExistingSkin"] = renderer is SkinnedMeshRenderer,
                    ["boundsCenter"] = new JArray(bounds.center.x, bounds.center.y, bounds.center.z),
                    ["boundsSize"] = new JArray(bounds.size.x, bounds.size.y, bounds.size.z),
                },
                ["referenceViews"] = views,
                ["requiredLandmarks"] = new JArray(baseline.Landmarks.Select(value => new JObject
                {
                    ["id"] = value.Id,
                    ["parentId"] = value.ParentId,
                })),
                ["localBaseline"] = baseline.ToJson(),
                ["responseRequirements"] = new JObject
                {
                    ["contractVersion"] = "1.0",
                    ["sameMeshHash"] = true,
                    ["coordinateSpace"] = "target-local",
                    ["allRequiredLandmarks"] = true,
                    ["confidenceRange"] = new JArray(0f, 1f),
                },
            };
        }

        public static void ExportRequest(GameObject target, IEnumerable<string> viewAssetPaths, string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath)) throw new ArgumentException("AI_REQUEST_PATH_REQUIRED");
            File.WriteAllText(absolutePath, BuildRequest(target, viewAssetPaths).ToString(), new UTF8Encoding(false));
        }

        public static RigAutoRigProposal ImportJson(GameObject target, string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("AI_PROPOSAL_JSON_REQUIRED"); var root = JObject.Parse(json);
            if ((string)root["contractVersion"] != "1.0") throw new InvalidOperationException("AI_PROPOSAL_VERSION_UNSUPPORTED");
            var baseline = HumanoidAutoRigAnalyzer.Analyze(target); if ((string)root["meshHash"] != baseline.MeshHash) throw new InvalidOperationException("AI_PROPOSAL_MESH_HASH_MISMATCH");
            var landmarks = root["landmarks"] as JArray; if (landmarks == null) throw new InvalidOperationException("AI_PROPOSAL_LANDMARKS_REQUIRED"); baseline.Landmarks.Clear();
            foreach (var token in landmarks.OfType<JObject>())
            {
                var position = token["localPosition"] as JArray; if (position == null || position.Count != 3) throw new InvalidOperationException("AI_PROPOSAL_POSITION_INVALID");
                baseline.Landmarks.Add(new RigAutoRigLandmark { Id = (string)token["id"], ParentId = (string)token["parentId"] ?? string.Empty, LocalPosition = new Vector3((float)position[0], (float)position[1], (float)position[2]), Confidence = Mathf.Clamp01((float?)token["confidence"] ?? 0f), Source = "ai-json" });
            }
            var required = new HashSet<string>(HumanoidAutoRigAnalyzer.Analyze(target).Landmarks.Select(value => value.Id)); if (!required.SetEquals(baseline.Landmarks.Select(value => value.Id))) throw new InvalidOperationException("AI_PROPOSAL_REQUIRED_LANDMARKS_MISSING");
            baseline.Rehash(); RigAutoRigProposalRegistry.Store(baseline); return baseline;
        }
    }

    internal static class RigAutoRigProposalRegistry
    {
        private static readonly Dictionary<int, RigAutoRigProposal> Values = new Dictionary<int, RigAutoRigProposal>();
        public static void Store(RigAutoRigProposal proposal) { if (proposal?.Target != null) Values[proposal.Target.GetMcpInstanceId()] = proposal; }
        public static RigAutoRigProposal Get(GameObject target) { Values.TryGetValue(target != null ? target.GetMcpInstanceId() : 0, out var value); return value; }
    }

    [InitializeOnLoad]
    internal static class AutoRigPreviewController
    {
        public static RigAutoRigProposal Active { get; private set; }
        static AutoRigPreviewController() => SceneView.duringSceneGui += Draw;
        public static void Show(RigAutoRigProposal proposal) { Active = proposal; SceneView.RepaintAll(); }
        public static void Clear() { Active = null; SceneView.RepaintAll(); }
        private static void Draw(SceneView view)
        {
            if (Active?.Target == null) return; var lookup = Active.Landmarks.ToDictionary(value => value.Id);
            foreach (var value in Active.Landmarks)
            {
                Vector3 point = Active.Target.transform.TransformPoint(value.LocalPosition); Handles.color = Color.Lerp(Color.red, Color.green, value.Confidence);
                Handles.SphereHandleCap(0, point, Quaternion.identity, HandleUtility.GetHandleSize(point) * .035f, EventType.Repaint);
                if (!string.IsNullOrEmpty(value.ParentId) && lookup.TryGetValue(value.ParentId, out var parent)) Handles.DrawAAPolyLine(3f, point, Active.Target.transform.TransformPoint(parent.LocalPosition));
            }
        }
    }
}
