using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Modeling.Commands;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Maicon.SceneAnalyzer.Editor.Modeling.Operations.Topology;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Operations.Transform
{
    [Serializable]
    internal sealed class TransformVerticesParameters
    {
        public float[] matrix;
        public int[] vertices;
    }

    public sealed class TransformVerticesOperation : IRecordedMeshOperation
    {
        private readonly Matrix4x4 _matrix;
        private readonly int[] _vertices;

        public MeshOperationDescriptor Descriptor { get; } = new MeshOperationDescriptor(
            "mesh.transform_vertices", "Transform mesh vertices", false, true, true, false, true);

        public TransformVerticesOperation(Matrix4x4 matrix, IReadOnlyList<int> vertices = null)
        {
            _matrix = matrix;
            if (vertices == null) _vertices = Array.Empty<int>();
            else
            {
                _vertices = new int[vertices.Count];
                for (int i = 0; i < vertices.Count; i++) _vertices[i] = vertices[i];
            }
        }

        public MeshValidationReport Validate(MeshOperationContext context)
        {
            var report = new MeshValidationReport();
            for (int i = 0; i < 16; i++)
                if (float.IsNaN(_matrix[i]) || float.IsInfinity(_matrix[i]))
                {
                    report.Error("TRANSFORM_NOT_FINITE", "Transform matrix contains NaN or Infinity.");
                    break;
                }
            for (int i = 0; i < _vertices.Length; i++)
                if (_vertices[i] < 0 || _vertices[i] >= context.WorkingMesh.vertexCount)
                {
                    report.Error("VERTEX_OUT_OF_RANGE", $"Vertex {_vertices[i]} is outside the working mesh.");
                    break;
                }
            return report;
        }

        public MeshOperationResult Preview(MeshOperationContext context) => Apply(context);

        public MeshOperationResult Apply(MeshOperationContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var mesh = context.WorkingMesh;
            var positions = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var selected = SelectionMask(positions.Length);
            var normalMatrix = _matrix.inverse.transpose;
            for (int i = 0; i < positions.Length; i++)
            {
                if (!selected[i]) continue;
                positions[i] = _matrix.MultiplyPoint3x4(positions[i]);
                if (normals.Length == positions.Length)
                    normals[i] = normalMatrix.MultiplyVector(normals[i]).normalized;
                if (tangents.Length == positions.Length)
                {
                    var transformed = normalMatrix.MultiplyVector(new Vector3(tangents[i].x, tangents[i].y, tangents[i].z)).normalized;
                    tangents[i] = new Vector4(transformed.x, transformed.y, transformed.z, tangents[i].w);
                }
            }
            mesh.vertices = positions;
            if (normals.Length == positions.Length) mesh.normals = normals;
            if (tangents.Length == positions.Length) mesh.tangents = tangents;
            mesh.RecalculateBounds();
            return new MeshOperationResult();
        }

        public string SerializeParameters()
        {
            var values = new float[16];
            for (int i = 0; i < values.Length; i++) values[i] = _matrix[i];
            return JsonConvert.SerializeObject(new TransformVerticesParameters { matrix = values, vertices = _vertices });
        }

        public static TransformVerticesOperation Deserialize(string json)
        {
            var parameters = JsonConvert.DeserializeObject<TransformVerticesParameters>(json ?? "{}")
                ?? new TransformVerticesParameters();
            var matrix = Matrix4x4.identity;
            if (parameters.matrix != null && parameters.matrix.Length == 16)
                for (int i = 0; i < 16; i++) matrix[i] = parameters.matrix[i];
            return new TransformVerticesOperation(matrix, parameters.vertices);
        }

        private bool[] SelectionMask(int count)
        {
            var mask = new bool[count];
            if (_vertices.Length == 0)
            {
                for (int i = 0; i < count; i++) mask[i] = true;
                return mask;
            }
            for (int i = 0; i < _vertices.Length; i++)
                if (_vertices[i] >= 0 && _vertices[i] < count) mask[_vertices[i]] = true;
            return mask;
        }
    }

    [Serializable]
    internal sealed class MirrorMeshParameters
    {
        public int axis;
        public float plane;
        public bool copy;
    }

    [Serializable]
    internal sealed class MirrorAndWeldParameters
    {
        public int axis;
        public float plane;
        public float tolerance = 1e-5f;
        public bool preserveSeams = true;
    }

    public sealed class MirrorMeshOperation : IRecordedMeshOperation
    {
        private readonly int _axis;
        private readonly float _plane;
        private readonly bool _copy;

        public MeshOperationDescriptor Descriptor { get; }

        public MirrorMeshOperation(int axis, float plane = 0f, bool copy = true)
        {
            _axis = axis;
            _plane = plane;
            _copy = copy;
            Descriptor = new MeshOperationDescriptor(
                "mesh.mirror", copy ? "Mirror mesh with copy" : "Mirror mesh",
                copy, true, true, copy, true);
        }

        public MeshValidationReport Validate(MeshOperationContext context)
        {
            var report = new MeshValidationReport();
            if (_axis < 0 || _axis > 2) report.Error("MIRROR_AXIS_INVALID", "Mirror axis must be X, Y or Z.");
            if (float.IsNaN(_plane) || float.IsInfinity(_plane)) report.Error("MIRROR_PLANE_INVALID", "Mirror plane is not finite.");
            return report;
        }

        public MeshOperationResult Preview(MeshOperationContext context) => Apply(context);

        public MeshOperationResult Apply(MeshOperationContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            return _copy ? ApplyCopy(context.WorkingMesh) : ApplyInPlace(context.WorkingMesh);
        }

        public string SerializeParameters()
            => JsonConvert.SerializeObject(new MirrorMeshParameters { axis = _axis, plane = _plane, copy = _copy });

        public static MirrorMeshOperation Deserialize(string json)
        {
            var value = JsonConvert.DeserializeObject<MirrorMeshParameters>(json ?? "{}") ?? new MirrorMeshParameters();
            return new MirrorMeshOperation(value.axis, value.plane, value.copy);
        }

        private MeshOperationResult ApplyInPlace(Mesh mesh)
        {
            var positions = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            for (int i = 0; i < positions.Length; i++)
            {
                positions[i] = Reflected(positions[i]);
                if (normals.Length == positions.Length)
                {
                    var normal = normals[i];
                    normal[_axis] = -normal[_axis];
                    normals[i] = normal;
                }
                if (tangents.Length == positions.Length)
                {
                    var tangent = tangents[i];
                    tangent[_axis] = -tangent[_axis];
                    tangent.w = -tangent.w;
                    tangents[i] = tangent;
                }
            }
            mesh.vertices = positions;
            if (normals.Length == positions.Length) mesh.normals = normals;
            if (tangents.Length == positions.Length) mesh.tangents = tangents;
            ReverseTriangleWinding(mesh, 0, false);
            mesh.RecalculateBounds();
            return new MeshOperationResult();
        }

        private MeshOperationResult ApplyCopy(Mesh mesh)
        {
            var source = MeshChannelSnapshot.Capture(mesh);
            int originalVertexCount = source.Positions.Length;
            var vertexSources = new List<MeshVertexSource>(originalVertexCount * 2);
            for (int i = 0; i < originalVertexCount; i++) vertexSources.Add(MeshVertexSource.Copy(i));
            for (int i = 0; i < originalVertexCount; i++) vertexSources.Add(MeshVertexSource.Copy(i));

            var originalName = mesh.name;
            mesh.Clear(false);
            mesh.indexFormat = originalVertexCount * 2 > ushort.MaxValue ? IndexFormat.UInt32 : source.IndexFormat;
            MeshChannelTransfer.ApplyInterpolated(source, mesh, vertexSources);
            var positions = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            for (int i = originalVertexCount; i < positions.Length; i++)
            {
                positions[i] = Reflected(positions[i]);
                if (normals.Length == positions.Length)
                {
                    var normal = normals[i];
                    normal[_axis] = -normal[_axis];
                    normals[i] = normal;
                }
                if (tangents.Length == positions.Length)
                {
                    var tangent = tangents[i];
                    tangent[_axis] = -tangent[_axis];
                    tangent.w = -tangent.w;
                    tangents[i] = tangent;
                }
            }
            mesh.vertices = positions;
            if (normals.Length == positions.Length) mesh.normals = normals;
            if (tangents.Length == positions.Length) mesh.tangents = tangents;
            mesh.subMeshCount = source.SubMeshes.Count;
            for (int subMesh = 0; subMesh < source.SubMeshes.Count; subMesh++)
            {
                var part = source.SubMeshes[subMesh];
                var indices = new int[part.Indices.Length * 2];
                Array.Copy(part.Indices, indices, part.Indices.Length);
                for (int i = 0; i < part.Indices.Length; i++)
                    indices[part.Indices.Length + i] = part.Indices[i] + originalVertexCount;
                if (part.Topology == MeshTopology.Triangles)
                {
                    for (int i = part.Indices.Length; i < indices.Length; i += 3)
                    {
                        var swap = indices[i + 1];
                        indices[i + 1] = indices[i + 2];
                        indices[i + 2] = swap;
                    }
                }
                mesh.SetIndices(indices, part.Topology, subMesh, false);
            }
            mesh.name = originalName;
            mesh.RecalculateBounds();
            return new MeshOperationResult
            {
                CreatedVertexCount = originalVertexCount,
                CreatedFaceCount = TriangleCount(source.SubMeshes),
            };
        }

        private Vector3 Reflected(Vector3 value)
        {
            value[_axis] = 2f * _plane - value[_axis];
            return value;
        }

        private static void ReverseTriangleWinding(Mesh mesh, int vertexOffset, bool onlyOffset)
        {
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                if (mesh.GetTopology(subMesh) != MeshTopology.Triangles) continue;
                var indices = mesh.GetIndices(subMesh, true);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    if (onlyOffset && indices[i] < vertexOffset) continue;
                    var swap = indices[i + 1];
                    indices[i + 1] = indices[i + 2];
                    indices[i + 2] = swap;
                }
                mesh.SetIndices(indices, MeshTopology.Triangles, subMesh, false);
            }
        }

        private static int TriangleCount(IReadOnlyList<MeshSubMeshSnapshot> subMeshes)
        {
            int count = 0;
            for (int i = 0; i < subMeshes.Count; i++)
                if (subMeshes[i].Topology == MeshTopology.Triangles) count += subMeshes[i].Indices.Length / 3;
            return count;
        }
    }

    public sealed class MirrorAndWeldMeshOperation : IRecordedMeshOperation
    {
        private readonly int _axis;
        private readonly float _plane;
        private readonly float _tolerance;
        private readonly bool _preserveSeams;

        public MeshOperationDescriptor Descriptor { get; } = new MeshOperationDescriptor(
            "mesh.mirror_weld", "Mirror mesh with welded seam", true, true, true, true, true);

        public MirrorAndWeldMeshOperation(
            int axis,
            float plane = 0f,
            float tolerance = 1e-5f,
            bool preserveSeams = true)
        {
            _axis = axis;
            _plane = plane;
            _tolerance = tolerance;
            _preserveSeams = preserveSeams;
        }

        public MeshValidationReport Validate(MeshOperationContext context)
        {
            var report = new MeshValidationReport();
            Append(report, new MirrorMeshOperation(_axis, _plane, true).Validate(context));
            Append(report, new WeldVerticesOperation(_tolerance, _preserveSeams).Validate(context));
            return report;
        }

        public MeshOperationResult Preview(MeshOperationContext context) => Apply(context);

        public MeshOperationResult Apply(MeshOperationContext context)
        {
            var mirror = new MirrorMeshOperation(_axis, _plane, true).Apply(context) ?? new MeshOperationResult();
            var weld = new WeldVerticesOperation(_tolerance, _preserveSeams).Apply(context) ?? new MeshOperationResult();
            return new MeshOperationResult
            {
                CreatedVertexCount = mirror.CreatedVertexCount + weld.CreatedVertexCount,
                RemovedVertexCount = mirror.RemovedVertexCount + weld.RemovedVertexCount,
                CreatedFaceCount = mirror.CreatedFaceCount + weld.CreatedFaceCount,
                RemovedFaceCount = mirror.RemovedFaceCount + weld.RemovedFaceCount,
            };
        }

        public string SerializeParameters()
            => JsonConvert.SerializeObject(new MirrorAndWeldParameters
            {
                axis = _axis,
                plane = _plane,
                tolerance = _tolerance,
                preserveSeams = _preserveSeams,
            });

        public static MirrorAndWeldMeshOperation Deserialize(string json)
        {
            var value = JsonConvert.DeserializeObject<MirrorAndWeldParameters>(json ?? "{}")
                ?? new MirrorAndWeldParameters();
            return new MirrorAndWeldMeshOperation(value.axis, value.plane, value.tolerance, value.preserveSeams);
        }

        private static void Append(MeshValidationReport target, MeshValidationReport source)
        {
            if (source == null) return;
            for (int i = 0; i < source.Issues.Count; i++)
            {
                var issue = source.Issues[i];
                if (issue.IsError) target.Error(issue.Code, issue.Message);
                else target.Warning(issue.Code, issue.Message);
            }
        }
    }
}
