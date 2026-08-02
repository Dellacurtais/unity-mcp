using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Modeling.SmartSplit;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.UI
{
    [InitializeOnLoad]
    public static class SmartBodySplitPreviewController
    {
        private const int MaxPreviewTriangles = 12000;
        private static SmartBodySplitProposal _proposal;
        private static Mesh _bakedMesh;

        static SmartBodySplitPreviewController()
        {
            SceneView.duringSceneGui += Draw;
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
        }

        public static SmartBodySplitProposal ActiveProposal => _proposal;

        public static void Show(SmartBodySplitProposal proposal)
        {
            ClearBakedMesh();
            _proposal = proposal;
            var skinned = proposal?.SourceRenderer as SkinnedMeshRenderer;
            if (skinned != null)
            {
                _bakedMesh = new Mesh { name = "SmartBodySplitPreview" };
                skinned.BakeMesh(_bakedMesh);
            }
            SceneView.RepaintAll();
        }

        public static void Clear()
        {
            _proposal = null;
            ClearBakedMesh();
            SceneView.RepaintAll();
        }

        private static void Draw(SceneView sceneView)
        {
            if (_proposal?.SourceRenderer == null || _proposal.SourceMesh == null) return;
            var mesh = _bakedMesh != null && _bakedMesh.vertexCount == _proposal.SourceMesh.vertexCount
                ? _bakedMesh
                : _proposal.SourceMesh;
            var vertices = mesh.vertices;
            var transform = _proposal.SourceRenderer.transform.localToWorldMatrix;
            int stride = Mathf.Max(1, Mathf.CeilToInt(_proposal.SourceTriangleCount / (float)MaxPreviewTriangles));
            int visibleIndex = 0;
            for (int partIndex = 0; partIndex < _proposal.Parts.Count; partIndex++)
            {
                var part = _proposal.Parts[partIndex];
                var color = ColorFor(part.Part);
                for (int triangleIndex = 0; triangleIndex < part.Triangles.Count; triangleIndex++, visibleIndex++)
                {
                    if (visibleIndex % stride != 0) continue;
                    var triangle = part.Triangles[triangleIndex];
                    if (triangle.A >= vertices.Length || triangle.B >= vertices.Length || triangle.C >= vertices.Length) continue;
                    var alpha = Mathf.Lerp(0.06f, 0.22f, triangle.Confidence);
                    Handles.color = new Color(color.r, color.g, color.b, alpha);
                    Handles.DrawAAConvexPolygon(
                        transform.MultiplyPoint3x4(vertices[triangle.A]),
                        transform.MultiplyPoint3x4(vertices[triangle.B]),
                        transform.MultiplyPoint3x4(vertices[triangle.C]));
                }
            }
        }

        private static Color ColorFor(SemanticBodyPart part)
        {
            switch (part)
            {
                case SemanticBodyPart.Head: return new Color(1f, 0.55f, 0.1f);
                case SemanticBodyPart.Torso: return new Color(0.2f, 0.65f, 1f);
                case SemanticBodyPart.LeftArm: return new Color(0.2f, 1f, 0.45f);
                case SemanticBodyPart.RightArm: return new Color(0.8f, 0.25f, 1f);
                case SemanticBodyPart.LeftLeg: return new Color(1f, 0.25f, 0.35f);
                case SemanticBodyPart.RightLeg: return new Color(1f, 0.9f, 0.2f);
                case SemanticBodyPart.LeftHand: return new Color(0.15f, 0.95f, 0.95f);
                case SemanticBodyPart.RightHand: return new Color(1f, 0.35f, 0.75f);
                case SemanticBodyPart.LeftFoot: return new Color(0.45f, 0.55f, 1f);
                case SemanticBodyPart.RightFoot: return new Color(0.95f, 0.65f, 0.25f);
                default: return Color.gray;
            }
        }

        private static void ClearBakedMesh()
        {
            if (_bakedMesh == null) return;
            UnityEngine.Object.DestroyImmediate(_bakedMesh);
            _bakedMesh = null;
        }
    }
}
