using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Modeling.Commands;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Maicon.SceneAnalyzer.Editor.Modeling.Spatial;
using Maicon.SceneAnalyzer.Editor.Modeling.Operations.Topology;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Operations.SurfaceFit
{
    [Serializable]
    public sealed class SmartSurfaceFitParameters
    {
        public string[] targetGlobalObjectIds = Array.Empty<string>();
        public int[] targetInstanceIds = Array.Empty<int>();
        public float[] sourceLocalToWorld = Identity();
        public float clearance = 0.005f;
        public float maxDistance = 1f;
        public float strength = 1f;
        public float shapePreservation = 0.35f;
        public int iterations = 8;
        public int[] affectedVertices = Array.Empty<int>();
        public int[] pinnedVertices = Array.Empty<int>();
        public bool adaptiveRefine = true;
        public float maximumEdgeLength;
        public int refinementPasses = 2;
        public bool recalculateNormals = true;

        public Matrix4x4 SourceLocalToWorld
        {
            get
            {
                var matrix = Matrix4x4.identity;
                if (sourceLocalToWorld != null && sourceLocalToWorld.Length == 16)
                    for (int i = 0; i < 16; i++) matrix[i] = sourceLocalToWorld[i];
                return matrix;
            }
        }

        public static float[] Matrix(Matrix4x4 matrix)
        {
            var values = new float[16];
            for (int i = 0; i < values.Length; i++) values[i] = matrix[i];
            return values;
        }

        private static float[] Identity() => Matrix(Matrix4x4.identity);
    }

    public sealed class SurfaceFitEvaluation
    {
        public int VertexCount { get; internal set; }
        public int EvaluatedVertexCount { get; internal set; }
        public int BelowClearanceCount { get; internal set; }
        public float MinimumSignedClearance { get; internal set; } = float.PositiveInfinity;
        public float MeanSignedClearance { get; internal set; }
        public bool IsValid => EvaluatedVertexCount > 0 && BelowClearanceCount == 0;
    }

    public sealed class SmartSurfaceFitOperation : IRecordedMeshOperation
    {
        private readonly SmartSurfaceFitParameters _parameters;

        public MeshOperationDescriptor Descriptor { get; } = new MeshOperationDescriptor(
            "mesh.surface_fit", "Smart Surface Fit", true, true, true, true, true);

        public SmartSurfaceFitOperation(SmartSurfaceFitParameters parameters)
        {
            _parameters = parameters ?? new SmartSurfaceFitParameters();
        }

        public MeshValidationReport Validate(MeshOperationContext context)
        {
            var report = new MeshValidationReport();
            if ((_parameters.targetGlobalObjectIds == null || _parameters.targetGlobalObjectIds.Length == 0) &&
                (_parameters.targetInstanceIds == null || _parameters.targetInstanceIds.Length == 0))
                report.Error("FIT_TARGET_REQUIRED", "Select at least one body/target renderer.");
            if (_parameters.clearance < 0f || !Finite(_parameters.clearance))
                report.Error("FIT_CLEARANCE_INVALID", "Clearance must be finite and non-negative.");
            if (_parameters.maxDistance <= 0f || !Finite(_parameters.maxDistance))
                report.Error("FIT_DISTANCE_INVALID", "Maximum fit distance must be finite and positive.");
            if (_parameters.iterations < 1 || _parameters.iterations > 200)
                report.Error("FIT_ITERATIONS_INVALID", "Surface Fit iterations must be between 1 and 200.");
            if (_parameters.refinementPasses < 0 || _parameters.refinementPasses > 4)
                report.Error("FIT_REFINE_PASSES_INVALID", "Adaptive refinement passes must be between 0 and 4.");
            if (_parameters.maximumEdgeLength < 0f || !Finite(_parameters.maximumEdgeLength))
                report.Error("FIT_REFINE_LENGTH_INVALID", "Maximum edge length must be finite and non-negative; zero selects an automatic value.");
            int targetCount = Mathf.Max(
                _parameters.targetGlobalObjectIds?.Length ?? 0,
                _parameters.targetInstanceIds?.Length ?? 0);
            for (int i = 0; i < targetCount; i++)
                if (!TryResolveRenderer(_parameters, i, out _))
                {
                    string label = _parameters.targetGlobalObjectIds != null && i < _parameters.targetGlobalObjectIds.Length
                        ? _parameters.targetGlobalObjectIds[i]
                        : $"instance {_parameters.targetInstanceIds[i]}";
                    report.Error("FIT_TARGET_MISSING", $"Target {label} cannot be resolved.");
                }
            ValidateVertices(report, _parameters.affectedVertices, context.WorkingMesh.vertexCount, "FIT_VERTEX_OUT_OF_RANGE");
            ValidateVertices(report, _parameters.pinnedVertices, context.WorkingMesh.vertexCount, "FIT_PIN_OUT_OF_RANGE");
            if (context.WorkingMesh.blendShapeCount > 0)
                report.Warning("FIT_BLEND_SHAPES_PRESERVED", "Blend-shape streams are preserved; validate extreme shape weights after fitting.");
            return report;
        }

        public MeshOperationResult Preview(MeshOperationContext context) => Apply(context);

        public MeshOperationResult Apply(MeshOperationContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            int createdVertices = 0;
            int createdFaces = 0;
            if (_parameters.adaptiveRefine && _parameters.refinementPasses > 0)
            {
                float maximumEdge = _parameters.maximumEdgeLength > 0f
                    ? _parameters.maximumEdgeLength
                    : Mathf.Max(_parameters.clearance * 4f, context.WorkingMesh.bounds.size.magnitude / 64f, 1e-5f);
                for (int pass = 0; pass < _parameters.refinementPasses; pass++)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    var refinement = new AdaptiveRefineOperation(maximumEdge).Apply(context);
                    createdVertices += refinement.CreatedVertexCount;
                    createdFaces += refinement.CreatedFaceCount;
                    if (refinement.CreatedFaceCount == 0) break;
                }
            }
            var bvhMeshes = new List<Mesh>();
            var bvhs = BuildTargets(_parameters, bvhMeshes);
            try
            {
                if (bvhs.Count == 0) throw new InvalidOperationException("FIT_TARGET_EMPTY: target contains no triangles.");
                var mesh = context.WorkingMesh;
                var original = mesh.vertices;
                var positions = (Vector3[])original.Clone();
                var targets = new Vector3[positions.Length];
                var affected = Mask(_parameters.affectedVertices, positions.Length, true);
                var pinned = Mask(_parameters.pinnedVertices, positions.Length, false);
                var hasTarget = new bool[positions.Length];
                for (int vertex = 0; vertex < positions.Length; vertex++)
                {
                    if ((vertex & 1023) == 0) context.CancellationToken.ThrowIfCancellationRequested();
                    if (!affected[vertex] || pinned[vertex]) continue;
                    var best = BestTarget(bvhs, positions[vertex], _parameters.maxDistance);
                    if (!best.Found) continue;
                    targets[vertex] = best.Point + best.Normal * _parameters.clearance;
                    hasTarget[vertex] = true;
                }

                var adjacency = BuildAdjacency(mesh, positions.Length);
                var restDifferential = new Vector3[positions.Length];
                for (int i = 0; i < positions.Length; i++)
                    restDifferential[i] = original[i] - Average(original, adjacency[i], original[i]);
                var next = new Vector3[positions.Length];
                float attraction = Mathf.Clamp01(_parameters.strength);
                float preservation = Mathf.Clamp01(_parameters.shapePreservation);
                for (int iteration = 0; iteration < _parameters.iterations; iteration++)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    for (int vertex = 0; vertex < positions.Length; vertex++)
                    {
                        if (pinned[vertex] || !affected[vertex]) { next[vertex] = positions[vertex]; continue; }
                        var target = hasTarget[vertex]
                            ? Vector3.Lerp(positions[vertex], targets[vertex], attraction)
                            : positions[vertex];
                        var shapeTarget = Average(positions, adjacency[vertex], positions[vertex]) + restDifferential[vertex];
                        next[vertex] = Vector3.Lerp(target, shapeTarget, preservation);
                    }
                    Array.Copy(next, positions, positions.Length);
                }

                // Final collision/clearance projection is deterministic and takes precedence over smoothing.
                for (int vertex = 0; vertex < positions.Length; vertex++)
                {
                    if (!affected[vertex] || pinned[vertex]) continue;
                    var closest = BestTarget(bvhs, positions[vertex], _parameters.maxDistance);
                    if (!closest.Found) continue;
                    float signed = Vector3.Dot(positions[vertex] - closest.Point, closest.Normal);
                    if (signed < _parameters.clearance)
                        positions[vertex] += closest.Normal * (_parameters.clearance - signed);
                }

                for (int i = 0; i < positions.Length; i++)
                    if (!Finite(positions[i])) throw new InvalidOperationException($"FIT_NOT_FINITE: vertex {i} produced NaN or Infinity.");
                mesh.vertices = positions;
                mesh.RecalculateBounds();
                if (_parameters.recalculateNormals && mesh.normals.Length == mesh.vertexCount)
                {
                    mesh.RecalculateNormals();
                    if (mesh.tangents.Length == mesh.vertexCount) mesh.RecalculateTangents();
                }
                return new MeshOperationResult { CreatedVertexCount = createdVertices, CreatedFaceCount = createdFaces };
            }
            finally
            {
                for (int i = 0; i < bvhMeshes.Count; i++)
                    if (bvhMeshes[i] != null) UnityEngine.Object.DestroyImmediate(bvhMeshes[i]);
            }
        }

        public string SerializeParameters() => JsonConvert.SerializeObject(_parameters);

        public static SmartSurfaceFitOperation Deserialize(string json)
            => new SmartSurfaceFitOperation(JsonConvert.DeserializeObject<SmartSurfaceFitParameters>(json ?? "{}") ?? new SmartSurfaceFitParameters());

        public static SmartSurfaceFitParameters CreateParameters(
            Renderer sourceRenderer,
            IReadOnlyList<Renderer> targets,
            float clearance,
            float maxDistance)
        {
            if (sourceRenderer == null) throw new ArgumentNullException(nameof(sourceRenderer));
            var ids = new List<string>();
            var instanceIds = new List<int>();
            if (targets != null)
                for (int i = 0; i < targets.Count; i++)
                    if (targets[i] != null)
                    {
                        ids.Add(GlobalObjectId.GetGlobalObjectIdSlow(targets[i]).ToString());
                        instanceIds.Add(targets[i].GetInstanceID());
                    }
            return new SmartSurfaceFitParameters
            {
                targetGlobalObjectIds = ids.ToArray(),
                targetInstanceIds = instanceIds.ToArray(),
                sourceLocalToWorld = SmartSurfaceFitParameters.Matrix(sourceRenderer.transform.localToWorldMatrix),
                clearance = clearance,
                maxDistance = maxDistance,
            };
        }

        public static SurfaceFitEvaluation Evaluate(
            Mesh source,
            Matrix4x4 sourceLocalToWorld,
            IReadOnlyList<Renderer> targets,
            float requiredClearance,
            float maxDistance)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var parameters = CreateParametersFromMatrices(sourceLocalToWorld, targets, requiredClearance, maxDistance);
            var temporary = new List<Mesh>();
            var bvhs = BuildTargets(parameters, temporary);
            try
            {
                var evaluation = new SurfaceFitEvaluation { VertexCount = source.vertexCount };
                var points = source.vertices;
                double sum = 0d;
                for (int i = 0; i < points.Length; i++)
                {
                    var closest = BestTarget(bvhs, points[i], maxDistance);
                    if (!closest.Found) continue;
                    float signed = Vector3.Dot(points[i] - closest.Point, closest.Normal);
                    evaluation.EvaluatedVertexCount++;
                    evaluation.MinimumSignedClearance = Mathf.Min(evaluation.MinimumSignedClearance, signed);
                    if (signed + 1e-5f < requiredClearance) evaluation.BelowClearanceCount++;
                    sum += signed;
                }
                evaluation.MeanSignedClearance = evaluation.EvaluatedVertexCount > 0
                    ? (float)(sum / evaluation.EvaluatedVertexCount)
                    : float.PositiveInfinity;
                return evaluation;
            }
            finally
            {
                for (int i = 0; i < temporary.Count; i++) UnityEngine.Object.DestroyImmediate(temporary[i]);
            }
        }

        private static SmartSurfaceFitParameters CreateParametersFromMatrices(
            Matrix4x4 sourceLocalToWorld,
            IReadOnlyList<Renderer> targets,
            float clearance,
            float maxDistance)
        {
            var ids = new List<string>();
            var instanceIds = new List<int>();
            if (targets != null)
                for (int i = 0; i < targets.Count; i++)
                    if (targets[i] != null)
                    {
                        ids.Add(GlobalObjectId.GetGlobalObjectIdSlow(targets[i]).ToString());
                        instanceIds.Add(targets[i].GetInstanceID());
                    }
            return new SmartSurfaceFitParameters
            {
                targetGlobalObjectIds = ids.ToArray(),
                targetInstanceIds = instanceIds.ToArray(),
                sourceLocalToWorld = SmartSurfaceFitParameters.Matrix(sourceLocalToWorld),
                clearance = clearance,
                maxDistance = maxDistance,
            };
        }

        private static List<TriangleBvh> BuildTargets(SmartSurfaceFitParameters parameters, List<Mesh> temporaryMeshes)
        {
            var result = new List<TriangleBvh>();
            var sourceWorldToLocal = parameters.SourceLocalToWorld.inverse;
            int targetCount = Mathf.Max(
                parameters.targetGlobalObjectIds?.Length ?? 0,
                parameters.targetInstanceIds?.Length ?? 0);
            for (int i = 0; i < targetCount; i++)
            {
                if (!TryResolveRenderer(parameters, i, out var renderer)) continue;
                Mesh mesh;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    mesh = new Mesh { name = skinned.name + "_SurfaceFitPose" };
                    skinned.BakeMesh(mesh);
                    temporaryMeshes.Add(mesh);
                }
                else mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                var targetToSource = sourceWorldToLocal * renderer.transform.localToWorldMatrix;
                var bvh = new TriangleBvh(mesh, targetToSource);
                if (bvh.TriangleCount > 0) result.Add(bvh);
            }
            return result;
        }

        private static ClosestTrianglePoint BestTarget(IReadOnlyList<TriangleBvh> targets, Vector3 point, float maxDistance)
        {
            var best = new ClosestTrianglePoint(false, default, default, float.PositiveInfinity, -1);
            for (int i = 0; i < targets.Count; i++)
            {
                var candidate = targets[i].FindClosest(point, Mathf.Min(maxDistance, best.Distance));
                if (candidate.Found && candidate.Distance < best.Distance) best = candidate;
            }
            return best;
        }

        private static bool TryResolveRenderer(string globalId, out Renderer renderer)
        {
            renderer = null;
            if (string.IsNullOrWhiteSpace(globalId) || !GlobalObjectId.TryParse(globalId, out var parsed)) return false;
            var value = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsed);
            renderer = value as Renderer;
            if (renderer == null && value is GameObject gameObject) renderer = gameObject.GetComponent<Renderer>();
            if (renderer == null && value is Component component) renderer = component.GetComponent<Renderer>();
            return renderer != null;
        }

        private static bool TryResolveRenderer(SmartSurfaceFitParameters parameters, int index, out Renderer renderer)
        {
            string globalId = parameters.targetGlobalObjectIds != null && index < parameters.targetGlobalObjectIds.Length
                ? parameters.targetGlobalObjectIds[index]
                : null;
            if (TryResolveRenderer(globalId, out renderer)) return true;
            int instanceId = parameters.targetInstanceIds != null && index < parameters.targetInstanceIds.Length
                ? parameters.targetInstanceIds[index]
                : 0;
            var value = instanceId == 0 ? null : EditorUtility.EntityIdToObject((EntityId)instanceId);
            renderer = value as Renderer;
            if (renderer == null && value is GameObject gameObject) renderer = gameObject.GetComponent<Renderer>();
            if (renderer == null && value is Component component) renderer = component.GetComponent<Renderer>();
            return renderer != null;
        }

        private static IReadOnlyList<int>[] BuildAdjacency(Mesh mesh, int count)
        {
            var sets = new HashSet<int>[count];
            for (int i = 0; i < count; i++) sets[i] = new HashSet<int>();
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
            var result = new IReadOnlyList<int>[count];
            for (int i = 0; i < count; i++) result[i] = new List<int>(sets[i]);
            return result;
        }

        private static void Connect(HashSet<int>[] sets, int a, int b) { sets[a].Add(b); sets[b].Add(a); }

        private static Vector3 Average(Vector3[] values, IReadOnlyList<int> indices, Vector3 fallback)
        {
            if (indices.Count == 0) return fallback;
            var result = Vector3.zero;
            for (int i = 0; i < indices.Count; i++) result += values[indices[i]];
            return result / indices.Count;
        }

        private static bool[] Mask(int[] indices, int count, bool emptyMeansAll)
        {
            var result = new bool[count];
            if ((indices == null || indices.Length == 0) && emptyMeansAll)
            {
                for (int i = 0; i < count; i++) result[i] = true;
                return result;
            }
            if (indices != null)
                for (int i = 0; i < indices.Length; i++) if (indices[i] >= 0 && indices[i] < count) result[indices[i]] = true;
            return result;
        }

        private static void ValidateVertices(MeshValidationReport report, int[] values, int count, string code)
        {
            if (values == null) return;
            for (int i = 0; i < values.Length; i++)
                if (values[i] < 0 || values[i] >= count)
                {
                    report.Error(code, $"Vertex {values[i]} is outside a mesh with {count} vertices.");
                    return;
                }
        }

        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
