using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Modeling.Commands;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Operations.Topology
{
    internal static class TopologyOperationUtility
    {
        public static HashSet<int> Selection(IReadOnlyList<int> faces, int faceCount)
        {
            var selected = new HashSet<int>();
            if (faces == null || faces.Count == 0)
            {
                for (int i = 0; i < faceCount; i++) selected.Add(i);
                return selected;
            }
            for (int i = 0; i < faces.Count; i++) selected.Add(faces[i]);
            return selected;
        }

        public static MeshValidationReport ValidateTriangleSelection(Mesh mesh, IReadOnlyList<int> faces)
        {
            var report = new MeshValidationReport();
            var topology = HalfEdgeTopology.Build(mesh);
            if (topology.Faces.Count == 0) report.Error("NO_TRIANGLES", "Working mesh contains no triangle faces.");
            if (faces != null)
                for (int i = 0; i < faces.Count; i++)
                    if (faces[i] < 0 || faces[i] >= topology.Faces.Count)
                    {
                        report.Error("FACE_OUT_OF_RANGE", $"Face {faces[i]} is outside the working mesh.");
                        break;
                    }
            return report;
        }

        public static void ReplaceMesh(
            Mesh mesh,
            MeshChannelSnapshot source,
            IReadOnlyList<MeshVertexSource> vertices,
            IReadOnlyList<List<int>> subMeshIndices)
        {
            var name = mesh.name;
            mesh.Clear(false);
            mesh.indexFormat = vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : source.IndexFormat;
            MeshChannelTransfer.ApplyInterpolated(source, mesh, vertices);
            mesh.subMeshCount = source.SubMeshes.Count;
            for (int subMesh = 0; subMesh < source.SubMeshes.Count; subMesh++)
                mesh.SetIndices(subMeshIndices[subMesh], source.SubMeshes[subMesh].Topology, subMesh, false);
            mesh.name = name;
            mesh.RecalculateBounds();
        }

        public static List<MeshVertexSource> OriginalVertexSources(int vertexCount)
        {
            var result = new List<MeshVertexSource>(vertexCount);
            for (int i = 0; i < vertexCount; i++) result.Add(MeshVertexSource.Copy(i));
            return result;
        }
    }

    [Serializable]
    internal sealed class FaceSelectionParameters
    {
        public int[] faces;
    }

    public sealed class SubdivideTrianglesOperation : IRecordedMeshOperation
    {
        private readonly int[] _faces;

        public MeshOperationDescriptor Descriptor { get; } = new MeshOperationDescriptor(
            "mesh.subdivide", "Subdivide triangles", true, true, true, true, true);

        public SubdivideTrianglesOperation(IReadOnlyList<int> faces = null)
        {
            _faces = Copy(faces);
        }

        public MeshValidationReport Validate(MeshOperationContext context)
            => TopologyOperationUtility.ValidateTriangleSelection(context.WorkingMesh, _faces);

        public MeshOperationResult Preview(MeshOperationContext context) => Apply(context);

        public MeshOperationResult Apply(MeshOperationContext context)
        {
            var mesh = context.WorkingMesh;
            var snapshot = MeshChannelSnapshot.Capture(mesh);
            var topology = HalfEdgeTopology.Build(mesh);
            var selection = TopologyOperationUtility.Selection(_faces, topology.Faces.Count);
            var faceLookup = new Dictionary<long, int>();
            for (int i = 0; i < topology.Faces.Count; i++)
            {
                var face = topology.Faces[i];
                faceLookup[Key(face.SubMesh, face.TriangleIndex)] = face.Index;
            }
            var vertexSources = TopologyOperationUtility.OriginalVertexSources(snapshot.Positions.Length);
            var output = new List<int>[snapshot.SubMeshes.Count];
            int subdivided = 0;
            for (int subMesh = 0; subMesh < snapshot.SubMeshes.Count; subMesh++)
            {
                var source = snapshot.SubMeshes[subMesh];
                output[subMesh] = new List<int>(source.Indices.Length * 2);
                if (source.Topology != MeshTopology.Triangles)
                {
                    output[subMesh].AddRange(source.Indices);
                    continue;
                }
                for (int index = 0; index < source.Indices.Length; index += 3)
                {
                    if ((index & 8191) == 0) context.CancellationToken.ThrowIfCancellationRequested();
                    int a = source.Indices[index];
                    int b = source.Indices[index + 1];
                    int c = source.Indices[index + 2];
                    int faceIndex = faceLookup[Key(subMesh, index / 3)];
                    if (!selection.Contains(faceIndex))
                    {
                        output[subMesh].Add(a); output[subMesh].Add(b); output[subMesh].Add(c);
                        continue;
                    }
                    int center = vertexSources.Count;
                    vertexSources.Add(MeshVertexSource.TriangleCenter(a, b, c));
                    output[subMesh].Add(a); output[subMesh].Add(b); output[subMesh].Add(center);
                    output[subMesh].Add(b); output[subMesh].Add(c); output[subMesh].Add(center);
                    output[subMesh].Add(c); output[subMesh].Add(a); output[subMesh].Add(center);
                    subdivided++;
                }
            }
            TopologyOperationUtility.ReplaceMesh(mesh, snapshot, vertexSources, output);
            return new MeshOperationResult { CreatedVertexCount = subdivided, CreatedFaceCount = subdivided * 2 };
        }

        public string SerializeParameters()
            => JsonConvert.SerializeObject(new FaceSelectionParameters { faces = _faces });

        public static SubdivideTrianglesOperation Deserialize(string json)
        {
            var value = JsonConvert.DeserializeObject<FaceSelectionParameters>(json ?? "{}") ?? new FaceSelectionParameters();
            return new SubdivideTrianglesOperation(value.faces);
        }

        private static int[] Copy(IReadOnlyList<int> source)
        {
            if (source == null) return Array.Empty<int>();
            var result = new int[source.Count];
            for (int i = 0; i < source.Count; i++) result[i] = source[i];
            return result;
        }

        private static long Key(int subMesh, int triangle) => ((long)subMesh << 32) | (uint)triangle;
    }

    [Serializable]
    internal sealed class ExtrudeParameters
    {
        public int[] faces;
        public float distance;
    }

    public sealed class ExtrudeFacesOperation : IRecordedMeshOperation
    {
        private readonly int[] _faces;
        private readonly float _distance;

        public MeshOperationDescriptor Descriptor { get; } = new MeshOperationDescriptor(
            "mesh.extrude_faces", "Extrude faces", true, true, true, true, true);

        public ExtrudeFacesOperation(IReadOnlyList<int> faces, float distance)
        {
            _faces = Copy(faces);
            _distance = distance;
        }

        public MeshValidationReport Validate(MeshOperationContext context)
        {
            var report = TopologyOperationUtility.ValidateTriangleSelection(context.WorkingMesh, _faces);
            if (_faces.Length == 0) report.Error("FACE_SELECTION_REQUIRED", "Extrude requires one or more selected faces.");
            if (float.IsNaN(_distance) || float.IsInfinity(_distance)) report.Error("DISTANCE_INVALID", "Extrude distance is not finite.");
            return report;
        }

        public MeshOperationResult Preview(MeshOperationContext context) => Apply(context);

        public MeshOperationResult Apply(MeshOperationContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var mesh = context.WorkingMesh;
            var snapshot = MeshChannelSnapshot.Capture(mesh);
            var topology = HalfEdgeTopology.Build(mesh);
            var selected = TopologyOperationUtility.Selection(_faces, topology.Faces.Count);
            var selectedVertices = new SortedSet<int>();
            var normals = new Dictionary<int, Vector3>();
            var edgeUses = new Dictionary<MeshEdgeKey, EdgeUse>();
            foreach (var faceIndex in selected)
            {
                var face = topology.Faces[faceIndex];
                var normal = Vector3.Cross(
                    snapshot.Positions[face.B] - snapshot.Positions[face.A],
                    snapshot.Positions[face.C] - snapshot.Positions[face.A]).normalized;
                Accumulate(normals, face.A, normal); Accumulate(normals, face.B, normal); Accumulate(normals, face.C, normal);
                selectedVertices.Add(face.A); selectedVertices.Add(face.B); selectedVertices.Add(face.C);
                AddEdge(edgeUses, face.A, face.B, face.SubMesh);
                AddEdge(edgeUses, face.B, face.C, face.SubMesh);
                AddEdge(edgeUses, face.C, face.A, face.SubMesh);
            }

            var vertexSources = TopologyOperationUtility.OriginalVertexSources(snapshot.Positions.Length);
            var duplicate = new Dictionary<int, int>();
            foreach (int vertex in selectedVertices)
            {
                duplicate[vertex] = vertexSources.Count;
                vertexSources.Add(MeshVertexSource.Copy(vertex));
            }

            var outputs = new List<int>[snapshot.SubMeshes.Count];
            for (int subMesh = 0; subMesh < outputs.Length; subMesh++) outputs[subMesh] = new List<int>();
            for (int faceIndex = 0; faceIndex < topology.Faces.Count; faceIndex++)
            {
                var face = topology.Faces[faceIndex];
                var output = outputs[face.SubMesh];
                if (selected.Contains(faceIndex))
                {
                    output.Add(duplicate[face.A]); output.Add(duplicate[face.B]); output.Add(duplicate[face.C]);
                }
                else
                {
                    output.Add(face.A); output.Add(face.B); output.Add(face.C);
                }
            }
            CopyNonTriangleSubMeshes(snapshot, outputs);

            int boundaryEdges = 0;
            foreach (var pair in edgeUses)
            {
                if (pair.Value.Count != 1) continue;
                boundaryEdges++;
                var edge = pair.Value;
                var output = outputs[edge.SubMesh];
                int newA = duplicate[edge.A];
                int newB = duplicate[edge.B];
                output.Add(edge.A); output.Add(edge.B); output.Add(newB);
                output.Add(edge.A); output.Add(newB); output.Add(newA);
            }

            TopologyOperationUtility.ReplaceMesh(mesh, snapshot, vertexSources, outputs);
            var positions = mesh.vertices;
            foreach (var pair in duplicate)
            {
                var normal = normals[pair.Key];
                positions[pair.Value] += (normal.sqrMagnitude > 1e-12f ? normal.normalized : Vector3.up) * _distance;
            }
            mesh.vertices = positions;
            mesh.RecalculateBounds();
            return new MeshOperationResult
            {
                CreatedVertexCount = duplicate.Count,
                CreatedFaceCount = boundaryEdges * 2,
            };
        }

        public string SerializeParameters()
            => JsonConvert.SerializeObject(new ExtrudeParameters { faces = _faces, distance = _distance });

        public static ExtrudeFacesOperation Deserialize(string json)
        {
            var value = JsonConvert.DeserializeObject<ExtrudeParameters>(json ?? "{}") ?? new ExtrudeParameters();
            return new ExtrudeFacesOperation(value.faces, value.distance);
        }

        private static void Accumulate(Dictionary<int, Vector3> values, int vertex, Vector3 normal)
        {
            values.TryGetValue(vertex, out var current);
            values[vertex] = current + normal;
        }

        private static void AddEdge(Dictionary<MeshEdgeKey, EdgeUse> edges, int a, int b, int subMesh)
        {
            var key = new MeshEdgeKey(a, b);
            if (edges.TryGetValue(key, out var edge))
            {
                edge.Count++;
                edges[key] = edge;
            }
            else edges[key] = new EdgeUse { A = a, B = b, SubMesh = subMesh, Count = 1 };
        }

        private static void CopyNonTriangleSubMeshes(MeshChannelSnapshot snapshot, List<int>[] outputs)
        {
            for (int subMesh = 0; subMesh < snapshot.SubMeshes.Count; subMesh++)
                if (snapshot.SubMeshes[subMesh].Topology != MeshTopology.Triangles)
                    outputs[subMesh].AddRange(snapshot.SubMeshes[subMesh].Indices);
        }

        private static int[] Copy(IReadOnlyList<int> source)
        {
            if (source == null) return Array.Empty<int>();
            var result = new int[source.Count];
            for (int i = 0; i < source.Count; i++) result[i] = source[i];
            return result;
        }

        private struct EdgeUse
        {
            public int A;
            public int B;
            public int SubMesh;
            public int Count;
        }
    }

    [Serializable]
    internal sealed class BevelFacesParameters
    {
        public int[] faces;
        public float inset;
        public float height;
    }

    public sealed class BevelFacesOperation : IRecordedMeshOperation
    {
        private readonly int[] _faces;
        private readonly float _inset;
        private readonly float _height;

        public MeshOperationDescriptor Descriptor { get; } = new MeshOperationDescriptor(
            "mesh.bevel_faces", "Inset / bevel faces", true, true, true, true, true);

        public BevelFacesOperation(IReadOnlyList<int> faces, float inset, float height = 0f)
        {
            _faces = faces == null ? Array.Empty<int>() : new List<int>(faces).ToArray();
            _inset = Mathf.Clamp01(inset);
            _height = height;
        }

        public MeshValidationReport Validate(MeshOperationContext context)
        {
            var report = TopologyOperationUtility.ValidateTriangleSelection(context.WorkingMesh, _faces);
            if (_faces.Length == 0) report.Error("FACE_SELECTION_REQUIRED", "Inset/bevel requires selected faces.");
            return report;
        }

        public MeshOperationResult Preview(MeshOperationContext context) => Apply(context);

        public MeshOperationResult Apply(MeshOperationContext context)
        {
            var mesh = context.WorkingMesh;
            var snapshot = MeshChannelSnapshot.Capture(mesh);
            var topology = HalfEdgeTopology.Build(mesh);
            var selected = TopologyOperationUtility.Selection(_faces, topology.Faces.Count);
            var sources = TopologyOperationUtility.OriginalVertexSources(snapshot.Positions.Length);
            var outputs = new List<int>[snapshot.SubMeshes.Count];
            for (int i = 0; i < outputs.Length; i++) outputs[i] = new List<int>();
            int addedVertices = 0;
            int addedFaces = 0;
            for (int faceIndex = 0; faceIndex < topology.Faces.Count; faceIndex++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var face = topology.Faces[faceIndex];
                var output = outputs[face.SubMesh];
                if (!selected.Contains(faceIndex))
                {
                    output.Add(face.A); output.Add(face.B); output.Add(face.C);
                    continue;
                }
                var centerWeight = _inset / 3f;
                int ia = sources.Count;
                sources.Add(new MeshVertexSource(new[] { face.A, face.B, face.C }, new[] { 1f - _inset + centerWeight, centerWeight, centerWeight }));
                int ib = sources.Count;
                sources.Add(new MeshVertexSource(new[] { face.A, face.B, face.C }, new[] { centerWeight, 1f - _inset + centerWeight, centerWeight }));
                int ic = sources.Count;
                sources.Add(new MeshVertexSource(new[] { face.A, face.B, face.C }, new[] { centerWeight, centerWeight, 1f - _inset + centerWeight }));
                output.Add(ia); output.Add(ib); output.Add(ic);
                output.Add(face.A); output.Add(face.B); output.Add(ib);
                output.Add(face.A); output.Add(ib); output.Add(ia);
                output.Add(face.B); output.Add(face.C); output.Add(ic);
                output.Add(face.B); output.Add(ic); output.Add(ib);
                output.Add(face.C); output.Add(face.A); output.Add(ia);
                output.Add(face.C); output.Add(ia); output.Add(ic);
                addedVertices += 3;
                addedFaces += 6;
            }
            for (int subMesh = 0; subMesh < snapshot.SubMeshes.Count; subMesh++)
                if (snapshot.SubMeshes[subMesh].Topology != MeshTopology.Triangles)
                    outputs[subMesh].AddRange(snapshot.SubMeshes[subMesh].Indices);
            TopologyOperationUtility.ReplaceMesh(mesh, snapshot, sources, outputs);
            if (Mathf.Abs(_height) > 1e-12f)
            {
                var positions = mesh.vertices;
                var originalNormals = snapshot.Normals;
                for (int i = snapshot.Positions.Length; i < positions.Length; i++)
                {
                    var source = sources[i];
                    var normal = Vector3.zero;
                    for (int contributor = 0; contributor < source.Indices.Length; contributor++)
                        normal += originalNormals.Length == snapshot.Positions.Length
                            ? originalNormals[source.Indices[contributor]] * source.Weights[contributor]
                            : Vector3.up * source.Weights[contributor];
                    positions[i] += normal.normalized * _height;
                }
                mesh.vertices = positions;
                mesh.RecalculateBounds();
            }
            return new MeshOperationResult { CreatedVertexCount = addedVertices, CreatedFaceCount = addedFaces };
        }

        public string SerializeParameters()
            => JsonConvert.SerializeObject(new BevelFacesParameters { faces = _faces, inset = _inset, height = _height });

        public static BevelFacesOperation Deserialize(string json)
        {
            var value = JsonConvert.DeserializeObject<BevelFacesParameters>(json ?? "{}") ?? new BevelFacesParameters();
            return new BevelFacesOperation(value.faces, value.inset, value.height);
        }
    }

    [Serializable]
    internal sealed class BridgeParameters
    {
        public int[] loopA;
        public int[] loopB;
        public int subMesh;
        public bool closed;
    }

    public sealed class BridgeLoopsOperation : IRecordedMeshOperation
    {
        private readonly int[] _loopA;
        private readonly int[] _loopB;
        private readonly int _subMesh;
        private readonly bool _closed;

        public MeshOperationDescriptor Descriptor { get; } = new MeshOperationDescriptor(
            "mesh.bridge_loops", "Bridge vertex loops", true, true, true, true, true);

        public BridgeLoopsOperation(IReadOnlyList<int> loopA, IReadOnlyList<int> loopB, int subMesh = 0, bool closed = true)
        {
            _loopA = loopA == null ? Array.Empty<int>() : new List<int>(loopA).ToArray();
            _loopB = loopB == null ? Array.Empty<int>() : new List<int>(loopB).ToArray();
            _subMesh = subMesh;
            _closed = closed;
        }

        public MeshValidationReport Validate(MeshOperationContext context)
        {
            var report = new MeshValidationReport();
            if (_loopA.Length < 2 || _loopA.Length != _loopB.Length)
                report.Error("LOOP_SIZE_MISMATCH", "Bridge loops must contain the same number of at least two vertices.");
            if (_subMesh < 0 || _subMesh >= context.WorkingMesh.subMeshCount)
                report.Error("SUBMESH_OUT_OF_RANGE", "Bridge target submesh is invalid.");
            for (int i = 0; i < _loopA.Length; i++)
                if (_loopA[i] < 0 || _loopA[i] >= context.WorkingMesh.vertexCount
                    || _loopB[i] < 0 || _loopB[i] >= context.WorkingMesh.vertexCount)
                {
                    report.Error("VERTEX_OUT_OF_RANGE", "Bridge loop references an invalid vertex.");
                    break;
                }
            return report;
        }

        public MeshOperationResult Preview(MeshOperationContext context) => Apply(context);

        public MeshOperationResult Apply(MeshOperationContext context)
        {
            var mesh = context.WorkingMesh;
            var snapshot = MeshChannelSnapshot.Capture(mesh);
            var outputs = new List<int>[snapshot.SubMeshes.Count];
            for (int i = 0; i < outputs.Length; i++) outputs[i] = new List<int>(snapshot.SubMeshes[i].Indices);
            int segments = _closed ? _loopA.Length : _loopA.Length - 1;
            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % _loopA.Length;
                outputs[_subMesh].Add(_loopA[i]); outputs[_subMesh].Add(_loopA[next]); outputs[_subMesh].Add(_loopB[next]);
                outputs[_subMesh].Add(_loopA[i]); outputs[_subMesh].Add(_loopB[next]); outputs[_subMesh].Add(_loopB[i]);
            }
            TopologyOperationUtility.ReplaceMesh(mesh, snapshot, TopologyOperationUtility.OriginalVertexSources(snapshot.Positions.Length), outputs);
            return new MeshOperationResult { CreatedFaceCount = segments * 2 };
        }

        public string SerializeParameters()
            => JsonConvert.SerializeObject(new BridgeParameters { loopA = _loopA, loopB = _loopB, subMesh = _subMesh, closed = _closed });

        public static BridgeLoopsOperation Deserialize(string json)
        {
            var value = JsonConvert.DeserializeObject<BridgeParameters>(json ?? "{}") ?? new BridgeParameters();
            return new BridgeLoopsOperation(value.loopA, value.loopB, value.subMesh, value.closed);
        }
    }

    [Serializable]
    internal sealed class FillHoleParameters
    {
        public int[] loop;
        public int subMesh;
    }

    public sealed class FillHoleOperation : IRecordedMeshOperation
    {
        private readonly int[] _loop;
        private readonly int _subMesh;

        public MeshOperationDescriptor Descriptor { get; } = new MeshOperationDescriptor(
            "mesh.fill_hole", "Fill boundary hole", true, true, true, true, true);

        public FillHoleOperation(IReadOnlyList<int> loop, int subMesh = 0)
        {
            _loop = loop == null ? Array.Empty<int>() : new List<int>(loop).ToArray();
            _subMesh = subMesh;
        }

        public MeshValidationReport Validate(MeshOperationContext context)
        {
            var report = new MeshValidationReport();
            if (_loop.Length < 3) report.Error("LOOP_TOO_SHORT", "Fill hole requires at least three boundary vertices.");
            if (_subMesh < 0 || _subMesh >= context.WorkingMesh.subMeshCount)
                report.Error("SUBMESH_OUT_OF_RANGE", "Fill target submesh is invalid.");
            var topology = HalfEdgeTopology.Build(context.WorkingMesh);
            var boundary = new HashSet<MeshEdgeKey>(topology.BoundaryEdges);
            for (int i = 0; i < _loop.Length; i++)
            {
                int next = (i + 1) % _loop.Length;
                if (!boundary.Contains(new MeshEdgeKey(_loop[i], _loop[next])))
                {
                    report.Error("LOOP_NOT_BOUNDARY", $"Edge {_loop[i]}-{_loop[next]} is not an open boundary.");
                    break;
                }
            }
            return report;
        }

        public MeshOperationResult Preview(MeshOperationContext context) => Apply(context);

        public MeshOperationResult Apply(MeshOperationContext context)
        {
            var mesh = context.WorkingMesh;
            var snapshot = MeshChannelSnapshot.Capture(mesh);
            var sources = TopologyOperationUtility.OriginalVertexSources(snapshot.Positions.Length);
            var weights = new float[_loop.Length];
            for (int i = 0; i < weights.Length; i++) weights[i] = 1f / weights.Length;
            int center = sources.Count;
            sources.Add(new MeshVertexSource((int[])_loop.Clone(), weights));
            var outputs = new List<int>[snapshot.SubMeshes.Count];
            for (int i = 0; i < outputs.Length; i++) outputs[i] = new List<int>(snapshot.SubMeshes[i].Indices);
            for (int i = 0; i < _loop.Length; i++)
            {
                int next = (i + 1) % _loop.Length;
                outputs[_subMesh].Add(_loop[i]); outputs[_subMesh].Add(_loop[next]); outputs[_subMesh].Add(center);
            }
            TopologyOperationUtility.ReplaceMesh(mesh, snapshot, sources, outputs);
            return new MeshOperationResult { CreatedVertexCount = 1, CreatedFaceCount = _loop.Length };
        }

        public string SerializeParameters()
            => JsonConvert.SerializeObject(new FillHoleParameters { loop = _loop, subMesh = _subMesh });

        public static FillHoleOperation Deserialize(string json)
        {
            var value = JsonConvert.DeserializeObject<FillHoleParameters>(json ?? "{}") ?? new FillHoleParameters();
            return new FillHoleOperation(value.loop, value.subMesh);
        }
    }

    [Serializable]
    internal sealed class ExtrudeEdgesParameters
    {
        public int[] edgeVertices;
        public float distance;
        public int subMesh;
    }

    public sealed class ExtrudeBoundaryEdgesOperation : IRecordedMeshOperation
    {
        private readonly int[] _edgeVertices;
        private readonly float _distance;
        private readonly int _subMesh;

        public MeshOperationDescriptor Descriptor { get; } = new MeshOperationDescriptor(
            "mesh.extrude_edges", "Extrude boundary edges", true, true, true, true, true);

        public ExtrudeBoundaryEdgesOperation(IReadOnlyList<int> edgeVertices, float distance, int subMesh = 0)
        {
            _edgeVertices = edgeVertices == null ? Array.Empty<int>() : new List<int>(edgeVertices).ToArray();
            _distance = distance;
            _subMesh = subMesh;
        }

        public MeshValidationReport Validate(MeshOperationContext context)
        {
            var report = new MeshValidationReport();
            if (_edgeVertices.Length < 2 || _edgeVertices.Length % 2 != 0)
                report.Error("EDGE_PAIR_COUNT", "Edge extrusion requires vertex pairs: a,b,c,d,...");
            if (_subMesh < 0 || _subMesh >= context.WorkingMesh.subMeshCount)
                report.Error("SUBMESH_OUT_OF_RANGE", "Edge extrusion target submesh is invalid.");
            if (float.IsNaN(_distance) || float.IsInfinity(_distance))
                report.Error("EXTRUDE_DISTANCE_INVALID", "Edge extrusion distance must be finite.");
            var topology = HalfEdgeTopology.Build(context.WorkingMesh);
            var boundary = new HashSet<MeshEdgeKey>(topology.BoundaryEdges);
            for (int i = 0; i + 1 < _edgeVertices.Length; i += 2)
            {
                int a = _edgeVertices[i], b = _edgeVertices[i + 1];
                if (a < 0 || b < 0 || a >= context.WorkingMesh.vertexCount || b >= context.WorkingMesh.vertexCount)
                {
                    report.Error("VERTEX_OUT_OF_RANGE", $"Edge {a}-{b} references an invalid vertex.");
                    break;
                }
                if (!boundary.Contains(new MeshEdgeKey(a, b)))
                {
                    report.Error("EDGE_NOT_BOUNDARY", $"Edge {a}-{b} is not an open boundary edge.");
                    break;
                }
            }
            return report;
        }

        public MeshOperationResult Preview(MeshOperationContext context) => Apply(context);

        public MeshOperationResult Apply(MeshOperationContext context)
        {
            var mesh = context.WorkingMesh;
            var snapshot = MeshChannelSnapshot.Capture(mesh);
            var sources = TopologyOperationUtility.OriginalVertexSources(snapshot.Positions.Length);
            var duplicate = new Dictionary<int, int>();
            for (int i = 0; i < _edgeVertices.Length; i++)
            {
                int vertex = _edgeVertices[i];
                if (duplicate.ContainsKey(vertex)) continue;
                duplicate.Add(vertex, sources.Count);
                sources.Add(MeshVertexSource.Copy(vertex));
            }
            var outputs = new List<int>[snapshot.SubMeshes.Count];
            for (int i = 0; i < outputs.Length; i++) outputs[i] = new List<int>(snapshot.SubMeshes[i].Indices);
            for (int i = 0; i < _edgeVertices.Length; i += 2)
            {
                int a = _edgeVertices[i], b = _edgeVertices[i + 1];
                int extrudedA = duplicate[a], extrudedB = duplicate[b];
                outputs[_subMesh].Add(a); outputs[_subMesh].Add(b); outputs[_subMesh].Add(extrudedB);
                outputs[_subMesh].Add(a); outputs[_subMesh].Add(extrudedB); outputs[_subMesh].Add(extrudedA);
            }
            TopologyOperationUtility.ReplaceMesh(mesh, snapshot, sources, outputs);
            var positions = mesh.vertices;
            var normals = snapshot.Normals;
            foreach (var pair in duplicate)
            {
                var normal = normals.Length == snapshot.Positions.Length ? normals[pair.Key] : Vector3.up;
                positions[pair.Value] += normal.normalized * _distance;
            }
            mesh.vertices = positions;
            mesh.RecalculateBounds();
            return new MeshOperationResult
            {
                CreatedVertexCount = duplicate.Count,
                CreatedFaceCount = _edgeVertices.Length,
            };
        }

        public string SerializeParameters()
            => JsonConvert.SerializeObject(new ExtrudeEdgesParameters { edgeVertices = _edgeVertices, distance = _distance, subMesh = _subMesh });

        public static ExtrudeBoundaryEdgesOperation Deserialize(string json)
        {
            var value = JsonConvert.DeserializeObject<ExtrudeEdgesParameters>(json ?? "{}") ?? new ExtrudeEdgesParameters();
            return new ExtrudeBoundaryEdgesOperation(value.edgeVertices, value.distance, value.subMesh);
        }
    }
}
