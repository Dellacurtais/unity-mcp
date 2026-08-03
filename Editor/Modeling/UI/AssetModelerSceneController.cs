using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.UI
{
    [InitializeOnLoad]
    internal static class AssetModelerSceneController
    {
        private static readonly Rect ToolbarRect = new Rect(12f, 12f, 286f, 142f);
        private static AssetModelerWindow _window;
        private static Mesh _cachedMesh;
        private static HalfEdgeTopology _topology;
        private static uint _topologyRevision;
        private static Vector3[] _vertices = Array.Empty<Vector3>();
        private static Mesh _bakedMesh;
        private static int _hoveredFace = -1;
        private static bool _collapsed;

        static AssetModelerSceneController()
        {
            SceneView.duringSceneGui += Draw;
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
        }

        public static void Attach(AssetModelerWindow window)
        {
            _window = window;
            SceneView.RepaintAll();
        }

        public static void Detach(AssetModelerWindow window)
        {
            if (_window != window) return;
            _window = null;
            _hoveredFace = -1;
            SceneView.RepaintAll();
        }

        private static void Clear()
        {
            _window = null;
            _cachedMesh = null;
            _topology = null;
            _vertices = Array.Empty<Vector3>();
            if (_bakedMesh != null) UnityEngine.Object.DestroyImmediate(_bakedMesh);
            _bakedMesh = null;
        }

        private static void Draw(SceneView sceneView)
        {
            if (_window == null) return;
            DrawToolbar();
            if (!_window.SceneFacePickingActive) return;
            var document = _window.SceneDocument;
            var renderer = _window.SceneRenderer;
            if (document?.WorkingMesh == null || renderer == null) return;
            RefreshGeometry(document, renderer);
            if (_topology == null || _vertices.Length != document.WorkingMesh.vertexCount) return;

            var current = Event.current;
            if (current.type == EventType.Layout)
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

            bool overToolbar = ToolbarRect.Contains(current.mousePosition);
            int hovered = -1;
            if (!overToolbar)
            {
                var ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
                TryPickFace(ray, renderer.transform.worldToLocalMatrix, _topology, _vertices, out hovered);
            }
            if (hovered != _hoveredFace)
            {
                _hoveredFace = hovered;
                sceneView.Repaint();
            }

            if (!overToolbar && current.type == EventType.MouseDown && current.button == 0 && !current.alt && _hoveredFace >= 0)
            {
                bool toggle = current.control || current.command;
                _window.SelectFaceFromScene(_hoveredFace, current.shift || toggle, toggle);
                current.Use();
            }
            DrawFaceSelection(renderer.localToWorldMatrix, _topology, _vertices, _window.SceneSelectedFaces, _hoveredFace);
        }

        private static void DrawToolbar()
        {
            Handles.BeginGUI();
            GUILayout.BeginArea(ToolbarRect, EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Asset Modeler", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (_window.SceneTarget != null && GUILayout.Button("Focar", EditorStyles.miniButton, GUILayout.Width(46f)))
                _window.SceneFrameTarget();
            if (GUILayout.Button(_collapsed ? "+" : "−", EditorStyles.miniButton, GUILayout.Width(24f))) _collapsed = !_collapsed;
            EditorGUILayout.EndHorizontal();
            if (!_collapsed)
            {
                EditorGUILayout.BeginHorizontal();
                WorkspaceButton("Objetos", AssetModelerWorkspace.Objects);
                WorkspaceButton("Topologia", AssetModelerWorkspace.Topology);
                WorkspaceButton("Deformar", AssetModelerWorkspace.Deform);
                WorkspaceButton("Vestir", AssetModelerWorkspace.SurfaceFit);
                EditorGUILayout.EndHorizontal();

                if (_window.SceneWorkspace == AssetModelerWorkspace.Objects)
                {
                    EditorGUILayout.BeginHorizontal();
                    NativeToolButton("Mover", Tool.Move);
                    NativeToolButton("Rotacionar", Tool.Rotate);
                    NativeToolButton("Escalar", Tool.Scale);
                    EditorGUILayout.EndHorizontal();
                }

                if (_window.SceneFacePickingActive)
                    EditorGUILayout.LabelField($"Seleção de faces ativa • {_window.SceneSelectedFaces.Count} selecionada(s)", EditorStyles.miniLabel);
                else if (_window.SceneTarget == null)
                    EditorGUILayout.LabelField("Selecione um objeto na Hierarchy.", EditorStyles.miniLabel);

                EditorGUILayout.BeginHorizontal();
                if (_window.SceneDocument == null)
                {
                    using (new EditorGUI.DisabledScope(_window.SceneTarget == null))
                        if (GUILayout.Button("Criar cópia editável", GUILayout.Height(24f))) _window.SceneCreateEditableCopy();
                }
                else if (_window.SceneSupportsPreview)
                {
                    if (GUILayout.Button(_window.SceneHasPreview ? "Atualizar" : "Preview", GUILayout.Height(24f))) _window.ScenePreviewCurrent();
                    using (new EditorGUI.DisabledScope(!_window.SceneHasPreview))
                    {
                        if (GUILayout.Button("Aplicar", GUILayout.Height(24f))) _window.SceneApplyCurrent();
                        if (GUILayout.Button("×", GUILayout.Width(26f), GUILayout.Height(24f))) _window.SceneCancelPreview();
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
            GUILayout.EndArea();
            Handles.EndGUI();
        }

        private static void WorkspaceButton(string label, AssetModelerWorkspace workspace)
        {
            bool active = _window.SceneWorkspace == workspace;
            if (GUILayout.Toggle(active, label, EditorStyles.miniButton) && !active)
                _window.SceneOpenWorkspace(workspace);
        }

        private static void NativeToolButton(string label, Tool tool)
        {
            if (GUILayout.Toggle(Tools.current == tool, label, EditorStyles.miniButton)) Tools.current = tool;
        }

        private static void RefreshGeometry(MeshEditDocument document, Renderer renderer)
        {
            var mesh = document.WorkingMesh;
            if (_cachedMesh != mesh || _topology == null || _topologyRevision != document.Revision.Topology)
            {
                _cachedMesh = mesh;
                _topology = HalfEdgeTopology.Build(mesh);
                _topologyRevision = document.Revision.Topology;
            }
            if (renderer is SkinnedMeshRenderer skinned)
            {
                if (_bakedMesh == null)
                {
                    _bakedMesh = new Mesh { name = "AssetModeler_ScenePick" };
                    _bakedMesh.hideFlags = HideFlags.HideAndDontSave;
                }
                skinned.BakeMesh(_bakedMesh, true);
                _vertices = _bakedMesh.vertexCount == mesh.vertexCount ? _bakedMesh.vertices : mesh.vertices;
            }
            else _vertices = mesh.vertices;
        }

        internal static bool TryPickFace(
            Ray worldRay,
            Matrix4x4 worldToLocal,
            HalfEdgeTopology topology,
            IReadOnlyList<Vector3> vertices,
            out int faceIndex)
        {
            faceIndex = -1;
            if (topology == null || vertices == null) return false;
            var origin = worldToLocal.MultiplyPoint3x4(worldRay.origin);
            var direction = worldToLocal.MultiplyVector(worldRay.direction);
            if (direction.sqrMagnitude < 1e-14f) return false;
            var ray = new Ray(origin, direction.normalized);
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < topology.Faces.Count; i++)
            {
                var face = topology.Faces[i];
                if (face.A >= vertices.Count || face.B >= vertices.Count || face.C >= vertices.Count) continue;
                if (!RayTriangle(ray, vertices[face.A], vertices[face.B], vertices[face.C], out float distance)) continue;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                faceIndex = face.Index;
            }
            return faceIndex >= 0;
        }

        private static bool RayTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float distance)
        {
            distance = 0f;
            var edge1 = b - a;
            var edge2 = c - a;
            var cross = Vector3.Cross(ray.direction, edge2);
            float determinant = Vector3.Dot(edge1, cross);
            if (Mathf.Abs(determinant) < 1e-8f) return false;
            float inverse = 1f / determinant;
            var offset = ray.origin - a;
            float u = Vector3.Dot(offset, cross) * inverse;
            if (u < 0f || u > 1f) return false;
            var q = Vector3.Cross(offset, edge1);
            float v = Vector3.Dot(ray.direction, q) * inverse;
            if (v < 0f || u + v > 1f) return false;
            distance = Vector3.Dot(edge2, q) * inverse;
            return distance > 1e-6f;
        }

        private static void DrawFaceSelection(
            Matrix4x4 localToWorld,
            HalfEdgeTopology topology,
            IReadOnlyList<Vector3> vertices,
            IReadOnlyList<int> selected,
            int hovered)
        {
            using (new Handles.DrawingScope(localToWorld))
            {
                int budget = 400;
                for (int i = 0; i < selected.Count && i < budget; i++)
                    DrawFace(topology, vertices, selected[i], new Color(0.12f, 0.65f, 1f, 0.22f), new Color(0.1f, 0.75f, 1f, 0.95f));
                if (hovered >= 0)
                    DrawFace(topology, vertices, hovered, new Color(1f, 0.65f, 0.05f, 0.28f), new Color(1f, 0.75f, 0.1f, 1f));
            }
        }

        private static void DrawFace(
            HalfEdgeTopology topology,
            IReadOnlyList<Vector3> vertices,
            int faceIndex,
            Color fill,
            Color outline)
        {
            if (faceIndex < 0 || faceIndex >= topology.Faces.Count) return;
            var face = topology.Faces[faceIndex];
            if (face.A >= vertices.Count || face.B >= vertices.Count || face.C >= vertices.Count) return;
            var points = new[] { vertices[face.A], vertices[face.B], vertices[face.C] };
            Handles.color = fill;
            Handles.DrawAAConvexPolygon(points);
            Handles.color = outline;
            Handles.DrawAAPolyLine(2.5f, points[0], points[1], points[2], points[0]);
        }
    }
}
