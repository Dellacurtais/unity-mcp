using System;
using Maicon.SceneAnalyzer.Editor.Modeling.Commands;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.UI
{
    [InitializeOnLoad]
    internal static class MeshOperationPreviewController
    {
        private static MeshOperationPreview _preview;
        private static Renderer _renderer;

        static MeshOperationPreviewController()
        {
            SceneView.duringSceneGui += Draw;
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
        }

        public static void Show(MeshOperationPreview preview, Renderer renderer)
        {
            if (!ReferenceEquals(_preview, preview)) _preview?.Dispose();
            _preview = preview;
            _renderer = renderer;
            SceneView.RepaintAll();
        }

        public static MeshOperationPreview Detach()
        {
            var value = _preview;
            _preview = null;
            _renderer = null;
            SceneView.RepaintAll();
            return value;
        }

        public static void Clear()
        {
            _preview?.Dispose();
            _preview = null;
            _renderer = null;
            SceneView.RepaintAll();
        }

        private static void Draw(SceneView sceneView)
        {
            if (_preview?.PreviewMesh == null || _renderer == null) return;
            var mesh = _preview.PreviewMesh;
            var vertices = mesh.vertices;
            var matrix = _renderer.transform.localToWorldMatrix;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
            Handles.color = new Color(0.2f, 0.85f, 1f, 0.8f);
            int drawn = 0;
            const int edgeBudget = 30000;
            for (int subMesh = 0; subMesh < mesh.subMeshCount && drawn < edgeBudget; subMesh++)
            {
                if (mesh.GetTopology(subMesh) != MeshTopology.Triangles) continue;
                var indices = mesh.GetIndices(subMesh, true);
                int stride = Math.Max(3, Mathf.CeilToInt(indices.Length / (float)edgeBudget) * 3);
                for (int i = 0; i < indices.Length && drawn < edgeBudget; i += stride)
                {
                    if (i + 2 >= indices.Length) break;
                    var a = matrix.MultiplyPoint3x4(vertices[indices[i]]);
                    var b = matrix.MultiplyPoint3x4(vertices[indices[i + 1]]);
                    var c = matrix.MultiplyPoint3x4(vertices[indices[i + 2]]);
                    Handles.DrawLine(a, b); Handles.DrawLine(b, c); Handles.DrawLine(c, a);
                    drawn += 3;
                }
            }
        }
    }
}
