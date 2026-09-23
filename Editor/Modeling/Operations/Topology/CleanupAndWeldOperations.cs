using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Modeling.Commands;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Operations.Topology
{
    [Serializable]
    internal sealed class CleanupParameters
    {
        public float areaEpsilon;
        public bool removeUnused = true;
        public bool removeLooseLines;
        public bool removeLoosePoints;
        public int[] removeComponentIds = Array.Empty<int>();
    }

    public sealed class CleanupMeshOperation : IRecordedMeshOperation
    {
        private readonly float _areaEpsilon;
        private readonly bool _removeUnused;
        private readonly bool _removeLooseLines;
        private readonly bool _removeLoosePoints;
        private readonly int[] _removeComponentIds;

        public MeshOperationDescriptor Descriptor { get; } = new MeshOperationDescriptor(
            "mesh.cleanup", "Clean mesh", true, true, true, true, true);

        public CleanupMeshOperation(float areaEpsilon = 0f, bool removeUnused = true,
            bool removeLooseLines = false, bool removeLoosePoints = false, IReadOnlyList<int> removeComponentIds = null)
        {
            _areaEpsilon = areaEpsilon;
            _removeUnused = removeUnused;
            _removeLooseLines = removeLooseLines;
            _removeLoosePoints = removeLoosePoints;
            _removeComponentIds = removeComponentIds == null ? Array.Empty<int>() : new List<int>(removeComponentIds).ToArray();
        }

        public MeshValidationReport Validate(MeshOperationContext context)
        {
            var report = new MeshValidationReport();
            if (_areaEpsilon < 0f || float.IsNaN(_areaEpsilon) || float.IsInfinity(_areaEpsilon))
                report.Error("CLEANUP_EPSILON_INVALID", "Minimum area must be finite and nonnegative.");
            else
            {
                try { ValidateComponentIds(MeshCleanupAnalysis.Analyze(context.WorkingMesh, _areaEpsilon)); }
                catch (Exception exception) { report.Error("CLEANUP_INPUT_INVALID", exception.Message); }
            }
            return report;
        }

        public MeshOperationResult Preview(MeshOperationContext context) => Apply(context);

        public MeshOperationResult Apply(MeshOperationContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var mesh = context.WorkingMesh;
            var snapshot = MeshChannelSnapshot.Capture(mesh);
            var analysis = MeshCleanupAnalysis.Analyze(snapshot, _areaEpsilon);
            ValidateComponentIds(analysis);
            var removedComponents = new HashSet<int>(_removeComponentIds);
            var removedStarts = new HashSet<int>[snapshot.SubMeshes.Count];
            for (int i = 0; i < removedStarts.Length; i++) removedStarts[i] = new HashSet<int>();
            var surfaceEdges = new HashSet<MeshEdgeKey>();
            var surfaceVertices = new bool[snapshot.Positions.Length];
            int removedFaces = 0;
            foreach (var face in analysis.Faces)
            {
                if (face.Degenerate || removedComponents.Contains(face.ComponentId))
                {
                    removedStarts[face.SubMesh].Add(face.IndexStart);
                    removedFaces++;
                    continue;
                }
                for (int corner = 0; corner < face.Vertices.Length; corner++)
                {
                    surfaceVertices[face.Vertices[corner]] = true;
                    surfaceEdges.Add(new MeshEdgeKey(face.Vertices[corner], face.Vertices[(corner + 1) % face.Vertices.Length]));
                }
            }
            var cleaned = new List<int>[snapshot.SubMeshes.Count];
            var topologies = new MeshTopology[snapshot.SubMeshes.Count];
            var referenced = new bool[snapshot.Positions.Length];
            int removedLines = 0, removedPoints = 0;

            for (int subMesh = 0; subMesh < snapshot.SubMeshes.Count; subMesh++)
            {
                var source = snapshot.SubMeshes[subMesh];
                var output = new List<int>(source.Indices.Length);
                cleaned[subMesh] = output;
                topologies[subMesh] = source.Topology;
                if (source.Topology == MeshTopology.Triangles || source.Topology == MeshTopology.Quads)
                {
                    int arity = source.Topology == MeshTopology.Triangles ? 3 : 4;
                    for (int i = 0; i < source.Indices.Length; i += arity)
                    {
                        if (removedStarts[subMesh].Contains(i)) continue;
                        for (int corner = 0; corner < arity; corner++) output.Add(source.Indices[i + corner]);
                    }
                }
                else if (_removeLoosePoints && source.Topology == MeshTopology.Points)
                {
                    foreach (int index in source.Indices)
                    {
                        if (surfaceVertices[index]) output.Add(index);
                        else removedPoints++;
                    }
                }
                else if (_removeLooseLines && (source.Topology == MeshTopology.Lines || source.Topology == MeshTopology.LineStrip))
                {
                    int step = source.Topology == MeshTopology.Lines ? 2 : 1;
                    int removedInSubMesh = 0;
                    for (int i = 0; i + 1 < source.Indices.Length; i += step)
                    {
                        int a = source.Indices[i], b = source.Indices[i + 1];
                        if (!surfaceEdges.Contains(new MeshEdgeKey(a, b))) { removedInSubMesh++; continue; }
                        output.Add(a); output.Add(b);
                    }
                    removedLines += removedInSubMesh;
                    if (source.Topology == MeshTopology.LineStrip)
                    {
                        // Split retained segments into independent lines so removing
                        // a middle segment can never create a new connecting edge.
                        if (removedInSubMesh > 0) topologies[subMesh] = MeshTopology.Lines;
                        else { output.Clear(); output.AddRange(source.Indices); }
                    }
                }
                else output.AddRange(source.Indices);
                foreach (int index in output) referenced[index] = true;
            }

            var selected = new List<int>(snapshot.Positions.Length);
            var remap = new int[snapshot.Positions.Length];
            for (int sourceVertex = 0; sourceVertex < snapshot.Positions.Length; sourceVertex++)
            {
                remap[sourceVertex] = -1;
                if (_removeUnused && !referenced[sourceVertex]) continue;
                remap[sourceVertex] = selected.Count;
                selected.Add(sourceVertex);
            }

            int removedVertices = snapshot.Positions.Length - selected.Count;
            if (removedVertices == 0 && removedFaces == 0 && removedLines == 0 && removedPoints == 0)
                return new MeshOperationResult();
            context.CancellationToken.ThrowIfCancellationRequested();
            var prepared = snapshot.CreateVertexSubset(mesh.name, selected,
                selected.Count > ushort.MaxValue ? IndexFormat.UInt32 : snapshot.IndexFormat);
            try
            {
                prepared.subMeshCount = cleaned.Length;
                for (int subMesh = 0; subMesh < cleaned.Length; subMesh++)
                {
                    var indices = cleaned[subMesh];
                    for (int i = 0; i < indices.Count; i++) indices[i] = remap[indices[i]];
                    prepared.SetIndices(indices, topologies[subMesh], subMesh, false);
                }
                prepared.bounds = SubsetBounds(snapshot.Positions, selected);
                context.CancellationToken.ThrowIfCancellationRequested();
                EditorUtility.CopySerialized(prepared, mesh);
            }
            finally { UnityEngine.Object.DestroyImmediate(prepared); }
            return new MeshOperationResult
            {
                RemovedVertexCount = removedVertices,
                RemovedFaceCount = removedFaces,
                RemovedLineCount = removedLines,
                RemovedPointCount = removedPoints,
                RemovedComponentCount = removedComponents.Count,
            };
        }

        public string SerializeParameters()
            => JsonConvert.SerializeObject(new CleanupParameters
            {
                areaEpsilon = _areaEpsilon, removeUnused = _removeUnused,
                removeLooseLines = _removeLooseLines, removeLoosePoints = _removeLoosePoints,
                removeComponentIds = _removeComponentIds,
            });

        public static CleanupMeshOperation Deserialize(string json)
        {
            var value = JsonConvert.DeserializeObject<CleanupParameters>(json ?? "{}") ?? new CleanupParameters();
            return new CleanupMeshOperation(value.areaEpsilon, value.removeUnused,
                value.removeLooseLines, value.removeLoosePoints, value.removeComponentIds);
        }

        private void ValidateComponentIds(MeshCleanupAnalysis analysis)
        {
            foreach (int id in _removeComponentIds)
                if (id < 0 || id >= analysis.Components.Count)
                    throw new InvalidOperationException("CLEANUP_COMPONENT_INVALID: component id is not present in this mesh revision.");
        }

        internal static Bounds SubsetBounds(Vector3[] positions, IReadOnlyList<int> selected)
        {
            if (selected.Count == 0) return new Bounds(Vector3.zero, Vector3.zero);
            var bounds = new Bounds(positions[selected[0]], Vector3.zero);
            for (int i = 1; i < selected.Count; i++) bounds.Encapsulate(positions[selected[i]]);
            return bounds;
        }
    }

    [Serializable]
    internal sealed class WeldParameters
    {
        public float tolerance = 1e-5f;
        public bool preserveSeams = true;
    }

    public sealed class WeldVerticesOperation : IRecordedMeshOperation
    {
        private readonly float _tolerance;
        private readonly bool _preserveSeams;

        public MeshOperationDescriptor Descriptor { get; } = new MeshOperationDescriptor(
            "mesh.weld", "Weld vertices", true, true, true, true, true);

        public WeldVerticesOperation(float tolerance = 1e-5f, bool preserveSeams = true)
        {
            _tolerance = tolerance;
            _preserveSeams = preserveSeams;
        }

        public MeshValidationReport Validate(MeshOperationContext context)
        {
            var report = new MeshValidationReport();
            if (_tolerance <= 0f || float.IsNaN(_tolerance) || float.IsInfinity(_tolerance))
                report.Error("WELD_TOLERANCE_INVALID", "Weld tolerance must be finite and greater than zero.");
            return report;
        }

        public MeshOperationResult Preview(MeshOperationContext context) => Apply(context);

        public MeshOperationResult Apply(MeshOperationContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var mesh = context.WorkingMesh;
            var snapshot = MeshChannelSnapshot.Capture(mesh);
            var buckets = new Dictionary<Vector3Int, List<int>>();
            var representative = new int[snapshot.Positions.Length];
            int removed = 0;
            for (int vertex = 0; vertex < snapshot.Positions.Length; vertex++)
            {
                if ((vertex & 2047) == 0) context.CancellationToken.ThrowIfCancellationRequested();
                var cell = Cell(snapshot.Positions[vertex]);
                int match = -1;
                for (int x = -1; x <= 1 && match < 0; x++)
                for (int y = -1; y <= 1 && match < 0; y++)
                for (int z = -1; z <= 1 && match < 0; z++)
                {
                    var neighbor = new Vector3Int(cell.x + x, cell.y + y, cell.z + z);
                    if (!buckets.TryGetValue(neighbor, out var candidates)) continue;
                    for (int candidate = 0; candidate < candidates.Count; candidate++)
                    {
                        int other = candidates[candidate];
                        if ((snapshot.Positions[vertex] - snapshot.Positions[other]).sqrMagnitude > _tolerance * _tolerance) continue;
                        if (_preserveSeams && !ChannelsCompatible(snapshot, vertex, other)) continue;
                        match = other;
                        break;
                    }
                }
                if (match >= 0)
                {
                    representative[vertex] = match;
                    removed++;
                }
                else
                {
                    representative[vertex] = vertex;
                    if (!buckets.TryGetValue(cell, out var values)) buckets[cell] = values = new List<int>();
                    values.Add(vertex);
                }
            }

            if (removed == 0) return new MeshOperationResult();
            var usedRepresentatives = new SortedSet<int>();
            var remappedSubMeshes = new List<int>[snapshot.SubMeshes.Count];
            int removedFaces = 0;
            for (int subMesh = 0; subMesh < snapshot.SubMeshes.Count; subMesh++)
            {
                var source = snapshot.SubMeshes[subMesh];
                var output = new List<int>(source.Indices.Length);
                remappedSubMeshes[subMesh] = output;
                if (source.Topology == MeshTopology.Triangles)
                {
                    for (int index = 0; index < source.Indices.Length; index += 3)
                    {
                        int a = representative[source.Indices[index]];
                        int b = representative[source.Indices[index + 1]];
                        int c = representative[source.Indices[index + 2]];
                        if (a == b || b == c || c == a)
                        {
                            removedFaces++;
                            continue;
                        }
                        output.Add(a); output.Add(b); output.Add(c);
                        usedRepresentatives.Add(a); usedRepresentatives.Add(b); usedRepresentatives.Add(c);
                    }
                }
                else
                {
                    for (int index = 0; index < source.Indices.Length; index++)
                    {
                        int value = representative[source.Indices[index]];
                        output.Add(value);
                        usedRepresentatives.Add(value);
                    }
                }
            }

            var selected = new List<int>(usedRepresentatives);
            var compact = new Dictionary<int, int>(selected.Count);
            for (int i = 0; i < selected.Count; i++) compact[selected[i]] = i;
            var prepared = snapshot.CreateVertexSubset(mesh.name, selected,
                selected.Count > ushort.MaxValue ? IndexFormat.UInt32 : snapshot.IndexFormat);
            try
            {
                prepared.subMeshCount = remappedSubMeshes.Length;
                for (int subMesh = 0; subMesh < remappedSubMeshes.Length; subMesh++)
                {
                    var indices = remappedSubMeshes[subMesh];
                    for (int i = 0; i < indices.Count; i++) indices[i] = compact[indices[i]];
                    prepared.SetIndices(indices, snapshot.SubMeshes[subMesh].Topology, subMesh, false);
                }
                prepared.bounds = CleanupMeshOperation.SubsetBounds(snapshot.Positions, selected);
                context.CancellationToken.ThrowIfCancellationRequested();
                EditorUtility.CopySerialized(prepared, mesh);
            }
            finally { UnityEngine.Object.DestroyImmediate(prepared); }
            return new MeshOperationResult
            {
                RemovedVertexCount = snapshot.Positions.Length - selected.Count,
                RemovedFaceCount = removedFaces,
            };
        }

        public string SerializeParameters()
            => JsonConvert.SerializeObject(new WeldParameters { tolerance = _tolerance, preserveSeams = _preserveSeams });

        public static WeldVerticesOperation Deserialize(string json)
        {
            var value = JsonConvert.DeserializeObject<WeldParameters>(json ?? "{}") ?? new WeldParameters();
            return new WeldVerticesOperation(value.tolerance, value.preserveSeams);
        }

        private Vector3Int Cell(Vector3 value)
            => new Vector3Int(
                Mathf.FloorToInt(value.x / _tolerance),
                Mathf.FloorToInt(value.y / _tolerance),
                Mathf.FloorToInt(value.z / _tolerance));

        private bool ChannelsCompatible(MeshChannelSnapshot snapshot, int a, int b)
        {
            float squared = _tolerance * _tolerance;
            if (snapshot.Normals.Length == snapshot.Positions.Length
                && (snapshot.Normals[a] - snapshot.Normals[b]).sqrMagnitude > squared) return false;
            if (snapshot.Tangents.Length == snapshot.Positions.Length
                && (snapshot.Tangents[a] - snapshot.Tangents[b]).sqrMagnitude > squared) return false;
            if (snapshot.Colors.Length == snapshot.Positions.Length
                && MaxColorDelta(snapshot.Colors[a], snapshot.Colors[b]) > _tolerance) return false;
            if (snapshot.Colors32.Length == snapshot.Positions.Length
                && !snapshot.Colors32[a].Equals(snapshot.Colors32[b])) return false;
            for (int channel = 0; channel < snapshot.Uvs.Length; channel++)
            {
                var uv = snapshot.Uvs[channel];
                if (uv.Values.Length == snapshot.Positions.Length
                    && (uv.Values[a] - uv.Values[b]).sqrMagnitude > squared) return false;
            }
            if (!BoneWeightsCompatible(snapshot, a, b)) return false;
            for (int shape = 0; shape < snapshot.BlendShapes.Count; shape++)
            for (int frame = 0; frame < snapshot.BlendShapes[shape].Frames.Count; frame++)
            {
                var data = snapshot.BlendShapes[shape].Frames[frame];
                if ((data.DeltaVertices[a] - data.DeltaVertices[b]).sqrMagnitude > squared
                    || (data.DeltaNormals[a] - data.DeltaNormals[b]).sqrMagnitude > squared
                    || (data.DeltaTangents[a] - data.DeltaTangents[b]).sqrMagnitude > squared) return false;
            }
            return true;
        }

        private static bool BoneWeightsCompatible(MeshChannelSnapshot snapshot, int a, int b)
        {
            if (snapshot.BonesPerVertex.Length != snapshot.Positions.Length) return true;
            int aStart = snapshot.BoneWeightOffsets[a];
            int aEnd = snapshot.BoneWeightOffsets[a + 1];
            int bStart = snapshot.BoneWeightOffsets[b];
            int bEnd = snapshot.BoneWeightOffsets[b + 1];
            if (aEnd - aStart != bEnd - bStart) return false;
            var map = new Dictionary<int, float>();
            for (int i = aStart; i < aEnd; i++) map[snapshot.BoneWeights[i].boneIndex] = snapshot.BoneWeights[i].weight;
            for (int i = bStart; i < bEnd; i++)
                if (!map.TryGetValue(snapshot.BoneWeights[i].boneIndex, out var weight)
                    || Mathf.Abs(weight - snapshot.BoneWeights[i].weight) > 1e-5f) return false;
            return true;
        }

        private static float MaxColorDelta(Color a, Color b)
            => Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b), Mathf.Abs(a.a - b.a));
    }
}
