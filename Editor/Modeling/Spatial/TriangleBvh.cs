using System;
using System.Collections.Generic;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Spatial
{
    public readonly struct ClosestTrianglePoint
    {
        public bool Found { get; }
        public Vector3 Point { get; }
        public Vector3 Normal { get; }
        public float Distance { get; }
        public int TriangleIndex { get; }

        public ClosestTrianglePoint(bool found, Vector3 point, Vector3 normal, float distance, int triangleIndex)
        {
            Found = found;
            Point = point;
            Normal = normal;
            Distance = distance;
            TriangleIndex = triangleIndex;
        }
    }

    public sealed class TriangleBvh
    {
        private const int LeafSize = 8;

        private struct Triangle
        {
            public Vector3 A;
            public Vector3 B;
            public Vector3 C;
            public Vector3 Centroid;
            public Vector3 Normal;
            public Bounds Bounds;
            public int Index;
        }

        private struct Node
        {
            public Bounds Bounds;
            public int Left;
            public int Right;
            public int Start;
            public int Count;
            public bool IsLeaf => Count > 0;
        }

        private readonly List<Triangle> _triangles = new List<Triangle>();
        private readonly List<Node> _nodes = new List<Node>();

        public int TriangleCount => _triangles.Count;

        public TriangleBvh(Mesh mesh, Matrix4x4 transform)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            var vertices = mesh.vertices;
            int triangleIndex = 0;
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                if (mesh.GetTopology(subMesh) != MeshTopology.Triangles) continue;
                var indices = mesh.GetIndices(subMesh, true);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    var a = transform.MultiplyPoint3x4(vertices[indices[i]]);
                    var b = transform.MultiplyPoint3x4(vertices[indices[i + 1]]);
                    var c = transform.MultiplyPoint3x4(vertices[indices[i + 2]]);
                    var bounds = new Bounds(a, Vector3.zero);
                    bounds.Encapsulate(b);
                    bounds.Encapsulate(c);
                    var cross = Vector3.Cross(b - a, c - a);
                    _triangles.Add(new Triangle
                    {
                        A = a,
                        B = b,
                        C = c,
                        Centroid = (a + b + c) / 3f,
                        Normal = cross.sqrMagnitude > 1e-16f ? cross.normalized : Vector3.up,
                        Bounds = bounds,
                        Index = triangleIndex++,
                    });
                }
            }
            if (_triangles.Count > 0) BuildNode(0, _triangles.Count);
        }

        public ClosestTrianglePoint FindClosest(Vector3 point, float maxDistance = float.PositiveInfinity)
        {
            if (_nodes.Count == 0) return new ClosestTrianglePoint(false, default, default, float.PositiveInfinity, -1);
            float bestSquared = float.IsInfinity(maxDistance) ? float.PositiveInfinity : maxDistance * maxDistance;
            var bestPoint = Vector3.zero;
            var bestNormal = Vector3.up;
            int bestTriangle = -1;
            var stack = new Stack<int>();
            stack.Push(0);
            while (stack.Count > 0)
            {
                var nodeIndex = stack.Pop();
                var node = _nodes[nodeIndex];
                if (SquaredDistance(point, node.Bounds) > bestSquared) continue;
                if (node.IsLeaf)
                {
                    for (int i = node.Start; i < node.Start + node.Count; i++)
                    {
                        var triangle = _triangles[i];
                        var closest = ClosestPointOnTriangle(point, triangle.A, triangle.B, triangle.C);
                        float squared = (closest - point).sqrMagnitude;
                        if (squared >= bestSquared) continue;
                        bestSquared = squared;
                        bestPoint = closest;
                        bestNormal = triangle.Normal;
                        bestTriangle = triangle.Index;
                    }
                    continue;
                }

                float leftDistance = SquaredDistance(point, _nodes[node.Left].Bounds);
                float rightDistance = SquaredDistance(point, _nodes[node.Right].Bounds);
                if (leftDistance < rightDistance)
                {
                    stack.Push(node.Right);
                    stack.Push(node.Left);
                }
                else
                {
                    stack.Push(node.Left);
                    stack.Push(node.Right);
                }
            }
            return bestTriangle >= 0
                ? new ClosestTrianglePoint(true, bestPoint, bestNormal, Mathf.Sqrt(bestSquared), bestTriangle)
                : new ClosestTrianglePoint(false, default, default, float.PositiveInfinity, -1);
        }

        private int BuildNode(int start, int count)
        {
            var bounds = _triangles[start].Bounds;
            var centroidBounds = new Bounds(_triangles[start].Centroid, Vector3.zero);
            for (int i = start + 1; i < start + count; i++)
            {
                bounds.Encapsulate(_triangles[i].Bounds);
                centroidBounds.Encapsulate(_triangles[i].Centroid);
            }
            int nodeIndex = _nodes.Count;
            _nodes.Add(default);
            if (count <= LeafSize)
            {
                _nodes[nodeIndex] = new Node { Bounds = bounds, Start = start, Count = count, Left = -1, Right = -1 };
                return nodeIndex;
            }
            var size = centroidBounds.size;
            int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            _triangles.Sort(start, count, new TriangleCentroidComparer(axis));
            int leftCount = count / 2;
            int left = BuildNode(start, leftCount);
            int right = BuildNode(start + leftCount, count - leftCount);
            _nodes[nodeIndex] = new Node { Bounds = bounds, Left = left, Right = right, Start = -1, Count = 0 };
            return nodeIndex;
        }

        private sealed class TriangleCentroidComparer : IComparer<Triangle>
        {
            private readonly int _axis;
            public TriangleCentroidComparer(int axis) { _axis = axis; }
            public int Compare(Triangle left, Triangle right) => left.Centroid[_axis].CompareTo(right.Centroid[_axis]);
        }

        private static float SquaredDistance(Vector3 point, Bounds bounds)
        {
            var closest = bounds.ClosestPoint(point);
            return (closest - point).sqrMagnitude;
        }

        // Ericson, Real-Time Collision Detection, closest point on triangle.
        private static Vector3 ClosestPointOnTriangle(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a;
            var ac = c - a;
            var ap = point - a;
            float d1 = Vector3.Dot(ab, ap);
            float d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;

            var bp = point - b;
            float d3 = Vector3.Dot(ab, bp);
            float d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                return a + v * ab;
            }

            var cp = point - c;
            float d5 = Vector3.Dot(ab, cp);
            float d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                return a + w * ac;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return b + w * (c - b);
            }
            float denominator = 1f / (va + vb + vc);
            float baryV = vb * denominator;
            float baryW = vc * denominator;
            return a + ab * baryV + ac * baryW;
        }
    }
}
