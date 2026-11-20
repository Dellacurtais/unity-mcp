using Maicon.SceneAnalyzer.Editor.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Maicon.SceneAnalyzer.Modeling;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Rigging
{
    public enum RigDiagnosticSeverity
    {
        Info,
        Warning,
        Error,
    }

    [Serializable]
    public sealed class RigDiagnosticIssue
    {
        public string Code;
        public RigDiagnosticSeverity Severity;
        public string Message;
        public string TargetPath;

        public RigDiagnosticIssue(string code, RigDiagnosticSeverity severity, string message, string targetPath = null)
        {
            Code = code ?? "UNKNOWN";
            Severity = severity;
            Message = message ?? string.Empty;
            TargetPath = targetPath ?? string.Empty;
        }

        internal JObject ToJson() => new JObject
        {
            ["code"] = Code,
            ["severity"] = Severity.ToString(),
            ["message"] = Message,
            ["targetPath"] = TargetPath,
        };
    }

    public sealed class RigTargetContext
    {
        public GameObject Root { get; internal set; }
        public Animator Animator { get; internal set; }
        public SkinnedMeshRenderer[] Renderers { get; internal set; } = Array.Empty<SkinnedMeshRenderer>();
        public Transform[] Bones { get; internal set; } = Array.Empty<Transform>();
        public string[] RendererPaths { get; internal set; } = Array.Empty<string>();
        public string[] MeshAssetPaths { get; internal set; } = Array.Empty<string>();
        public string[] MeshHashes { get; internal set; } = Array.Empty<string>();
        public string RigType { get; internal set; } = "None";
        public string HierarchyHash { get; internal set; } = string.Empty;
        public string BindPoseHash { get; internal set; } = string.Empty;
        public string SkinWeightHash { get; internal set; } = string.Empty;
        public string StateHash { get; internal set; } = string.Empty;

        public bool IsValid => Root != null && Renderers.Length > 0;
    }

    [Serializable]
    public sealed class RigDiagnosticReport
    {
        public string RootName;
        public string RigType;
        public string HierarchyHash;
        public string BindPoseHash;
        public string SkinWeightHash;
        public string StateHash;
        public string[] MeshAssetPaths = Array.Empty<string>();
        public string[] MeshHashes = Array.Empty<string>();
        public int RendererCount;
        public int BoneCount;
        public int VertexCount;
        public int WeightedVertexCount;
        public int UnweightedVertexCount;
        public int InvalidWeightCount;
        public int MaximumInfluences;
        public float AverageInfluences;
        public readonly List<RigDiagnosticIssue> Issues = new List<RigDiagnosticIssue>();

        public bool IsValid => Issues.All(issue => issue.Severity != RigDiagnosticSeverity.Error);
        public int ErrorCount => Issues.Count(issue => issue.Severity == RigDiagnosticSeverity.Error);
        public int WarningCount => Issues.Count(issue => issue.Severity == RigDiagnosticSeverity.Warning);

        internal JObject ToJson() => new JObject
        {
            ["rootName"] = RootName,
            ["rigType"] = RigType,
            ["hierarchyHash"] = HierarchyHash,
            ["bindPoseHash"] = BindPoseHash,
            ["skinWeightHash"] = SkinWeightHash,
            ["stateHash"] = StateHash,
            ["meshAssetPaths"] = new JArray(MeshAssetPaths),
            ["meshHashes"] = new JArray(MeshHashes),
            ["rendererCount"] = RendererCount,
            ["boneCount"] = BoneCount,
            ["vertexCount"] = VertexCount,
            ["weightedVertexCount"] = WeightedVertexCount,
            ["unweightedVertexCount"] = UnweightedVertexCount,
            ["invalidWeightCount"] = InvalidWeightCount,
            ["maximumInfluences"] = MaximumInfluences,
            ["averageInfluences"] = AverageInfluences,
            ["valid"] = IsValid,
            ["errors"] = ErrorCount,
            ["warnings"] = WarningCount,
            ["issues"] = new JArray(Issues.Select(issue => issue.ToJson())),
        };
    }

    public static class RigTargetResolver
    {
        public static RigTargetContext Resolve(GameObject selected)
        {
            if (selected == null) return new RigTargetContext();
            var animator = selected.GetComponentInParent<Animator>();
            var ikBinding = selected.GetComponentInParent<RigIkBinding>();
            GameObject root = animator != null
                ? animator.gameObject
                : ikBinding != null && ikBinding.CharacterRoot != null ? ikBinding.CharacterRoot.gameObject : selected;
            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(renderer => renderer != null && renderer.sharedMesh != null)
                .ToArray();
            var selectedRenderer = selected.GetComponent<SkinnedMeshRenderer>();
            if (animator == null && selectedRenderer != null && selectedRenderer.sharedMesh != null)
            {
                root = CharacterRoot(selectedRenderer).gameObject;
                animator = root.GetComponentInChildren<Animator>(true);
                renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(value => value != null && value.sharedMesh != null).ToArray();
            }
            if (renderers.Length == 0)
            {
                var selectedTransform = selected.transform;
                var match = UnityObjectIds.FindAll<SkinnedMeshRenderer>(FindObjectsInactive.Include)
                    .FirstOrDefault(value => value != null && value.sharedMesh != null && value.gameObject.scene.IsValid()
                        && (value.rootBone == selectedTransform || (value.bones ?? Array.Empty<Transform>()).Contains(selectedTransform)));
                if (match != null)
                {
                    root = CharacterRoot(match).gameObject;
                    animator = root.GetComponentInChildren<Animator>(true);
                    renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(value => value != null && value.sharedMesh != null).ToArray();
                }
            }

            var uniqueBones = new List<Transform>();
            var seen = new HashSet<int>();
            for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                var bones = renderers[rendererIndex].bones ?? Array.Empty<Transform>();
                for (int boneIndex = 0; boneIndex < bones.Length; boneIndex++)
                    if (bones[boneIndex] != null && seen.Add(bones[boneIndex].GetMcpInstanceId())) uniqueBones.Add(bones[boneIndex]);
            }

            string rigType = animator != null && animator.avatar != null && animator.avatar.isHuman
                ? "Humanoid"
                : uniqueBones.Count > 0 ? "Generic" : "None";
            string hierarchyHash = ComputeHierarchyHash(root != null ? root.transform : null, uniqueBones);
            string bindPoseHash = ComputeBindPoseHash(root != null ? root.transform : null, renderers);
            string skinWeightHash = ComputeSkinWeightHash(root != null ? root.transform : null, renderers);
            return new RigTargetContext
            {
                Root = root,
                Animator = animator,
                Renderers = renderers,
                Bones = uniqueBones.ToArray(),
                RendererPaths = renderers.Select(value => RelativePath(value.transform, root != null ? root.transform : null)).ToArray(),
                MeshAssetPaths = renderers.Select(value => AssetDatabase.GetAssetPath(value.sharedMesh) ?? string.Empty).ToArray(),
                MeshHashes = renderers.Select(value => ComputeMeshHash(value.sharedMesh)).ToArray(),
                RigType = rigType,
                HierarchyHash = hierarchyHash,
                BindPoseHash = bindPoseHash,
                SkinWeightHash = skinWeightHash,
                StateHash = HashText(hierarchyHash + "|" + bindPoseHash + "|" + skinWeightHash),
            };
        }

        public static string RelativePath(Transform transform, Transform root)
        {
            if (transform == null) return "<null>";
            if (root == null) return transform.name;
            if (transform == root) return string.Empty;
            var names = new Stack<string>();
            var current = transform;
            while (current != null && current != root)
            {
                names.Push(current.name);
                current = current.parent;
            }
            if (current != root) return "<outside>/" + transform.name;
            return string.Join("/", names);
        }

        private static Transform CharacterRoot(SkinnedMeshRenderer renderer)
        {
            var animator = renderer.GetComponentInParent<Animator>();
            if (animator != null) return animator.transform;
            if (renderer.rootBone == null) return renderer.transform;
            var ancestors = new HashSet<int>();
            for (var value = renderer.transform; value != null; value = value.parent) ancestors.Add(value.GetMcpInstanceId());
            for (var value = renderer.rootBone; value != null; value = value.parent)
                if (ancestors.Contains(value.GetMcpInstanceId())) return value;
            return renderer.transform;
        }

        internal static string ComputeHierarchyHash(Transform root, IReadOnlyList<Transform> bones)
        {
            using (var sha = SHA256.Create())
            {
                var builder = new StringBuilder();
                for (int i = 0; i < bones.Count; i++)
                {
                    var bone = bones[i];
                    builder.Append(i).Append(':').Append(RelativePath(bone, root)).Append('|');
                }
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                var result = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++) result.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        internal static string ComputeBindPoseHash(Transform root, IReadOnlyList<SkinnedMeshRenderer> renderers)
        {
            var builder = new StringBuilder();
            for (int rendererIndex = 0; rendererIndex < renderers.Count; rendererIndex++)
            {
                var renderer = renderers[rendererIndex];
                builder.Append(RelativePath(renderer.transform, root)).Append('|');
                var bindPoses = renderer.sharedMesh != null ? renderer.sharedMesh.bindposes : Array.Empty<Matrix4x4>();
                for (int pose = 0; pose < bindPoses.Length; pose++) Append(builder, bindPoses[pose]);
            }
            return HashText(builder.ToString());
        }

        internal static string ComputeSkinWeightHash(Transform root, IReadOnlyList<SkinnedMeshRenderer> renderers)
        {
            var builder = new StringBuilder();
            for (int rendererIndex = 0; rendererIndex < renderers.Count; rendererIndex++)
            {
                var renderer = renderers[rendererIndex];
                builder.Append(RelativePath(renderer.transform, root)).Append('|');
                var mesh = renderer.sharedMesh;
                if (mesh == null) continue;
                using (var counts = mesh.GetBonesPerVertex())
                using (var weights = mesh.GetAllBoneWeights())
                {
                    for (int i = 0; i < counts.Length; i++) builder.Append((int)counts[i]).Append(',');
                    builder.Append('|');
                    for (int i = 0; i < weights.Length; i++)
                        builder.Append(weights[i].boneIndex).Append(':').Append(weights[i].weight.ToString("R", CultureInfo.InvariantCulture)).Append(',');
                }
            }
            return HashText(builder.ToString());
        }

        internal static string ComputeMeshHash(Mesh mesh)
        {
            if (mesh == null) return string.Empty;
            var builder = new StringBuilder();
            builder.Append(mesh.vertexCount).Append('|').Append(mesh.subMeshCount).Append('|');
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++) Append(builder, vertices[i]);
            var triangles = mesh.triangles;
            for (int i = 0; i < triangles.Length; i++) builder.Append(triangles[i]).Append(',');
            return HashText(builder.ToString());
        }

        internal static string HashText(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var result = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++) result.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        private static void Append(StringBuilder builder, Vector3 value)
            => builder.Append(value.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(value.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(value.z.ToString("R", CultureInfo.InvariantCulture)).Append(';');

        private static void Append(StringBuilder builder, Matrix4x4 value)
        {
            for (int i = 0; i < 16; i++) builder.Append(value[i].ToString("R", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(';');
        }

    }

    public static class RigInspectorService
    {
        private static readonly HumanBodyBones[] RequiredHumanoidBones =
        {
            HumanBodyBones.Hips,
            HumanBodyBones.Head,
            HumanBodyBones.LeftUpperArm,
            HumanBodyBones.LeftLowerArm,
            HumanBodyBones.RightUpperArm,
            HumanBodyBones.RightLowerArm,
            HumanBodyBones.LeftUpperLeg,
            HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.RightUpperLeg,
            HumanBodyBones.RightLowerLeg,
        };

        public static RigDiagnosticReport Inspect(GameObject selected)
            => Inspect(RigTargetResolver.Resolve(selected));

        public static RigDiagnosticReport Inspect(RigTargetContext context)
        {
            var report = new RigDiagnosticReport
            {
                RootName = context?.Root != null ? context.Root.name : string.Empty,
                RigType = context?.RigType ?? "None",
                HierarchyHash = context?.HierarchyHash ?? string.Empty,
                BindPoseHash = context?.BindPoseHash ?? string.Empty,
                SkinWeightHash = context?.SkinWeightHash ?? string.Empty,
                StateHash = context?.StateHash ?? string.Empty,
                MeshAssetPaths = context?.MeshAssetPaths ?? Array.Empty<string>(),
                MeshHashes = context?.MeshHashes ?? Array.Empty<string>(),
                RendererCount = context?.Renderers?.Length ?? 0,
                BoneCount = context?.Bones?.Length ?? 0,
            };
            if (context == null || context.Root == null)
            {
                Error(report, "RIG_TARGET_MISSING", "Selecione o root do personagem ou um SkinnedMeshRenderer.");
                return report;
            }
            if (context.Renderers.Length == 0)
            {
                Error(report, "SKINNED_RENDERER_MISSING", "Nenhum SkinnedMeshRenderer com mesh foi encontrado.", context.Root.name);
                return report;
            }

            InspectAvatar(context, report);
            var influenceTotal = 0L;
            for (int i = 0; i < context.Renderers.Length; i++)
                InspectRenderer(context, context.Renderers[i], report, ref influenceTotal);
            if (report.VertexCount > 0) report.AverageInfluences = influenceTotal / (float)report.VertexCount;
            if (report.UnweightedVertexCount > 0)
                Warning(report, "UNWEIGHTED_VERTICES", $"Foram encontrados {report.UnweightedVertexCount} vértices sem influência.", context.Root.name);
            InspectBones(context, report);
            return report;
        }

        public static float[] ComputeInfluenceHeatmap(Mesh mesh, int boneIndex)
        {
            if (mesh == null) return Array.Empty<float>();
            var result = new float[mesh.vertexCount];
            using (var counts = mesh.GetBonesPerVertex())
            using (var weights = mesh.GetAllBoneWeights())
            {
                int offset = 0;
                for (int vertex = 0; vertex < counts.Length; vertex++)
                {
                    int count = counts[vertex];
                    float sum = 0f;
                    for (int influence = 0; influence < count && offset + influence < weights.Length; influence++)
                        if (weights[offset + influence].boneIndex == boneIndex) sum += weights[offset + influence].weight;
                    result[vertex] = Mathf.Clamp01(sum);
                    offset += count;
                }
            }
            return result;
        }

        private static void InspectAvatar(RigTargetContext context, RigDiagnosticReport report)
        {
            var animator = context.Animator;
            if (animator == null)
            {
                Warning(report, "ANIMATOR_MISSING", "O personagem não possui Animator no root.", context.Root.name);
                return;
            }
            if (animator.avatar == null)
            {
                Warning(report, "AVATAR_MISSING", "O Animator não possui Avatar.", RigTargetResolver.RelativePath(animator.transform, context.Root.transform));
                return;
            }
            if (!animator.avatar.isValid)
                Error(report, "AVATAR_INVALID", "O Avatar associado não é válido.", animator.avatar.name);
            if (!animator.avatar.isHuman) return;
            for (int i = 0; i < RequiredHumanoidBones.Length; i++)
                if (animator.GetBoneTransform(RequiredHumanoidBones[i]) == null)
                    Error(report, "HUMANOID_BONE_MISSING", $"Bone Humanoid obrigatório ausente: {RequiredHumanoidBones[i]}.", animator.avatar.name);
        }

        private static void InspectRenderer(
            RigTargetContext context,
            SkinnedMeshRenderer renderer,
            RigDiagnosticReport report,
            ref long influenceTotal)
        {
            string rendererPath = RigTargetResolver.RelativePath(renderer.transform, context.Root.transform);
            var mesh = renderer.sharedMesh;
            if (renderer.rootBone == null) Error(report, "ROOT_BONE_MISSING", "O SkinnedMeshRenderer não possui rootBone.", rendererPath);
            var bones = renderer.bones ?? Array.Empty<Transform>();
            var bindPoses = mesh.bindposes ?? Array.Empty<Matrix4x4>();
            if (bones.Length != bindPoses.Length)
                Error(report, "BINDPOSE_COUNT_MISMATCH", $"Renderer possui {bones.Length} bones e {bindPoses.Length} bind poses.", rendererPath);
            if (!Finite(mesh.bounds)) Error(report, "BOUNDS_INVALID", "Bounds da mesh contém valores não finitos.", rendererPath);
            else if (mesh.vertexCount > 0 && mesh.bounds.size.sqrMagnitude <= 1e-12f)
                Warning(report, "BOUNDS_EMPTY", "Bounds da mesh é vazio apesar de possuir vértices.", rendererPath);
            InspectBlendShapes(mesh, report, rendererPath);

            report.VertexCount += mesh.vertexCount;
            using (var counts = mesh.GetBonesPerVertex())
            using (var weights = mesh.GetAllBoneWeights())
            {
                if (counts.Length != mesh.vertexCount)
                {
                    Error(report, "BONE_COUNT_CHANNEL_MISMATCH", $"Canal bones-per-vertex possui {counts.Length} entradas para {mesh.vertexCount} vértices.", rendererPath);
                    return;
                }
                int offset = 0;
                for (int vertex = 0; vertex < counts.Length; vertex++)
                {
                    int count = counts[vertex];
                    influenceTotal += count;
                    report.MaximumInfluences = Mathf.Max(report.MaximumInfluences, count);
                    if (count == 0)
                    {
                        report.UnweightedVertexCount++;
                        continue;
                    }
                    report.WeightedVertexCount++;
                    float sum = 0f;
                    for (int influence = 0; influence < count; influence++)
                    {
                        if (offset + influence >= weights.Length)
                        {
                            report.InvalidWeightCount++;
                            Error(report, "BONE_WEIGHT_CHANNEL_TRUNCATED", $"Pesos terminam durante o vértice {vertex}.", rendererPath);
                            break;
                        }
                        var weight = weights[offset + influence];
                        if (!Finite(weight.weight) || weight.weight < 0f)
                        {
                            report.InvalidWeightCount++;
                            Error(report, "BONE_WEIGHT_INVALID", $"Vértice {vertex} possui peso inválido {weight.weight:R}.", rendererPath);
                            continue;
                        }
                        if (weight.boneIndex < 0 || weight.boneIndex >= bones.Length)
                        {
                            report.InvalidWeightCount++;
                            Error(report, "BONE_INDEX_OUT_OF_RANGE", $"Vértice {vertex} referencia bone {weight.boneIndex}, mas o renderer possui {bones.Length} bones.", rendererPath);
                        }
                        sum += weight.weight;
                    }
                    if (Mathf.Abs(sum - 1f) > 2e-4f)
                    {
                        report.InvalidWeightCount++;
                        Warning(report, "BONE_WEIGHT_NOT_NORMALIZED", $"Vértice {vertex} soma {sum:R} em pesos.", rendererPath);
                    }
                    offset += count;
                }
                if (offset != weights.Length)
                    Warning(report, "BONE_WEIGHT_UNUSED_DATA", $"Canal contém {weights.Length - offset} influência(s) além do esperado.", rendererPath);
            }
        }

        private static void InspectBlendShapes(Mesh mesh, RigDiagnosticReport report, string rendererPath)
        {
            if (mesh == null || mesh.blendShapeCount == 0 || mesh.vertexCount == 0) return;
            var deltaVertices = new Vector3[mesh.vertexCount]; var deltaNormals = new Vector3[mesh.vertexCount]; var deltaTangents = new Vector3[mesh.vertexCount];
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                int frames = mesh.GetBlendShapeFrameCount(shape);
                if (frames == 0) Warning(report, "BLEND_SHAPE_EMPTY", $"Blend shape {shape} não possui frames.", rendererPath);
                for (int frame = 0; frame < frames; frame++)
                {
                    mesh.GetBlendShapeFrameVertices(shape, frame, deltaVertices, deltaNormals, deltaTangents);
                    for (int vertex = 0; vertex < mesh.vertexCount; vertex++)
                        if (!Finite(deltaVertices[vertex]) || !Finite(deltaNormals[vertex]) || !Finite(deltaTangents[vertex]))
                        {
                            Error(report, "BLEND_SHAPE_NON_FINITE", $"Blend shape {shape}, frame {frame}, vértice {vertex} contém delta não finito.", rendererPath);
                            return;
                        }
                }
            }
        }

        private static void InspectBones(RigTargetContext context, RigDiagnosticReport report)
        {
            var seen = new HashSet<int>();
            for (int rendererIndex = 0; rendererIndex < context.Renderers.Length; rendererIndex++)
            {
                var renderer = context.Renderers[rendererIndex];
                var bones = renderer.bones ?? Array.Empty<Transform>();
                for (int boneIndex = 0; boneIndex < bones.Length; boneIndex++)
                {
                    var bone = bones[boneIndex];
                    if (bone == null)
                    {
                        Error(report, "NULL_BONE", $"Bone {boneIndex} está nulo.", renderer.name);
                        continue;
                    }
                    string path = RigTargetResolver.RelativePath(bone, context.Root.transform);
                    if (!seen.Add(bone.GetMcpInstanceId()))
                        Info(report, "SHARED_BONE", "Bone é compartilhado por mais de um renderer ou slot.", path);
                    if (path.StartsWith("<outside>", StringComparison.Ordinal))
                        Error(report, "BONE_OUTSIDE_ROOT", "Bone está fora da hierarquia do personagem.", path);
                    var scale = bone.lossyScale;
                    if (!Finite(scale) || Mathf.Abs(scale.x) <= 1e-8f || Mathf.Abs(scale.y) <= 1e-8f || Mathf.Abs(scale.z) <= 1e-8f)
                        Error(report, "BONE_SCALE_INVALID", "Bone possui escala zero ou não finita.", path);
                    else
                    {
                        if (scale.x < 0f || scale.y < 0f || scale.z < 0f)
                            Warning(report, "BONE_NEGATIVE_SCALE", "Bone possui escala negativa.", path);
                        float minimum = Mathf.Min(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                        float maximum = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                        if (minimum > 1e-8f && maximum / minimum > 1.01f)
                            Warning(report, "BONE_NON_UNIFORM_SCALE", "Bone possui escala mundial não uniforme.", path);
                    }
                }
            }
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(Bounds bounds) => Finite(bounds.center) && Finite(bounds.size);
        private static void Info(RigDiagnosticReport report, string code, string message, string path = null)
            => report.Issues.Add(new RigDiagnosticIssue(code, RigDiagnosticSeverity.Info, message, path));
        private static void Warning(RigDiagnosticReport report, string code, string message, string path = null)
            => report.Issues.Add(new RigDiagnosticIssue(code, RigDiagnosticSeverity.Warning, message, path));
        private static void Error(RigDiagnosticReport report, string code, string message, string path = null)
            => report.Issues.Add(new RigDiagnosticIssue(code, RigDiagnosticSeverity.Error, message, path));
    }
}
