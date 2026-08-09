using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Rigging
{
    public sealed class SkinWeightSpatialIndex
    {
        private sealed class Node { public Bounds Bounds; public int Start; public int Count; public Node Left; public Node Right; }
        private readonly Vector3[] vertices;
        private readonly int[] triangles;
        private readonly int[] order;
        private readonly Dictionary<Vector3Int, List<int>> vertexCells = new Dictionary<Vector3Int, List<int>>();
        private readonly float cellSize;
        private readonly Node root;
        public int VertexCount => vertices.Length;
        public int TriangleCount => triangles.Length / 3;
        public int LastTriangleTests { get; private set; }

        public SkinWeightSpatialIndex(IReadOnlyList<Vector3> sourceVertices, IReadOnlyList<int> sourceTriangles)
        {
            if (sourceVertices == null || sourceTriangles == null || sourceVertices.Count == 0 || sourceTriangles.Count < 3) throw new ArgumentException("SKIN_SPATIAL_INDEX_EMPTY");
            vertices = sourceVertices.ToArray(); triangles = sourceTriangles.ToArray(); order = Enumerable.Range(0, triangles.Length / 3).ToArray();
            var bounds = new Bounds(vertices[0], Vector3.zero); for (int i = 1; i < vertices.Length; i++) bounds.Encapsulate(vertices[i]);
            int resolution = Mathf.Clamp(Mathf.CeilToInt(Mathf.Pow(vertices.Length, 1f / 3f)), 8, 128);
            cellSize = Mathf.Max(Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z)) / resolution, 1e-5f);
            for (int vertex = 0; vertex < vertices.Length; vertex++)
            {
                var key = Cell(vertices[vertex]); if (!vertexCells.TryGetValue(key, out var bucket)) vertexCells[key] = bucket = new List<int>(); bucket.Add(vertex);
            }
            root = Build(0, order.Length);
        }

        public bool Raycast(Ray ray, bool frontFacesOnly, out float distance, out Vector3 normal)
        {
            distance = float.PositiveInfinity; normal = Vector3.zero; LastTriangleTests = 0;
            Search(root, ray, frontFacesOnly, ref distance, ref normal);
            return !float.IsInfinity(distance) && !float.IsNaN(distance);
        }

        public int[] QuerySphere(Vector3 center, float radius)
        {
            radius = Mathf.Max(radius, 1e-6f); int extent = Mathf.CeilToInt(radius / cellSize); var cell = Cell(center); float squared = radius * radius;
            long span = (long)extent * 2L + 1L;
            if (extent > 128 || span * span * span > Math.Max(4096L, vertices.Length * 4L))
                return Enumerable.Range(0, vertices.Length).Where(index => (vertices[index] - center).sqrMagnitude <= squared).ToArray();
            var result = new List<int>();
            for (int x = -extent; x <= extent; x++) for (int y = -extent; y <= extent; y++) for (int z = -extent; z <= extent; z++)
            {
                if (!vertexCells.TryGetValue(cell + new Vector3Int(x, y, z), out var bucket)) continue;
                for (int i = 0; i < bucket.Count; i++) if ((vertices[bucket[i]] - center).sqrMagnitude <= squared) result.Add(bucket[i]);
            }
            return result.ToArray();
        }

        private Node Build(int start, int count)
        {
            var node = new Node { Start = start, Count = count, Bounds = TriangleBounds(order[start]) };
            for (int i = 1; i < count; i++) node.Bounds.Encapsulate(TriangleBounds(order[start + i]));
            if (count <= 8) return node;
            int axis = node.Bounds.size.x >= node.Bounds.size.y && node.Bounds.size.x >= node.Bounds.size.z ? 0 : node.Bounds.size.y >= node.Bounds.size.z ? 1 : 2;
            Array.Sort(order, start, count, Comparer<int>.Create((a, b) => TriangleCenter(a)[axis].CompareTo(TriangleCenter(b)[axis])));
            int left = count / 2; node.Left = Build(start, left); node.Right = Build(start + left, count - left); node.Count = 0; return node;
        }

        private void Search(Node node, Ray ray, bool frontFacesOnly, ref float best, ref Vector3 bestNormal)
        {
            if (node == null || !node.Bounds.IntersectRay(ray, out float entry) || entry > best) return;
            if (node.Count > 0)
            {
                for (int item = 0; item < node.Count; item++)
                {
                    int triangle = order[node.Start + item] * 3; int a = triangles[triangle], b = triangles[triangle + 1], c = triangles[triangle + 2];
                    if (a < 0 || b < 0 || c < 0 || a >= vertices.Length || b >= vertices.Length || c >= vertices.Length) continue;
                    LastTriangleTests++; var normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).normalized;
                    if (frontFacesOnly && Vector3.Dot(normal, ray.direction) >= 0f) continue;
                    if (!RayTriangle(ray, vertices[a], vertices[b], vertices[c], out float distance) || distance >= best) continue;
                    best = distance; bestNormal = normal;
                }
                return;
            }
            float leftEntry = Entry(node.Left, ray), rightEntry = Entry(node.Right, ray);
            if (leftEntry <= rightEntry) { Search(node.Left, ray, frontFacesOnly, ref best, ref bestNormal); Search(node.Right, ray, frontFacesOnly, ref best, ref bestNormal); }
            else { Search(node.Right, ray, frontFacesOnly, ref best, ref bestNormal); Search(node.Left, ray, frontFacesOnly, ref best, ref bestNormal); }
        }

        private static float Entry(Node node, Ray ray) => node != null && node.Bounds.IntersectRay(ray, out float entry) ? entry : float.PositiveInfinity;
        private Bounds TriangleBounds(int triangleIndex) { int i = triangleIndex * 3; var bounds = new Bounds(vertices[triangles[i]], Vector3.zero); bounds.Encapsulate(vertices[triangles[i + 1]]); bounds.Encapsulate(vertices[triangles[i + 2]]); return bounds; }
        private Vector3 TriangleCenter(int triangleIndex) { int i = triangleIndex * 3; return (vertices[triangles[i]] + vertices[triangles[i + 1]] + vertices[triangles[i + 2]]) / 3f; }
        private Vector3Int Cell(Vector3 value) => new Vector3Int(Mathf.FloorToInt(value.x / cellSize), Mathf.FloorToInt(value.y / cellSize), Mathf.FloorToInt(value.z / cellSize));

        private static bool RayTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float distance)
        {
            distance = 0f; var edge1 = b - a; var edge2 = c - a; var p = Vector3.Cross(ray.direction, edge2); float determinant = Vector3.Dot(edge1, p);
            if (Mathf.Abs(determinant) < 1e-8f) return false; float inverse = 1f / determinant; var t = ray.origin - a; float u = Vector3.Dot(t, p) * inverse;
            if (u < 0f || u > 1f) return false; var q = Vector3.Cross(t, edge1); float v = Vector3.Dot(ray.direction, q) * inverse;
            if (v < 0f || u + v > 1f) return false; distance = Vector3.Dot(edge2, q) * inverse; return distance >= 0f;
        }
    }

    public enum SkinWeightPaintMode { Replace, Add, Subtract, Smooth, Normalize, Prune, Flood }

    [Serializable]
    public sealed class SkinWeightBrushSettings
    {
        public SkinWeightPaintMode Mode = SkinWeightPaintMode.Replace;
        public float Radius = 0.08f;
        public float Strength = 0.25f;
        public float Falloff = 1f;
        public int MaximumInfluences = 8;
        public float PruneThreshold = 0.001f;
        public bool MirrorX;
        public bool FrontFacesOnly = true;
        public bool XRay;
    }

    public sealed class SkinWeightOperationResult
    {
        public int AffectedVertices;
        public int PrunedInfluences;
        public int MaximumInfluences;
        public string MeshAssetPath;
        public string BeforeHash;
        public string AfterHash;
        public bool UndoRegistered;
    }

    public static class RigAssetSafety
    {
        public static bool IsEditableStandaloneMesh(Mesh mesh)
        {
            if (mesh == null) return false;
            string path = AssetDatabase.GetAssetPath(mesh);
            return path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetExtension(path), ".asset", StringComparison.OrdinalIgnoreCase)
                && AssetDatabase.LoadMainAssetAtPath(path) == mesh;
        }

        public static Mesh CreateEditableSkinCopy(SkinnedMeshRenderer renderer, string outputFolder = null)
        {
            if (renderer == null || renderer.sharedMesh == null) throw new ArgumentNullException(nameof(renderer));
            var folder = AssetModelerAssetPaths.ValidateAndEnsureFolder(string.IsNullOrWhiteSpace(outputFolder)
                ? $"{AssetModelerAssetPaths.DefaultRoot}/Rigging/Skins"
                : outputFolder);
            var copy = UnityEngine.Object.Instantiate(renderer.sharedMesh);
            copy.name = renderer.sharedMesh.name + "_SkinEditable";
            string path = AssetModelerAssetPaths.UniqueAssetPath(folder, copy.name, ".asset");
            AssetDatabase.CreateAsset(copy, path);
            Undo.RegisterCreatedObjectUndo(copy, "Create editable skin mesh asset");
            Undo.RecordObject(renderer, "Assign editable skin mesh");
            renderer.sharedMesh = copy;
            EditorUtility.SetDirty(renderer);
            return copy;
        }

        public static void RequireEditable(Mesh mesh)
        {
            if (!IsEditableStandaloneMesh(mesh))
                throw new InvalidOperationException("EDITABLE_SKIN_REQUIRED: create an editable .asset skin copy before changing weights.");
        }

        public static void CreateAssetWithUndo(UnityEngine.Object asset, string path, string undoName)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            AssetDatabase.CreateAsset(asset, path);
            Undo.RegisterCreatedObjectUndo(asset, string.IsNullOrWhiteSpace(undoName) ? "Create rig asset" : undoName);
            EditorUtility.SetDirty(asset);
        }
    }

    public sealed class SkinWeightTable
    {
        public readonly List<Dictionary<int, float>> Vertices;
        public SkinWeightTable(int vertexCount)
        {
            Vertices = new List<Dictionary<int, float>>(vertexCount);
            for (int i = 0; i < vertexCount; i++) Vertices.Add(new Dictionary<int, float>());
        }

        public static SkinWeightTable Read(Mesh mesh)
        {
            var table = new SkinWeightTable(mesh.vertexCount);
            using (var counts = mesh.GetBonesPerVertex())
            using (var weights = mesh.GetAllBoneWeights())
            {
                int offset = 0;
                for (int vertex = 0; vertex < counts.Length; vertex++)
                {
                    int count = counts[vertex];
                    for (int i = 0; i < count && offset + i < weights.Length; i++)
                    {
                        var value = weights[offset + i];
                        if (value.weight > 0f) table.Vertices[vertex][value.boneIndex] = value.weight;
                    }
                    offset += count;
                }
            }
            return table;
        }

        public int NormalizeAndLimit(int vertex, int maximumInfluences, float pruneThreshold)
            => NormalizeAndLimit(vertex, maximumInfluences, pruneThreshold, null);

        public int NormalizeAndLimit(int vertex, int maximumInfluences, float pruneThreshold, ISet<int> lockedBones)
        {
            var weights = Vertices[vertex];
            int before = weights.Count;
            foreach (var key in weights.Where(pair => (lockedBones == null || !lockedBones.Contains(pair.Key)) && (!Finite(pair.Value) || pair.Value <= pruneThreshold)).Select(pair => pair.Key).ToArray()) weights.Remove(key);
            var locked = weights.Where(pair => lockedBones != null && lockedBones.Contains(pair.Key) && Finite(pair.Value) && pair.Value > 0f).OrderBy(pair => pair.Key).ToArray();
            var unlocked = weights.Where(pair => lockedBones == null || !lockedBones.Contains(pair.Key)).OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).ToArray();
            weights.Clear();
            int keep = Mathf.Clamp(maximumInfluences, 1, byte.MaxValue);
            for (int i = 0; i < locked.Length; i++) weights[locked[i].Key] = locked[i].Value;
            int unlockedKeep = Mathf.Max(0, keep - locked.Length);
            float lockedSum = locked.Sum(pair => pair.Value);
            float unlockedSum = unlocked.Take(unlockedKeep).Sum(pair => Mathf.Max(0f, pair.Value));
            float available = Mathf.Max(0f, 1f - lockedSum);
            if (unlockedSum > 1e-12f)
                for (int i = 0; i < Mathf.Min(unlockedKeep, unlocked.Length); i++)
                    weights[unlocked[i].Key] = Mathf.Max(0f, unlocked[i].Value) / unlockedSum * available;
            else if (locked.Length == 0 && unlocked.Length > 0)
                weights[unlocked[0].Key] = 1f;
            return before - weights.Count;
        }

        public SkinWeightTable Clone()
        {
            var copy = new SkinWeightTable(Vertices.Count);
            for (int vertex = 0; vertex < Vertices.Count; vertex++)
                foreach (var pair in Vertices[vertex]) copy.Vertices[vertex][pair.Key] = pair.Value;
            return copy;
        }

        public void Write(Mesh mesh)
        {
            var counts = new byte[Vertices.Count];
            var output = new List<BoneWeight1>();
            for (int vertex = 0; vertex < Vertices.Count; vertex++)
            {
                var ordered = Vertices[vertex].OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Take(byte.MaxValue).ToArray();
                counts[vertex] = (byte)ordered.Length;
                for (int i = 0; i < ordered.Length; i++) output.Add(new BoneWeight1 { boneIndex = ordered[i].Key, weight = ordered[i].Value });
            }
            using (var nativeCounts = new NativeArray<byte>(counts, Allocator.Temp))
            using (var nativeWeights = new NativeArray<BoneWeight1>(output.ToArray(), Allocator.Temp))
                mesh.SetBoneWeights(nativeCounts, nativeWeights);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public static class SkinWeightEditorService
    {
        private sealed class MeshTopologyCache
        {
            public int VertexCount;
            public int IndexCount;
            public int[][] Neighbors;
            public int[] MirrorVertices;
        }

        private static readonly Dictionary<int, MeshTopologyCache> TopologyCaches = new Dictionary<int, MeshTopologyCache>();

        public static SkinWeightOperationResult ApplyRadialBrush(
            SkinnedMeshRenderer renderer,
            int boneIndex,
            Vector3 localCenter,
            SkinWeightBrushSettings settings,
            ISet<int> lockedBones = null,
            ISet<int> lockedVertices = null,
            IReadOnlyList<Vector3> brushPositions = null,
            ISet<int> includedVertices = null,
            bool registerUndo = true,
            IReadOnlyList<int> candidateVertices = null)
        {
            if (renderer == null || renderer.sharedMesh == null) throw new ArgumentNullException(nameof(renderer));
            if (boneIndex < 0 || boneIndex >= renderer.bones.Length) throw new ArgumentOutOfRangeException(nameof(boneIndex));
            settings = settings ?? new SkinWeightBrushSettings();
            settings.FrontFacesOnly = settings.FrontFacesOnly && !settings.XRay;
            var mesh = renderer.sharedMesh;
            RigAssetSafety.RequireEditable(mesh);
            string beforeHash = RigTargetResolver.ComputeSkinWeightHash(renderer.transform, new[] { renderer });
            if (registerUndo) Undo.RegisterCompleteObjectUndo(mesh, "Paint skin weights");
            var table = SkinWeightTable.Read(mesh);
            var topology = GetTopology(mesh);
            var positions = brushPositions != null && brushPositions.Count == mesh.vertexCount ? brushPositions : mesh.vertices;
            var affected = new List<int>();
            var changed = new HashSet<int>();
            var originals = new Dictionary<int, Dictionary<int, float>>();
            float radius = Mathf.Max(settings.Radius, 1e-6f);
            IEnumerable<int> candidates = candidateVertices != null && settings.Mode != SkinWeightPaintMode.Flood && settings.Mode != SkinWeightPaintMode.Normalize
                ? candidateVertices.Distinct() : Enumerable.Range(0, positions.Count);
            foreach (int vertex in candidates)
            {
                if (vertex < 0 || vertex >= positions.Count) continue;
                if (lockedVertices != null && lockedVertices.Contains(vertex)) continue;
                if (includedVertices != null && !includedVertices.Contains(vertex)) continue;
                float distance = Vector3.Distance(positions[vertex], localCenter);
                if (settings.Mode != SkinWeightPaintMode.Flood && distance > radius) continue;
                float normalized = settings.Mode == SkinWeightPaintMode.Flood ? 1f : 1f - distance / radius;
                float amount = Mathf.Clamp01(settings.Strength) * Mathf.Pow(Mathf.Clamp01(normalized), Mathf.Max(0.01f, settings.Falloff));
                if (!originals.ContainsKey(vertex)) originals[vertex] = new Dictionary<int, float>(table.Vertices[vertex]);
                if (Apply(table, topology, vertex, boneIndex, amount, settings, lockedBones)) { affected.Add(vertex); changed.Add(vertex); }
            }
            if (settings.MirrorX && affected.Count > 0)
                Mirror(topology.MirrorVertices, table, affected, boneIndex, MirroredBone(renderer, boneIndex), lockedBones, lockedVertices, includedVertices, changed, originals);
            int pruned = 0;
            int maximum = 0;
            foreach (int vertex in changed)
            {
                EnsureFallback(table.Vertices[vertex], originals.TryGetValue(vertex, out var original) ? original : null, boneIndex, lockedBones);
                pruned += table.NormalizeAndLimit(vertex, settings.MaximumInfluences, settings.PruneThreshold, lockedBones);
            }
            for (int vertex = 0; vertex < table.Vertices.Count; vertex++) maximum = Mathf.Max(maximum, table.Vertices[vertex].Count);
            table.Write(mesh);
            EditorUtility.SetDirty(mesh);
            return new SkinWeightOperationResult
            {
                AffectedVertices = affected.Count,
                PrunedInfluences = pruned,
                MaximumInfluences = maximum,
                MeshAssetPath = AssetDatabase.GetAssetPath(mesh),
                BeforeHash = beforeHash,
                AfterHash = RigTargetResolver.ComputeSkinWeightHash(renderer.transform, new[] { renderer }),
                UndoRegistered = registerUndo,
            };
        }

        public static SkinWeightOperationResult NormalizeAll(SkinnedMeshRenderer renderer, int maximumInfluences = 8, float pruneThreshold = 0.001f)
        {
            return ApplyRadialBrush(renderer, 0, Vector3.zero, new SkinWeightBrushSettings
            {
                Mode = SkinWeightPaintMode.Normalize,
                Strength = 1f,
                MaximumInfluences = maximumInfluences,
                PruneThreshold = pruneThreshold,
            });
        }

        private static bool Apply(SkinWeightTable table, MeshTopologyCache topology, int vertex, int bone, float amount, SkinWeightBrushSettings settings, ISet<int> lockedBones)
        {
            var weights = table.Vertices[vertex];
            if (lockedBones != null && lockedBones.Contains(bone) && settings.Mode != SkinWeightPaintMode.Normalize) return false;
            weights.TryGetValue(bone, out float current);
            switch (settings.Mode)
            {
                case SkinWeightPaintMode.Replace: weights[bone] = Mathf.Lerp(current, 1f, amount); break;
                case SkinWeightPaintMode.Add: weights[bone] = current + amount; break;
                case SkinWeightPaintMode.Subtract: weights[bone] = Mathf.Max(0f, current - amount); break;
                case SkinWeightPaintMode.Flood: weights[bone] = Mathf.Lerp(current, 1f, amount); break;
                case SkinWeightPaintMode.Prune:
                    foreach (var key in weights.Where(pair => pair.Value < settings.PruneThreshold).Select(pair => pair.Key).ToArray()) weights.Remove(key);
                    break;
                case SkinWeightPaintMode.Smooth:
                    float average = NeighborAverage(topology, table, vertex, bone);
                    weights[bone] = Mathf.Lerp(current, average, amount);
                    break;
                case SkinWeightPaintMode.Normalize: break;
            }
            return true;
        }

        private static float NeighborAverage(MeshTopologyCache topology, SkinWeightTable table, int vertex, int bone)
        {
            float sum = 0f; int count = 0;
            var neighbors = vertex >= 0 && vertex < topology.Neighbors.Length ? topology.Neighbors[vertex] : Array.Empty<int>();
            for (int i = 0; i < neighbors.Length; i++)
            {
                table.Vertices[neighbors[i]].TryGetValue(bone, out float value);
                sum += value; count++;
            }
            return count > 0 ? sum / count : 0f;
        }

        private static void Mirror(IReadOnlyList<int> mirrorVertices, SkinWeightTable table, IReadOnlyList<int> affected, int sourceBone, int destinationBone, ISet<int> lockedBones, ISet<int> lockedVertices, ISet<int> includedVertices,
            ISet<int> changed, IDictionary<int, Dictionary<int, float>> originals)
        {
            if (lockedBones != null && lockedBones.Contains(destinationBone)) return;
            for (int i = 0; i < affected.Count; i++)
            {
                int source = affected[i];
                int best = source >= 0 && source < mirrorVertices.Count ? mirrorVertices[source] : -1;
                if (best < 0 || (lockedVertices != null && lockedVertices.Contains(best))) continue;
                if (includedVertices != null && !includedVertices.Contains(best)) continue;
                if (!originals.ContainsKey(best)) originals[best] = new Dictionary<int, float>(table.Vertices[best]);
                table.Vertices[source].TryGetValue(sourceBone, out float value);
                table.Vertices[best][destinationBone] = value; changed.Add(best);
            }
        }

        private static MeshTopologyCache GetTopology(Mesh mesh)
        {
            int id = mesh.GetInstanceID();
            int[] triangles = mesh.triangles;
            if (TopologyCaches.TryGetValue(id, out var cache) && cache.VertexCount == mesh.vertexCount && cache.IndexCount == triangles.Length) return cache;
            var sets = new HashSet<int>[mesh.vertexCount];
            for (int vertex = 0; vertex < sets.Length; vertex++) sets[vertex] = new HashSet<int>();
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                if (a < 0 || b < 0 || c < 0 || a >= sets.Length || b >= sets.Length || c >= sets.Length) continue;
                sets[a].Add(b); sets[a].Add(c); sets[b].Add(a); sets[b].Add(c); sets[c].Add(a); sets[c].Add(b);
            }
            var vertices = mesh.vertices;
            float tolerance = Mathf.Max(mesh.bounds.size.magnitude * 1e-4f, 1e-5f);
            var spatial = new Dictionary<Vector3Int, List<int>>();
            for (int vertex = 0; vertex < vertices.Length; vertex++)
            {
                var key = Quantize(vertices[vertex], tolerance);
                if (!spatial.TryGetValue(key, out var bucket)) spatial[key] = bucket = new List<int>();
                bucket.Add(vertex);
            }
            var mirror = Enumerable.Repeat(-1, vertices.Length).ToArray();
            for (int vertex = 0; vertex < vertices.Length; vertex++)
            {
                var expected = vertices[vertex]; expected.x = -expected.x;
                var center = Quantize(expected, tolerance); float bestDistance = tolerance;
                for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                {
                    if (!spatial.TryGetValue(center + new Vector3Int(x, y, z), out var bucket)) continue;
                    for (int candidateIndex = 0; candidateIndex < bucket.Count; candidateIndex++)
                    {
                        int candidate = bucket[candidateIndex]; float distance = Vector3.Distance(expected, vertices[candidate]);
                        if (distance <= bestDistance) { mirror[vertex] = candidate; bestDistance = distance; }
                    }
                }
            }
            cache = new MeshTopologyCache { VertexCount = mesh.vertexCount, IndexCount = triangles.Length, Neighbors = sets.Select(value => value.ToArray()).ToArray(), MirrorVertices = mirror };
            TopologyCaches[id] = cache; return cache;
        }

        private static Vector3Int Quantize(Vector3 value, float cell)
            => new Vector3Int(Mathf.RoundToInt(value.x / cell), Mathf.RoundToInt(value.y / cell), Mathf.RoundToInt(value.z / cell));

        private static int MirroredBone(SkinnedMeshRenderer renderer, int boneIndex)
        {
            var bones = renderer.bones ?? Array.Empty<Transform>();
            if (boneIndex < 0 || boneIndex >= bones.Length || bones[boneIndex] == null) return boneIndex;
            string name = bones[boneIndex].name;
            string counterpart = SwapSide(name);
            if (counterpart == name) return boneIndex;
            for (int i = 0; i < bones.Length; i++) if (bones[i] != null && string.Equals(bones[i].name, counterpart, StringComparison.OrdinalIgnoreCase)) return i;
            return boneIndex;
        }

        private static string SwapSide(string value)
        {
            if (value.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0) return Replace(value, "Left", "Right");
            if (value.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0) return Replace(value, "Right", "Left");
            if (value.StartsWith("L_", StringComparison.OrdinalIgnoreCase)) return "R_" + value.Substring(2);
            if (value.StartsWith("R_", StringComparison.OrdinalIgnoreCase)) return "L_" + value.Substring(2);
            if (value.EndsWith("_L", StringComparison.OrdinalIgnoreCase)) return value.Substring(0, value.Length - 2) + "_R";
            if (value.EndsWith("_R", StringComparison.OrdinalIgnoreCase)) return value.Substring(0, value.Length - 2) + "_L";
            return value;
        }

        private static string Replace(string value, string oldValue, string newValue)
        {
            int index = value.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
            return index < 0 ? value : value.Substring(0, index) + newValue + value.Substring(index + oldValue.Length);
        }

        private static void EnsureFallback(Dictionary<int, float> weights, Dictionary<int, float> original, int bone, ISet<int> lockedBones)
        {
            if (weights.Count > 0 && weights.Values.Sum() > 1e-12f) return;
            if (original != null && original.Count > 0 && original.Values.Sum() > 1e-12f)
            {
                weights.Clear();
                foreach (var pair in original) weights[pair.Key] = pair.Value;
                return;
            }
            int fallback = bone >= 0 && (lockedBones == null || !lockedBones.Contains(bone)) ? bone : 0;
            weights.Clear(); weights[fallback] = 1f;
        }
    }
}
