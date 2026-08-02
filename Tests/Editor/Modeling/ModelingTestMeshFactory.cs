using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Tests
{
    internal sealed class ModelingTestCharacter : IDisposable
    {
        public GameObject Root { get; set; }
        public SkinnedMeshRenderer Renderer { get; set; }
        public Mesh Mesh { get; set; }

        public void Dispose()
        {
            if (Root != null) UnityEngine.Object.DestroyImmediate(Root);
            if (Mesh != null) UnityEngine.Object.DestroyImmediate(Mesh);
        }
    }

    internal static class ModelingTestMeshFactory
    {
        public static ModelingTestCharacter CreateSixPartCharacter()
        {
            var root = new GameObject("ModelingTestCharacter");
            var bones = new[]
            {
                CreateBone(root.transform, "Head", new Vector3(0f, 2f, 0f)),
                CreateBone(root.transform, "Spine", new Vector3(0f, 1f, 0f)),
                CreateBone(root.transform, "LeftUpperArm", new Vector3(-1f, 1.3f, 0f)),
                CreateBone(root.transform, "RightUpperArm", new Vector3(1f, 1.3f, 0f)),
                CreateBone(root.transform, "LeftUpperLeg", new Vector3(-0.35f, 0.3f, 0f)),
                CreateBone(root.transform, "RightUpperLeg", new Vector3(0.35f, 0.3f, 0f)),
            };
            var rendererObject = new GameObject("Body");
            rendererObject.transform.SetParent(root.transform, false);
            var renderer = rendererObject.AddComponent<SkinnedMeshRenderer>();
            var mesh = CreateSixPartMesh(bones.Length);
            renderer.sharedMesh = mesh;
            renderer.bones = bones;
            renderer.rootBone = bones[1];
            return new ModelingTestCharacter { Root = root, Renderer = renderer, Mesh = mesh };
        }

        public static Mesh CreateSixPartMesh(int boneCount)
        {
            var centers = new[]
            {
                new Vector3(0f, 2f, 0f),
                new Vector3(0f, 1f, 0f),
                new Vector3(-1f, 1.3f, 0f),
                new Vector3(1f, 1.3f, 0f),
                new Vector3(-0.35f, 0.25f, 0f),
                new Vector3(0.35f, 0.25f, 0f),
            };
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var tangents = new List<Vector4>();
            var colors = new List<Color32>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            var counts = new byte[centers.Length * 3];
            var weights = new BoneWeight1[counts.Length];
            for (int part = 0; part < centers.Length; part++)
            {
                var center = centers[part];
                int start = vertices.Count;
                vertices.Add(center + new Vector3(-0.08f, -0.06f, 0f));
                vertices.Add(center + new Vector3(0.08f, -0.06f, 0f));
                vertices.Add(center + new Vector3(0f, 0.09f, 0f));
                for (int i = 0; i < 3; i++)
                {
                    normals.Add(Vector3.forward);
                    tangents.Add(new Vector4(1f, 0f, 0f, 1f));
                    colors.Add(new Color32((byte)(20 + part * 30), (byte)(200 - part * 20), 120, 255));
                    uvs.Add(new Vector2(i == 1 ? 1f : 0f, i == 2 ? 1f : 0f));
                    counts[start + i] = 1;
                    weights[start + i] = new BoneWeight1 { boneIndex = Mathf.Min(part, boneCount - 1), weight = 1f };
                }
                triangles.Add(start);
                triangles.Add(start + 1);
                triangles.Add(start + 2);
            }

            var mesh = new Mesh { name = "SixPartSkinnedFixture" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTangents(tangents);
            mesh.SetColors(colors);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            var bindPoses = new Matrix4x4[boneCount];
            for (int i = 0; i < bindPoses.Length; i++) bindPoses[i] = Matrix4x4.identity;
            mesh.bindposes = bindPoses;
            using (var nativeCounts = new NativeArray<byte>(counts, Allocator.Temp))
            using (var nativeWeights = new NativeArray<BoneWeight1>(weights, Allocator.Temp))
                mesh.SetBoneWeights(nativeCounts, nativeWeights);

            var deltaVertices = new Vector3[vertices.Count];
            var deltaNormals = new Vector3[vertices.Count];
            var deltaTangents = new Vector3[vertices.Count];
            for (int i = 0; i < deltaVertices.Length; i++) deltaVertices[i] = new Vector3(0f, 0.01f * (i + 1), 0f);
            mesh.AddBlendShapeFrame("Breath", 100f, deltaVertices, deltaNormals, deltaTangents);
            mesh.RecalculateBounds();
            return mesh;
        }

        public static Mesh CreateGrid(int width, int height)
        {
            var vertices = new Vector3[(width + 1) * (height + 1)];
            var triangles = new int[width * height * 6];
            int vertex = 0;
            for (int y = 0; y <= height; y++)
                for (int x = 0; x <= width; x++) vertices[vertex++] = new Vector3(x, y, 0f);
            int index = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int a = y * (width + 1) + x;
                    int b = a + 1;
                    int c = a + width + 1;
                    int d = c + 1;
                    triangles[index++] = a;
                    triangles[index++] = d;
                    triangles[index++] = c;
                    triangles[index++] = a;
                    triangles[index++] = b;
                    triangles[index++] = d;
                }
            }
            var mesh = new Mesh { name = $"Grid_{width}x{height}" };
            if (vertices.Length > ushort.MaxValue) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Transform CreateBone(Transform parent, string name, Vector3 localPosition)
        {
            var bone = new GameObject(name).transform;
            bone.SetParent(parent, false);
            bone.localPosition = localPosition;
            return bone;
        }
    }
}
