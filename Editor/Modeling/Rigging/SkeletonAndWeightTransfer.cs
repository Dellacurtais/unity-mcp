using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using Maicon.SceneAnalyzer.Modeling;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Rigging
{
    public sealed class WeightTransferReport
    {
        public int VertexCount;
        public int ExactVertexMatches;
        public int SurfaceMatches;
        public int UnmappedBoneCount;
        public float MaximumDistance;
        public float MeanDistance;
        public int PreservedVertexCount;
        public string BeforeHash;
        public string AfterHash;
        public bool IsValid => VertexCount > 0 && ExactVertexMatches + SurfaceMatches + PreservedVertexCount == VertexCount && UnmappedBoneCount == 0;
    }

    [Serializable]
    public sealed class WeightTransferOptions
    {
        public float MaximumDistance = float.PositiveInfinity;
        public int MaximumInfluences = 8;
        public float PruneThreshold = 1e-6f;
        public bool MirrorRegionX;
        public HashSet<int> IncludedVertices;
    }

    public sealed class BonePruneReport
    {
        public int OriginalBoneCount;
        public int RemovedBoneCount;
        public string[] RemovedBonePaths = Array.Empty<string>();
    }

    public static class SkeletonEditorService
    {
        public static Transform CreateBone(Transform parent, string name, Vector3 localPosition)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            var gameObject = new GameObject(string.IsNullOrWhiteSpace(name) ? "Bone" : name.Trim());
            Undo.RegisterCreatedObjectUndo(gameObject, "Create bone");
            Undo.SetTransformParent(gameObject.transform, parent, "Parent bone");
            gameObject.transform.localPosition = localPosition;
            gameObject.transform.localRotation = Quaternion.identity;
            return gameObject.transform;
        }

        public static Transform DuplicateBone(Transform source, bool includeChildren)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var clone = UnityEngine.Object.Instantiate(source.gameObject, source.parent, false);
            clone.name = source.name + "_Copy";
            if (!includeChildren)
                while (clone.transform.childCount > 0) UnityEngine.Object.DestroyImmediate(clone.transform.GetChild(0).gameObject);
            Undo.RegisterCreatedObjectUndo(clone, "Duplicate bone");
            return clone.transform;
        }

        public static void RenameBone(Transform bone, string name)
        {
            if (bone == null) throw new ArgumentNullException(nameof(bone));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("BONE_NAME_REQUIRED", nameof(name));
            Undo.RecordObject(bone.gameObject, "Rename bone"); bone.name = name.Trim(); EditorUtility.SetDirty(bone.gameObject);
        }

        public static void ReparentBone(Transform bone, Transform parent, bool preserveWorldPose = true)
        {
            if (bone == null || parent == null) throw new ArgumentNullException();
            if (parent == bone || parent.IsChildOf(bone)) throw new InvalidOperationException("BONE_PARENT_CYCLE: a bone cannot be parented to itself or its descendant.");
            Undo.SetTransformParent(bone, parent, "Reparent bone");
            if (!preserveWorldPose) { Undo.RecordObject(bone, "Reset reparented bone"); bone.localPosition = Vector3.zero; bone.localRotation = Quaternion.identity; }
        }

        public static void MoveBone(Transform bone, Vector3 localPosition, Quaternion localRotation)
        {
            if (bone == null) throw new ArgumentNullException(nameof(bone));
            Undo.RecordObject(bone, "Move bone"); bone.localPosition = localPosition; bone.localRotation = localRotation;
        }

        public static void SetRootBone(SkinnedMeshRenderer renderer, Transform rootBone)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            Undo.RecordObject(renderer, "Set root bone"); renderer.rootBone = rootBone; EditorUtility.SetDirty(renderer);
        }

        public static BonePruneReport RemoveUnusedBones(SkinnedMeshRenderer renderer, GameObject character, IReadOnlyList<AnimationClip> proofClips)
        {
            if (renderer == null || renderer.sharedMesh == null) throw new ArgumentNullException(nameof(renderer));
            RigAssetSafety.RequireEditable(renderer.sharedMesh);
            var bones = renderer.bones ?? Array.Empty<Transform>();
            var used = new HashSet<int>(SkinWeightTable.Read(renderer.sharedMesh).Vertices.SelectMany(value => value.Keys));
            var animated = new HashSet<string>(StringComparer.Ordinal);
            if (proofClips != null && character != null)
                for (int clipIndex = 0; clipIndex < proofClips.Count; clipIndex++)
                {
                    var clip = proofClips[clipIndex]; if (clip == null) continue;
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip)) animated.Add(binding.path ?? string.Empty);
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip)) animated.Add(binding.path ?? string.Empty);
                }
            var keep = new List<int>(); var removed = new List<string>();
            for (int boneIndex = 0; boneIndex < bones.Length; boneIndex++)
            {
                string path = bones[boneIndex] != null && character != null ? RigTargetResolver.RelativePath(bones[boneIndex], character.transform) : string.Empty;
                bool referencedByClip = animated.Any(value => value == path || value.StartsWith(path + "/", StringComparison.Ordinal));
                if (used.Contains(boneIndex) || referencedByClip || bones[boneIndex] == renderer.rootBone) keep.Add(boneIndex);
                else removed.Add(path);
            }
            if (removed.Count == 0) return new BonePruneReport { OriginalBoneCount = bones.Length };
            var map = Enumerable.Repeat(-1, bones.Length).ToArray(); for (int i = 0; i < keep.Count; i++) map[keep[i]] = i;
            var table = SkinWeightTable.Read(renderer.sharedMesh);
            for (int vertex = 0; vertex < table.Vertices.Count; vertex++)
            {
                var replacement = new Dictionary<int, float>();
                foreach (var pair in table.Vertices[vertex]) if (pair.Key >= 0 && pair.Key < map.Length && map[pair.Key] >= 0) replacement[map[pair.Key]] = pair.Value;
                table.Vertices[vertex].Clear(); foreach (var pair in replacement) table.Vertices[vertex][pair.Key] = pair.Value;
                table.NormalizeAndLimit(vertex, byte.MaxValue, 1e-8f);
            }
            Undo.RegisterCompleteObjectUndo(renderer.sharedMesh, "Remove unused skin bones");
            Undo.RecordObject(renderer, "Remove unused skin bones");
            renderer.bones = keep.Select(index => bones[index]).ToArray();
            table.Write(renderer.sharedMesh); renderer.sharedMesh.bindposes = BindPoseService.Calculate(renderer); EditorUtility.SetDirty(renderer); EditorUtility.SetDirty(renderer.sharedMesh);
            return new BonePruneReport { OriginalBoneCount = bones.Length, RemovedBoneCount = removed.Count, RemovedBonePaths = removed.ToArray() };
        }
    }

    public static class BindPoseService
    {
        public static Matrix4x4[] Calculate(SkinnedMeshRenderer renderer)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            return (renderer.bones ?? Array.Empty<Transform>()).Select(bone => bone != null
                ? bone.worldToLocalMatrix * renderer.transform.localToWorldMatrix
                : Matrix4x4.identity).ToArray();
        }

        public static float Rebuild(SkinnedMeshRenderer renderer, float worldTolerance = 1e-4f)
        {
            if (renderer == null || renderer.sharedMesh == null) throw new ArgumentNullException(nameof(renderer));
            RigAssetSafety.RequireEditable(renderer.sharedMesh);
            if ((renderer.bones ?? Array.Empty<Transform>()).Any(bone => bone == null))
                throw new InvalidOperationException("NULL_BONE: all renderer bones must resolve before rebuilding bind poses.");
            var beforeMesh = new Mesh(); var afterMesh = new Mesh();
            Undo.IncrementCurrentGroup(); int undoGroup = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Rebuild bind poses");
            try
            {
                renderer.BakeMesh(beforeMesh);
                Undo.RegisterCompleteObjectUndo(renderer.sharedMesh, "Rebuild bind poses");
                renderer.sharedMesh.bindposes = Calculate(renderer);
                renderer.BakeMesh(afterMesh);
                float maximum = MaximumWorldDelta(renderer, beforeMesh.vertices, afterMesh.vertices);
                if (!float.IsNaN(maximum) && maximum > Mathf.Max(0f, worldTolerance))
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                    throw new InvalidOperationException($"BINDPOSE_WORLD_GEOMETRY_CHANGED: maximum delta {maximum:R} exceeds {worldTolerance:R}.");
                }
                EditorUtility.SetDirty(renderer.sharedMesh);
                Undo.CollapseUndoOperations(undoGroup);
                return maximum;
            }
            catch
            {
                if (Undo.GetCurrentGroup() >= undoGroup) Undo.RevertAllDownToGroup(undoGroup);
                throw;
            }
            finally { UnityEngine.Object.DestroyImmediate(beforeMesh); UnityEngine.Object.DestroyImmediate(afterMesh); }
        }

        public static RigBindPoseSnapshot Capture(SkinnedMeshRenderer renderer, GameObject character, string outputFolder = null)
        {
            if (renderer == null || renderer.sharedMesh == null || character == null) throw new ArgumentNullException();
            var context = RigTargetResolver.Resolve(character);
            var snapshot = ScriptableObject.CreateInstance<RigBindPoseSnapshot>(); snapshot.name = renderer.sharedMesh.name + "_BindPose";
            snapshot.CharacterStateHash = context.StateHash; snapshot.HierarchyHash = context.HierarchyHash;
            snapshot.RendererPath = RigTargetResolver.RelativePath(renderer.transform, context.Root.transform);
            snapshot.RootBonePath = RigTargetResolver.RelativePath(renderer.rootBone, context.Root.transform);
            snapshot.BonePaths.AddRange((renderer.bones ?? Array.Empty<Transform>()).Select(value => RigTargetResolver.RelativePath(value, context.Root.transform)));
            snapshot.BindPoses.AddRange(renderer.sharedMesh.bindposes ?? Array.Empty<Matrix4x4>()); snapshot.CreatedUtc = DateTime.UtcNow.ToString("O");
            var folder = AssetModelerAssetPaths.ValidateAndEnsureFolder(string.IsNullOrWhiteSpace(outputFolder) ? "Assets/AssetModeler/Rigging/BindPoses" : outputFolder);
            RigAssetSafety.CreateAssetWithUndo(snapshot, AssetModelerAssetPaths.UniqueAssetPath(folder, snapshot.name, ".asset"), "Capture bind pose snapshot");
            return snapshot;
        }

        public static void Restore(SkinnedMeshRenderer renderer, GameObject character, RigBindPoseSnapshot snapshot)
        {
            if (renderer == null || renderer.sharedMesh == null || character == null || snapshot == null) throw new ArgumentNullException();
            RigAssetSafety.RequireEditable(renderer.sharedMesh);
            var context = RigTargetResolver.Resolve(character);
            if (!string.Equals(context.HierarchyHash, snapshot.HierarchyHash, StringComparison.Ordinal)) throw new InvalidOperationException("BINDPOSE_SNAPSHOT_HIERARCHY_CHANGED");
            if (snapshot.BindPoses.Count != snapshot.BonePaths.Count) throw new InvalidOperationException("BINDPOSE_SNAPSHOT_INVALID");
            var bones = snapshot.BonePaths.Select(path => string.IsNullOrEmpty(path) ? context.Root.transform : context.Root.transform.Find(path)).ToArray();
            if (bones.Any(value => value == null)) throw new InvalidOperationException("BINDPOSE_SNAPSHOT_BONE_MISSING");
            Undo.RecordObject(renderer, "Restore bind pose snapshot"); Undo.RegisterCompleteObjectUndo(renderer.sharedMesh, "Restore bind pose snapshot");
            renderer.bones = bones; renderer.rootBone = string.IsNullOrEmpty(snapshot.RootBonePath) ? null : context.Root.transform.Find(snapshot.RootBonePath);
            renderer.sharedMesh.bindposes = snapshot.BindPoses.ToArray(); EditorUtility.SetDirty(renderer); EditorUtility.SetDirty(renderer.sharedMesh);
        }

        private static float MaximumWorldDelta(SkinnedMeshRenderer renderer, IReadOnlyList<Vector3> before, IReadOnlyList<Vector3> after)
        {
            if (before.Count != after.Count) return float.PositiveInfinity;
            float maximum = 0f;
            for (int i = 0; i < before.Count; i++) maximum = Mathf.Max(maximum, Vector3.Distance(renderer.transform.TransformPoint(before[i]), renderer.transform.TransformPoint(after[i])));
            return maximum;
        }
    }

    public static class SkinWeightTransferService
    {
        public static WeightTransferReport Transfer(SkinnedMeshRenderer source, SkinnedMeshRenderer destination, float maximumDistance = float.PositiveInfinity)
            => Transfer(source, destination, new WeightTransferOptions { MaximumDistance = maximumDistance });

        public static WeightTransferReport Transfer(SkinnedMeshRenderer source, SkinnedMeshRenderer destination, WeightTransferOptions options)
        {
            if (source == null || source.sharedMesh == null) throw new ArgumentNullException(nameof(source));
            if (destination == null || destination.sharedMesh == null) throw new ArgumentNullException(nameof(destination));
            options = options ?? new WeightTransferOptions();
            RigAssetSafety.RequireEditable(destination.sharedMesh);
            string beforeHash = RigTargetResolver.ComputeSkinWeightHash(destination.transform, new[] { destination });
            var sourceBones = source.bones ?? Array.Empty<Transform>();
            if ((destination.bones ?? Array.Empty<Transform>()).Length == 0)
            {
                Undo.RecordObject(destination, "Assign transferred skeleton");
                destination.bones = sourceBones; destination.rootBone = source.rootBone;
            }
            var destinationBones = destination.bones ?? Array.Empty<Transform>();
            var boneMap = BuildBoneMap(sourceBones, destinationBones);
            var usedBones = SkinWeightTable.Read(source.sharedMesh).Vertices.SelectMany(weights => weights.Keys).Distinct().ToArray();
            var unmapped = usedBones.Where(index => index < 0 || index >= boneMap.Length || boneMap[index] < 0).ToArray();
            if (unmapped.Length > 0) throw new InvalidOperationException("WEIGHT_TRANSFER_UNMAPPED_BONES: " + string.Join(",", unmapped));

            var sourceTable = SkinWeightTable.Read(source.sharedMesh);
            var destinationTable = SkinWeightTable.Read(destination.sharedMesh);
            var sourceVertices = source.sharedMesh.vertices;
            var destinationVertices = destination.sharedMesh.vertices;
            var sourceTriangles = source.sharedMesh.triangles;
            var report = new WeightTransferReport { VertexCount = destinationVertices.Length, BeforeHash = beforeHash };
            double distanceSum = 0d;
            var included = ExpandRegion(destinationVertices, options.IncludedVertices, options.MirrorRegionX, destination.sharedMesh.bounds);
            bool exactTopology = sourceVertices.Length == destinationVertices.Length
                && source.sharedMesh.triangles.SequenceEqual(destination.sharedMesh.triangles);
            var surfaceIndex = exactTopology ? null : new TriangleBvh(source, sourceVertices, sourceTriangles);
            for (int vertex = 0; vertex < destinationVertices.Length; vertex++)
            {
                if (included != null && !included.Contains(vertex)) { report.PreservedVertexCount++; continue; }
                destinationTable.Vertices[vertex].Clear();
                Vector3 world = destination.transform.TransformPoint(destinationVertices[vertex]);
                if (exactTopology)
                {
                    CopyWeights(sourceTable.Vertices[vertex], destinationTable.Vertices[vertex], boneMap, 1f);
                    float distance = Vector3.Distance(world, source.transform.TransformPoint(sourceVertices[vertex]));
                    report.MaximumDistance = Mathf.Max(report.MaximumDistance, distance); distanceSum += distance; report.ExactVertexMatches++;
                    continue;
                }
                surfaceIndex.FindClosest(world, out int a, out int b, out int c, out Vector3 barycentric, out float surfaceDistance);
                if (a < 0 || surfaceDistance > options.MaximumDistance) throw new InvalidOperationException($"WEIGHT_TRANSFER_COVERAGE_FAILED: vertex {vertex} has no source surface within {options.MaximumDistance:R}.");
                CopyWeights(sourceTable.Vertices[a], destinationTable.Vertices[vertex], boneMap, barycentric.x);
                CopyWeights(sourceTable.Vertices[b], destinationTable.Vertices[vertex], boneMap, barycentric.y);
                CopyWeights(sourceTable.Vertices[c], destinationTable.Vertices[vertex], boneMap, barycentric.z);
                report.MaximumDistance = Mathf.Max(report.MaximumDistance, surfaceDistance); distanceSum += surfaceDistance; report.SurfaceMatches++;
            }
            for (int vertex = 0; vertex < destinationTable.Vertices.Count; vertex++)
                destinationTable.NormalizeAndLimit(vertex, options.MaximumInfluences, options.PruneThreshold);
            Undo.RegisterCompleteObjectUndo(destination.sharedMesh, "Transfer skin weights");
            destinationTable.Write(destination.sharedMesh);
            destination.sharedMesh.bindposes = BindPoseService.Calculate(destination);
            EditorUtility.SetDirty(destination.sharedMesh);
            int matchedCount = report.ExactVertexMatches + report.SurfaceMatches;
            report.MeanDistance = matchedCount > 0 ? (float)(distanceSum / matchedCount) : 0f;
            report.AfterHash = RigTargetResolver.ComputeSkinWeightHash(destination.transform, new[] { destination });
            return report;
        }

        private static int[] BuildBoneMap(Transform[] source, Transform[] destination)
        {
            var map = Enumerable.Repeat(-1, source.Length).ToArray();
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] == null) continue;
                for (int j = 0; j < destination.Length; j++) if (source[i] == destination[j]) { map[i] = j; break; }
                if (map[i] >= 0) continue;
                string name = Normalize(source[i].name);
                var matches = Enumerable.Range(0, destination.Length).Where(j => destination[j] != null && Normalize(destination[j].name) == name).ToArray();
                if (matches.Length == 1) map[i] = matches[0];
            }
            return map;
        }

        private static string Normalize(string value) => new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray())
            .Replace("left", "l").Replace("right", "r");

        private static void CopyWeights(Dictionary<int, float> source, Dictionary<int, float> destination, int[] map, float multiplier)
        {
            foreach (var pair in source)
            {
                int bone = map[pair.Key]; destination.TryGetValue(bone, out float current); destination[bone] = current + pair.Value * multiplier;
            }
        }

        private static HashSet<int> ExpandRegion(IReadOnlyList<Vector3> vertices, ISet<int> region, bool mirrorX, Bounds bounds)
        {
            if (region == null || region.Count == 0) return null;
            var result = new HashSet<int>(region.Where(value => value >= 0 && value < vertices.Count));
            if (!mirrorX) return result;
            float tolerance = Mathf.Max(bounds.size.magnitude * 1e-4f, 1e-5f);
            var cells = new Dictionary<Vector3Int, List<int>>();
            for (int i = 0; i < vertices.Count; i++)
            {
                var key = Quantize(vertices[i], tolerance); if (!cells.TryGetValue(key, out var bucket)) cells[key] = bucket = new List<int>(); bucket.Add(i);
            }
            foreach (int source in result.ToArray())
            {
                var expected = vertices[source]; expected.x = -expected.x; var center = Quantize(expected, tolerance);
                int best = -1; float distance = tolerance;
                for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                    if (cells.TryGetValue(center + new Vector3Int(x, y, z), out var bucket)) for (int i = 0; i < bucket.Count; i++)
                    { float current = Vector3.Distance(expected, vertices[bucket[i]]); if (current <= distance) { best = bucket[i]; distance = current; } }
                if (best >= 0) result.Add(best);
            }
            return result;
        }

        private static Vector3Int Quantize(Vector3 value, float cell)
            => new Vector3Int(Mathf.RoundToInt(value.x / cell), Mathf.RoundToInt(value.y / cell), Mathf.RoundToInt(value.z / cell));

        private sealed class TriangleBvh
        {
            private sealed class Node { public Bounds Bounds; public int Start; public int Count; public Node Left; public Node Right; }
            private readonly Vector3[] worldVertices;
            private readonly int[] triangles;
            private readonly int[] order;
            private readonly Node root;

            public TriangleBvh(SkinnedMeshRenderer renderer, Vector3[] vertices, int[] indices)
            {
                if (indices == null || indices.Length < 3) throw new InvalidOperationException("WEIGHT_TRANSFER_SOURCE_HAS_NO_TRIANGLES");
                worldVertices = vertices.Select(renderer.transform.TransformPoint).ToArray(); triangles = indices;
                order = Enumerable.Range(0, indices.Length / 3).ToArray(); root = Build(0, order.Length);
            }

            public void FindClosest(Vector3 point, out int bestA, out int bestB, out int bestC, out Vector3 barycentric, out float distance)
            {
                bestA = bestB = bestC = -1; barycentric = Vector3.zero; float bestSquared = float.PositiveInfinity;
                Search(root, point, ref bestA, ref bestB, ref bestC, ref barycentric, ref bestSquared); distance = Mathf.Sqrt(bestSquared);
            }

            private Node Build(int start, int count)
            {
                var node = new Node { Start = start, Count = count, Bounds = TriangleBounds(order[start]) };
                for (int i = 1; i < count; i++) node.Bounds.Encapsulate(TriangleBounds(order[start + i]));
                if (count <= 8) return node;
                int axis = node.Bounds.size.x >= node.Bounds.size.y && node.Bounds.size.x >= node.Bounds.size.z ? 0 : node.Bounds.size.y >= node.Bounds.size.z ? 1 : 2;
                Array.Sort(order, start, count, Comparer<int>.Create((a, b) => Center(a)[axis].CompareTo(Center(b)[axis])));
                int leftCount = count / 2; node.Left = Build(start, leftCount); node.Right = Build(start + leftCount, count - leftCount); node.Count = 0; return node;
            }

            private void Search(Node node, Vector3 point, ref int bestA, ref int bestB, ref int bestC, ref Vector3 bestBarycentric, ref float bestSquared)
            {
                if (node == null || SqrDistance(node.Bounds, point) > bestSquared) return;
                if (node.Count > 0)
                {
                    for (int item = 0; item < node.Count; item++)
                    {
                        int triangle = order[node.Start + item] * 3; int a = triangles[triangle], b = triangles[triangle + 1], c = triangles[triangle + 2];
                        var barycentric = ClosestBarycentric(point, worldVertices[a], worldVertices[b], worldVertices[c]);
                        var closest = worldVertices[a] * barycentric.x + worldVertices[b] * barycentric.y + worldVertices[c] * barycentric.z;
                        float squared = (point - closest).sqrMagnitude; if (squared >= bestSquared) continue;
                        bestSquared = squared; bestA = a; bestB = b; bestC = c; bestBarycentric = barycentric;
                    }
                    return;
                }
                float leftDistance = node.Left != null ? SqrDistance(node.Left.Bounds, point) : float.PositiveInfinity;
                float rightDistance = node.Right != null ? SqrDistance(node.Right.Bounds, point) : float.PositiveInfinity;
                if (leftDistance <= rightDistance) { Search(node.Left, point, ref bestA, ref bestB, ref bestC, ref bestBarycentric, ref bestSquared); Search(node.Right, point, ref bestA, ref bestB, ref bestC, ref bestBarycentric, ref bestSquared); }
                else { Search(node.Right, point, ref bestA, ref bestB, ref bestC, ref bestBarycentric, ref bestSquared); Search(node.Left, point, ref bestA, ref bestB, ref bestC, ref bestBarycentric, ref bestSquared); }
            }

            private Bounds TriangleBounds(int triangleIndex)
            {
                int index = triangleIndex * 3; var bounds = new Bounds(worldVertices[triangles[index]], Vector3.zero); bounds.Encapsulate(worldVertices[triangles[index + 1]]); bounds.Encapsulate(worldVertices[triangles[index + 2]]); return bounds;
            }
            private Vector3 Center(int triangleIndex) { int index = triangleIndex * 3; return (worldVertices[triangles[index]] + worldVertices[triangles[index + 1]] + worldVertices[triangles[index + 2]]) / 3f; }
            private static float SqrDistance(Bounds bounds, Vector3 point) { var closest = bounds.ClosestPoint(point); return (closest - point).sqrMagnitude; }
        }

        private static Vector3 ClosestBarycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a; var ac = c - a; var ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap); if (d1 <= 0f && d2 <= 0f) return new Vector3(1f, 0f, 0f);
            var bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp); if (d3 >= 0f && d4 <= d3) return new Vector3(0f, 1f, 0f);
            float vc = d1 * d4 - d3 * d2; if (vc <= 0f && d1 >= 0f && d3 <= 0f) { float v = d1 / (d1 - d3); return new Vector3(1f - v, v, 0f); }
            var cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp); if (d6 >= 0f && d5 <= d6) return new Vector3(0f, 0f, 1f);
            float vb = d5 * d2 - d1 * d6; if (vb <= 0f && d2 >= 0f && d6 <= 0f) { float w = d2 / (d2 - d6); return new Vector3(1f - w, 0f, w); }
            float va = d3 * d6 - d5 * d4; if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) { float w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); return new Vector3(0f, 1f - w, w); }
            float inverse = 1f / (va + vb + vc); float v2 = vb * inverse, w2 = vc * inverse; return new Vector3(1f - v2 - w2, v2, w2);
        }
    }
}
