using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Modeling.Commands;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Newtonsoft.Json;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Operations.Deform
{
    public enum MeshDeformMode
    {
        Grab,
        Smooth,
        Relax,
        Flatten,
        Inflate,
        Bend,
        Twist,
        Taper,
        Lattice,
        Spline,
        Laplacian,
        Arap,
    }

    [Serializable]
    public sealed class MeshDeformParameters
    {
        public MeshDeformMode mode;
        public int axis = 1;
        public float strength = 0.1f;
        public float radius = 1f;
        public int iterations = 8;
        public float[] center = { 0f, 0f, 0f };
        public float[] direction = { 0f, 0.1f, 0f };
        public int[] selectedVertices = Array.Empty<int>();
        public int[] pinnedVertices = Array.Empty<int>();
        public float[] latticeOffsets = new float[24];
        public float[] splinePoints = Array.Empty<float>();
        public bool recalculateNormals = true;

        public Vector3 Center => Vector(center);
        public Vector3 Direction => Vector(direction);

        private static Vector3 Vector(float[] value)
            => value != null && value.Length >= 3 ? new Vector3(value[0], value[1], value[2]) : Vector3.zero;
    }

    public sealed class DeformMeshOperation : IRecordedMeshOperation
    {
        private readonly MeshDeformParameters _parameters;

        public MeshOperationDescriptor Descriptor { get; } = new MeshOperationDescriptor(
            "mesh.deform", "Deform mesh", false, true, true, true, true);

        public DeformMeshOperation(MeshDeformParameters parameters)
        {
            _parameters = parameters ?? new MeshDeformParameters();
        }

        public MeshValidationReport Validate(MeshOperationContext context)
        {
            var report = new MeshValidationReport();
            if (_parameters.axis < 0 || _parameters.axis > 2) report.Error("DEFORM_AXIS_INVALID", "Axis must be X, Y or Z.");
            if (!Finite(_parameters.strength) || !Finite(_parameters.radius) || _parameters.radius < 0f)
                report.Error("DEFORM_PARAMETER_INVALID", "Strength/radius must be finite and radius cannot be negative.");
            if (_parameters.iterations < 1 || _parameters.iterations > 500)
                report.Error("DEFORM_ITERATIONS_INVALID", "Iterations must be between 1 and 500.");
            ValidateVertices(report, _parameters.selectedVertices, context.WorkingMesh.vertexCount, "SELECTED_VERTEX_OUT_OF_RANGE");
            ValidateVertices(report, _parameters.pinnedVertices, context.WorkingMesh.vertexCount, "PINNED_VERTEX_OUT_OF_RANGE");
            if (_parameters.mode == MeshDeformMode.Lattice
                && (_parameters.latticeOffsets == null || _parameters.latticeOffsets.Length != 24))
                report.Error("LATTICE_CONTROL_COUNT", "Lattice deformation requires eight XYZ control offsets.");
            if (_parameters.mode == MeshDeformMode.Spline)
            {
                if (_parameters.splinePoints == null || _parameters.splinePoints.Length < 6 || _parameters.splinePoints.Length % 3 != 0)
                    report.Error("SPLINE_CONTROL_COUNT", "Spline deformation requires at least two XYZ control points.");
                else
                    for (int i = 0; i < _parameters.splinePoints.Length; i++)
                        if (!Finite(_parameters.splinePoints[i]))
                        {
                            report.Error("SPLINE_CONTROL_INVALID", "Spline control points must be finite.");
                            break;
                        }
            }
            if (context.WorkingMesh.blendShapeCount > 0)
                report.Warning("BLEND_SHAPE_DELTAS_PRESERVED", "Base positions are deformed while blend-shape delta streams remain preserved.");
            return report;
        }

        public MeshOperationResult Preview(MeshOperationContext context) => Apply(context);

        public MeshOperationResult Apply(MeshOperationContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var mesh = context.WorkingMesh;
            var positions = mesh.vertices;
            var original = (Vector3[])positions.Clone();
            var adjacency = BuildAdjacency(mesh, positions.Length);
            var weights = BuildSoftWeights(original, _parameters.selectedVertices, _parameters.Center, _parameters.radius);
            var pinned = Mask(_parameters.pinnedVertices, positions.Length);

            switch (_parameters.mode)
            {
                case MeshDeformMode.Grab:
                    for (int i = 0; i < positions.Length; i++) positions[i] += _parameters.Direction * _parameters.strength * weights[i];
                    break;
                case MeshDeformMode.Inflate:
                {
                    var normals = mesh.normals;
                    if (normals.Length != positions.Length) normals = EstimateNormals(mesh, positions);
                    for (int i = 0; i < positions.Length; i++) positions[i] += normals[i] * _parameters.strength * weights[i];
                    break;
                }
                case MeshDeformMode.Flatten:
                    for (int i = 0; i < positions.Length; i++)
                        positions[i][_parameters.axis] = Mathf.Lerp(positions[i][_parameters.axis], _parameters.Center[_parameters.axis], Mathf.Clamp01(Mathf.Abs(_parameters.strength)) * weights[i]);
                    break;
                case MeshDeformMode.Smooth:
                    Smooth(positions, adjacency, weights, pinned, Mathf.Clamp01(Mathf.Abs(_parameters.strength)), _parameters.iterations, false, context);
                    break;
                case MeshDeformMode.Relax:
                    Smooth(positions, adjacency, weights, pinned, Mathf.Clamp01(Mathf.Abs(_parameters.strength)), _parameters.iterations, true, context);
                    break;
                case MeshDeformMode.Laplacian:
                    LaplacianEdit(original, positions, adjacency, weights, pinned, context);
                    break;
                case MeshDeformMode.Arap:
                    ArapDistanceSolve(original, positions, adjacency, weights, pinned, context);
                    break;
                case MeshDeformMode.Bend:
                    Bend(positions, weights, mesh.bounds, _parameters.Center, _parameters.axis, _parameters.strength);
                    break;
                case MeshDeformMode.Twist:
                    Twist(positions, weights, mesh.bounds, _parameters.Center, _parameters.axis, _parameters.strength);
                    break;
                case MeshDeformMode.Taper:
                    Taper(positions, weights, mesh.bounds, _parameters.Center, _parameters.axis, _parameters.strength);
                    break;
                case MeshDeformMode.Lattice:
                    Lattice(positions, weights, mesh.bounds, _parameters.latticeOffsets);
                    break;
                case MeshDeformMode.Spline:
                    Spline(positions, weights, mesh.bounds, _parameters.axis, _parameters.strength, _parameters.splinePoints);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            for (int i = 0; i < positions.Length; i++)
                if (!Finite(positions[i])) throw new InvalidOperationException($"DEFORM_NOT_FINITE: vertex {i} produced NaN or Infinity.");
            mesh.vertices = positions;
            mesh.RecalculateBounds();
            if (_parameters.recalculateNormals && mesh.normals.Length == mesh.vertexCount)
            {
                mesh.RecalculateNormals();
                if (mesh.tangents.Length == mesh.vertexCount) mesh.RecalculateTangents();
            }
            return new MeshOperationResult();
        }

        public string SerializeParameters() => JsonConvert.SerializeObject(_parameters);

        public static DeformMeshOperation Deserialize(string json)
            => new DeformMeshOperation(JsonConvert.DeserializeObject<MeshDeformParameters>(json ?? "{}") ?? new MeshDeformParameters());

        private void LaplacianEdit(
            Vector3[] original,
            Vector3[] positions,
            IReadOnlyList<int>[] adjacency,
            float[] weights,
            bool[] pinned,
            MeshOperationContext context)
        {
            var output = positions;
            var differential = new Vector3[positions.Length];
            for (int i = 0; i < positions.Length; i++) differential[i] = original[i] - Average(original, adjacency[i], original[i]);
            var handles = Mask(_parameters.selectedVertices, positions.Length);
            for (int i = 0; i < positions.Length; i++)
                if (handles[i]) positions[i] = original[i] + _parameters.Direction * _parameters.strength;
            var next = new Vector3[positions.Length];
            for (int iteration = 0; iteration < _parameters.iterations; iteration++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                for (int i = 0; i < positions.Length; i++)
                {
                    if (pinned[i] || handles[i]) { next[i] = positions[i]; continue; }
                    var target = Average(positions, adjacency[i], positions[i]) + differential[i];
                    next[i] = Vector3.Lerp(positions[i], target, Mathf.Clamp01(weights[i] * Mathf.Abs(_parameters.strength)));
                }
                Swap(ref positions, ref next);
            }
            if (!ReferenceEquals(positions, output)) Array.Copy(positions, output, positions.Length);
        }

        private void ArapDistanceSolve(
            Vector3[] original,
            Vector3[] positions,
            IReadOnlyList<int>[] adjacency,
            float[] weights,
            bool[] pinned,
            MeshOperationContext context)
        {
            var handles = Mask(_parameters.selectedVertices, positions.Length);
            for (int i = 0; i < positions.Length; i++)
                if (handles[i]) positions[i] = original[i] + _parameters.Direction * _parameters.strength;
            for (int iteration = 0; iteration < _parameters.iterations; iteration++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                for (int a = 0; a < positions.Length; a++)
                {
                    var neighbors = adjacency[a];
                    for (int n = 0; n < neighbors.Count; n++)
                    {
                        int b = neighbors[n];
                        if (b <= a) continue;
                        float restLength = Vector3.Distance(original[a], original[b]);
                        var delta = positions[b] - positions[a];
                        float length = delta.magnitude;
                        if (length < 1e-9f || restLength < 1e-9f) continue;
                        var correction = delta * ((length - restLength) / length);
                        float stiffness = Mathf.Clamp01(Mathf.Max(weights[a], weights[b]) * Mathf.Abs(_parameters.strength));
                        bool lockA = pinned[a] || handles[a];
                        bool lockB = pinned[b] || handles[b];
                        if (!lockA && !lockB)
                        {
                            positions[a] += correction * 0.5f * stiffness;
                            positions[b] -= correction * 0.5f * stiffness;
                        }
                        else if (!lockA) positions[a] += correction * stiffness;
                        else if (!lockB) positions[b] -= correction * stiffness;
                    }
                }
            }
        }

        private static void Smooth(
            Vector3[] positions,
            IReadOnlyList<int>[] adjacency,
            float[] weights,
            bool[] pinned,
            float strength,
            int iterations,
            bool tangential,
            MeshOperationContext context)
        {
            var next = new Vector3[positions.Length];
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var normals = tangential ? EstimateNormalsFromAdjacency(positions, adjacency) : null;
                for (int i = 0; i < positions.Length; i++)
                {
                    if (pinned[i] || adjacency[i].Count == 0) { next[i] = positions[i]; continue; }
                    var delta = Average(positions, adjacency[i], positions[i]) - positions[i];
                    if (tangential) delta -= Vector3.Dot(delta, normals[i]) * normals[i];
                    next[i] = positions[i] + delta * strength * weights[i];
                }
                Array.Copy(next, positions, positions.Length);
            }
        }

        private static void Bend(Vector3[] positions, float[] weights, Bounds bounds, Vector3 center, int axis, float strength)
        {
            var rotationAxis = Axis((axis + 2) % 3);
            float length = Mathf.Max(bounds.size[axis], 1e-6f);
            for (int i = 0; i < positions.Length; i++)
            {
                float normalized = (positions[i][axis] - center[axis]) / length;
                var rotation = Quaternion.AngleAxis(strength * normalized * Mathf.Rad2Deg * weights[i], rotationAxis);
                positions[i] = center + rotation * (positions[i] - center);
            }
        }

        private static void Twist(Vector3[] positions, float[] weights, Bounds bounds, Vector3 center, int axis, float strength)
        {
            float length = Mathf.Max(bounds.size[axis], 1e-6f);
            var rotationAxis = Axis(axis);
            for (int i = 0; i < positions.Length; i++)
            {
                float normalized = (positions[i][axis] - center[axis]) / length;
                var rotation = Quaternion.AngleAxis(strength * normalized * Mathf.Rad2Deg * weights[i], rotationAxis);
                positions[i] = center + rotation * (positions[i] - center);
            }
        }

        private static void Taper(Vector3[] positions, float[] weights, Bounds bounds, Vector3 center, int axis, float strength)
        {
            float length = Mathf.Max(bounds.size[axis], 1e-6f);
            for (int i = 0; i < positions.Length; i++)
            {
                float normalized = (positions[i][axis] - center[axis]) / length;
                float scale = Mathf.Max(0.001f, 1f + strength * normalized * weights[i]);
                var offset = positions[i] - center;
                for (int dimension = 0; dimension < 3; dimension++) if (dimension != axis) offset[dimension] *= scale;
                positions[i] = center + offset;
            }
        }

        private static void Lattice(Vector3[] positions, float[] weights, Bounds bounds, float[] offsets)
        {
            for (int i = 0; i < positions.Length; i++)
            {
                var normalized = new Vector3(
                    Mathf.InverseLerp(bounds.min.x, bounds.max.x, positions[i].x),
                    Mathf.InverseLerp(bounds.min.y, bounds.max.y, positions[i].y),
                    Mathf.InverseLerp(bounds.min.z, bounds.max.z, positions[i].z));
                var displacement = Vector3.zero;
                for (int corner = 0; corner < 8; corner++)
                {
                    float x = (corner & 1) != 0 ? normalized.x : 1f - normalized.x;
                    float y = (corner & 2) != 0 ? normalized.y : 1f - normalized.y;
                    float z = (corner & 4) != 0 ? normalized.z : 1f - normalized.z;
                    int offset = corner * 3;
                    displacement += new Vector3(offsets[offset], offsets[offset + 1], offsets[offset + 2]) * (x * y * z);
                }
                positions[i] += displacement * weights[i];
            }
        }

        private static void Spline(Vector3[] positions, float[] weights, Bounds bounds, int axis, float strength, float[] values)
        {
            int count = values.Length / 3;
            var points = new Vector3[count];
            for (int i = 0; i < count; i++) points[i] = new Vector3(values[i * 3], values[i * 3 + 1], values[i * 3 + 2]);
            var sourceAxis = Axis(axis);
            float blend = Mathf.Clamp01(Mathf.Abs(strength));
            for (int i = 0; i < positions.Length; i++)
            {
                float t = Mathf.InverseLerp(bounds.min[axis], bounds.max[axis], positions[i][axis]);
                var curve = CatmullRom(points, t);
                var before = CatmullRom(points, Mathf.Clamp01(t - 0.001f));
                var after = CatmullRom(points, Mathf.Clamp01(t + 0.001f));
                var tangent = after - before;
                if (tangent.sqrMagnitude < 1e-12f) tangent = sourceAxis;
                var straight = bounds.center;
                straight[axis] = Mathf.Lerp(bounds.min[axis], bounds.max[axis], t);
                var rotation = Quaternion.FromToRotation(sourceAxis, tangent.normalized);
                var target = curve + rotation * (positions[i] - straight);
                positions[i] = Vector3.Lerp(positions[i], target, blend * weights[i]);
            }
        }

        private static Vector3 CatmullRom(IReadOnlyList<Vector3> points, float t)
        {
            if (points.Count == 2) return Vector3.Lerp(points[0], points[1], t);
            float scaled = Mathf.Clamp01(t) * (points.Count - 1);
            int segment = Mathf.Min(Mathf.FloorToInt(scaled), points.Count - 2);
            float local = scaled - segment;
            var p0 = points[Mathf.Max(0, segment - 1)];
            var p1 = points[segment];
            var p2 = points[segment + 1];
            var p3 = points[Mathf.Min(points.Count - 1, segment + 2)];
            float local2 = local * local;
            float local3 = local2 * local;
            return 0.5f * ((2f * p1)
                + (-p0 + p2) * local
                + (2f * p0 - 5f * p1 + 4f * p2 - p3) * local2
                + (-p0 + 3f * p1 - 3f * p2 + p3) * local3);
        }

        private static float[] BuildSoftWeights(Vector3[] positions, int[] selected, Vector3 center, float radius)
        {
            var result = new float[positions.Length];
            if (selected == null || selected.Length == 0)
            {
                if (radius <= 1e-8f)
                {
                    for (int i = 0; i < result.Length; i++) result[i] = 1f;
                    return result;
                }
                for (int i = 0; i < result.Length; i++) result[i] = Falloff(Vector3.Distance(positions[i], center), radius);
                return result;
            }
            for (int i = 0; i < positions.Length; i++)
            {
                float distance = float.PositiveInfinity;
                for (int selectedIndex = 0; selectedIndex < selected.Length; selectedIndex++)
                    distance = Mathf.Min(distance, Vector3.Distance(positions[i], positions[selected[selectedIndex]]));
                result[i] = radius <= 1e-8f ? (distance <= 1e-8f ? 1f : 0f) : Falloff(distance, radius);
            }
            return result;
        }

        private static float Falloff(float distance, float radius)
        {
            float t = Mathf.Clamp01(1f - distance / Mathf.Max(radius, 1e-8f));
            return t * t * (3f - 2f * t);
        }

        private static IReadOnlyList<int>[] BuildAdjacency(Mesh mesh, int vertexCount)
        {
            var sets = new HashSet<int>[vertexCount];
            for (int i = 0; i < sets.Length; i++) sets[i] = new HashSet<int>();
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                if (mesh.GetTopology(subMesh) != MeshTopology.Triangles) continue;
                var indices = mesh.GetIndices(subMesh, true);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    Connect(sets, indices[i], indices[i + 1]);
                    Connect(sets, indices[i + 1], indices[i + 2]);
                    Connect(sets, indices[i + 2], indices[i]);
                }
            }
            var result = new IReadOnlyList<int>[vertexCount];
            for (int i = 0; i < result.Length; i++) result[i] = new List<int>(sets[i]);
            return result;
        }

        private static void Connect(HashSet<int>[] adjacency, int a, int b)
        {
            adjacency[a].Add(b);
            adjacency[b].Add(a);
        }

        private static Vector3 Average(Vector3[] positions, IReadOnlyList<int> indices, Vector3 fallback)
        {
            if (indices == null || indices.Count == 0) return fallback;
            var value = Vector3.zero;
            for (int i = 0; i < indices.Count; i++) value += positions[indices[i]];
            return value / indices.Count;
        }

        private static Vector3[] EstimateNormals(Mesh mesh, Vector3[] positions)
        {
            var normals = new Vector3[positions.Length];
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                if (mesh.GetTopology(subMesh) != MeshTopology.Triangles) continue;
                var indices = mesh.GetIndices(subMesh, true);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    var normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                    normals[a] += normal; normals[b] += normal; normals[c] += normal;
                }
            }
            for (int i = 0; i < normals.Length; i++) normals[i] = normals[i].sqrMagnitude > 1e-12f ? normals[i].normalized : Vector3.up;
            return normals;
        }

        private static Vector3[] EstimateNormalsFromAdjacency(Vector3[] positions, IReadOnlyList<int>[] adjacency)
        {
            var normals = new Vector3[positions.Length];
            for (int vertex = 0; vertex < positions.Length; vertex++)
            {
                var neighbors = adjacency[vertex];
                var normal = Vector3.zero;
                for (int i = 0; i < neighbors.Count; i++)
                {
                    int next = (i + 1) % Math.Max(1, neighbors.Count);
                    normal += Vector3.Cross(positions[neighbors[i]] - positions[vertex], positions[neighbors[next]] - positions[vertex]);
                }
                normals[vertex] = normal.sqrMagnitude > 1e-12f ? normal.normalized : Vector3.up;
            }
            return normals;
        }

        private static bool[] Mask(int[] indices, int count)
        {
            var result = new bool[count];
            if (indices != null)
                for (int i = 0; i < indices.Length; i++) if (indices[i] >= 0 && indices[i] < count) result[indices[i]] = true;
            return result;
        }

        private static void ValidateVertices(MeshValidationReport report, int[] vertices, int count, string code)
        {
            if (vertices == null) return;
            for (int i = 0; i < vertices.Length; i++)
                if (vertices[i] < 0 || vertices[i] >= count)
                {
                    report.Error(code, $"Vertex {vertices[i]} is outside a mesh with {count} vertices.");
                    return;
                }
        }

        private static Vector3 Axis(int axis) => axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static void Swap(ref Vector3[] left, ref Vector3[] right) { var temp = left; left = right; right = temp; }
    }
}
