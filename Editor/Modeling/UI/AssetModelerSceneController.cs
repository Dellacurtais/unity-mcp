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
        private static readonly int SceneControlHint = "AssetModelerFaceSelection".GetHashCode();
        private const float MarqueeThreshold = 5f;
        private static bool _pointerCaptured;
        private static bool _marqueeActive;
        private static Vector2 _marqueeStart;
        private static Vector2 _marqueeEnd;
        private static int _clickCount;
        private static int _capturedControlId;

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
            ResetPointerCapture();
            SceneView.RepaintAll();
        }

        private static void Clear()
        {
            _window = null;
            _cachedMesh = null;
            _topology = null;
            _vertices = Array.Empty<Vector3>();
            ResetPointerCapture();
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
            int controlId = GUIUtility.GetControlID(SceneControlHint, FocusType.Passive);
            if (current.type == EventType.Layout)
                HandleUtility.AddDefaultControl(controlId);

            bool overToolbar = ToolbarRect.Contains(current.mousePosition);
            int hovered = -1;
            if (!overToolbar && !_marqueeActive)
            {
                var ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
                TryPickFace(ray, renderer.transform.worldToLocalMatrix, _topology, _vertices, out hovered);
            }
            if (hovered != _hoveredFace)
            {
                _hoveredFace = hovered;
                sceneView.Repaint();
            }

            HandlePointer(sceneView, renderer, current, controlId, overToolbar);
            DrawFaceSelection(renderer.localToWorldMatrix, _topology, _vertices, _window.SceneSelectedFaces, _hoveredFace);
            if (_marqueeActive) DrawMarquee(_marqueeStart, _marqueeEnd);
        }

        private static void HandlePointer(
            SceneView sceneView,
            Renderer renderer,
            Event current,
            int controlId,
            bool overToolbar)
        {
            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape && _pointerCaptured)
            {
                ResetPointerCapture();
                current.Use();
                sceneView.Repaint();
                return;
            }

            if (!_pointerCaptured
                && !overToolbar
                && current.type == EventType.MouseDown
                && current.button == 0
                && !current.alt)
            {
                _pointerCaptured = true;
                _marqueeActive = false;
                _marqueeStart = current.mousePosition;
                _marqueeEnd = _marqueeStart;
                _clickCount = current.clickCount;
                _capturedControlId = controlId;
                GUIUtility.hotControl = controlId;
                current.Use();
                return;
            }

            if (!_pointerCaptured || GUIUtility.hotControl != controlId) return;

            if (current.type == EventType.MouseDrag && current.button == 0)
            {
                _marqueeEnd = current.mousePosition;
                if (!_marqueeActive
                    && (_marqueeEnd - _marqueeStart).sqrMagnitude >= MarqueeThreshold * MarqueeThreshold)
                {
                    _marqueeActive = true;
                    _hoveredFace = -1;
                }
                current.Use();
                sceneView.Repaint();
                return;
            }

            if (current.type != EventType.MouseUp || current.button != 0) return;

            bool remove = current.control || current.command;
            bool additive = current.shift && !remove;
            if (_marqueeActive)
            {
                _marqueeEnd = current.mousePosition;
                bool crossing = _marqueeEnd.x < _marqueeStart.x;
                var rect = NormalizeRect(_marqueeStart, _marqueeEnd);
                var faces = CollectFacesInGuiRect(
                    rect,
                    crossing,
                    renderer.localToWorldMatrix,
                    _topology,
                    _vertices,
                    world =>
                    {
                        var gui = HandleUtility.WorldToGUIPoint(world);
                        float depth = sceneView.camera.WorldToViewportPoint(world).z;
                        return new Vector3(gui.x, gui.y, depth);
                    });
                _window.SelectFacesFromScene(
                    faces,
                    additive,
                    remove,
                    crossing ? "Área cruzada" : "Área contida");
            }
            else if (_hoveredFace >= 0)
            {
                var faces = _clickCount >= 2
                    ? CollectConnectedFaces(_topology, new[] { _hoveredFace })
                    : new[] { _hoveredFace };
                _window.SelectFacesFromScene(
                    faces,
                    additive,
                    remove,
                    _clickCount >= 2 ? "Ilha conectada" : $"Face {_hoveredFace}");
            }
            else if (!additive && !remove)
            {
                _window.SelectFacesFromScene(Array.Empty<int>(), false, false, "Área vazia");
            }

            ResetPointerCapture();
            current.Use();
            sceneView.Repaint();
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
                    EditorGUILayout.LabelField($"Faces • clique/arraste • {_window.SceneSelectedFaces.Count} selecionada(s)", EditorStyles.miniLabel);
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

        internal static int[] CollectFacesInGuiRect(
            Rect rect,
            bool crossing,
            Matrix4x4 localToWorld,
            HalfEdgeTopology topology,
            IReadOnlyList<Vector3> vertices,
            Func<Vector3, Vector3> projectWorldToGui)
        {
            if (topology == null || vertices == null || projectWorldToGui == null)
                return Array.Empty<int>();

            var selected = new List<int>();
            for (int i = 0; i < topology.Faces.Count; i++)
            {
                var face = topology.Faces[i];
                if (face.A < 0 || face.B < 0 || face.C < 0
                    || face.A >= vertices.Count || face.B >= vertices.Count || face.C >= vertices.Count)
                    continue;

                var a = projectWorldToGui(localToWorld.MultiplyPoint3x4(vertices[face.A]));
                var b = projectWorldToGui(localToWorld.MultiplyPoint3x4(vertices[face.B]));
                var c = projectWorldToGui(localToWorld.MultiplyPoint3x4(vertices[face.C]));
                if (a.z <= 0f || b.z <= 0f || c.z <= 0f) continue;

                var pointA = new Vector2(a.x, a.y);
                var pointB = new Vector2(b.x, b.y);
                var pointC = new Vector2(c.x, c.y);
                bool matches = crossing
                    ? TriangleOverlapsRect(pointA, pointB, pointC, rect)
                    : ContainsInclusive(rect, pointA) && ContainsInclusive(rect, pointB) && ContainsInclusive(rect, pointC);
                if (matches) selected.Add(face.Index);
            }
            return selected.ToArray();
        }

        internal static int[] CollectConnectedFaces(HalfEdgeTopology topology, IEnumerable<int> seeds)
        {
            if (topology == null || seeds == null) return Array.Empty<int>();
            var selected = new SortedSet<int>();
            var queue = new Queue<int>();
            foreach (int seed in seeds)
            {
                if (seed < 0 || seed >= topology.Faces.Count || !selected.Add(seed)) continue;
                queue.Enqueue(seed);
            }
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                var neighbors = topology.Faces[current].Neighbors;
                for (int i = 0; i < neighbors.Length; i++)
                    if (selected.Add(neighbors[i])) queue.Enqueue(neighbors[i]);
            }
            var result = new int[selected.Count];
            selected.CopyTo(result);
            return result;
        }

        private static bool TriangleOverlapsRect(Vector2 a, Vector2 b, Vector2 c, Rect rect)
        {
            if (ContainsInclusive(rect, a) || ContainsInclusive(rect, b) || ContainsInclusive(rect, c)) return true;
            var bottomLeft = new Vector2(rect.xMin, rect.yMax);
            var topLeft = new Vector2(rect.xMin, rect.yMin);
            var topRight = new Vector2(rect.xMax, rect.yMin);
            var bottomRight = new Vector2(rect.xMax, rect.yMax);
            if (PointInTriangle(bottomLeft, a, b, c)
                || PointInTriangle(topLeft, a, b, c)
                || PointInTriangle(topRight, a, b, c)
                || PointInTriangle(bottomRight, a, b, c))
                return true;
            return SegmentIntersectsRect(a, b, topLeft, topRight, bottomRight, bottomLeft)
                || SegmentIntersectsRect(b, c, topLeft, topRight, bottomRight, bottomLeft)
                || SegmentIntersectsRect(c, a, topLeft, topRight, bottomRight, bottomLeft);
        }

        private static bool SegmentIntersectsRect(
            Vector2 a,
            Vector2 b,
            Vector2 topLeft,
            Vector2 topRight,
            Vector2 bottomRight,
            Vector2 bottomLeft)
            => SegmentsIntersect(a, b, topLeft, topRight)
                || SegmentsIntersect(a, b, topRight, bottomRight)
                || SegmentsIntersect(a, b, bottomRight, bottomLeft)
                || SegmentsIntersect(a, b, bottomLeft, topLeft);

        private static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float abC = Cross(b - a, c - a);
            float abD = Cross(b - a, d - a);
            float cdA = Cross(d - c, a - c);
            float cdB = Cross(d - c, b - c);
            const float epsilon = 1e-5f;
            if (((abC > epsilon && abD < -epsilon) || (abC < -epsilon && abD > epsilon))
                && ((cdA > epsilon && cdB < -epsilon) || (cdA < -epsilon && cdB > epsilon)))
                return true;
            return Mathf.Abs(abC) <= epsilon && OnSegment(a, b, c)
                || Mathf.Abs(abD) <= epsilon && OnSegment(a, b, d)
                || Mathf.Abs(cdA) <= epsilon && OnSegment(c, d, a)
                || Mathf.Abs(cdB) <= epsilon && OnSegment(c, d, b);
        }

        private static bool OnSegment(Vector2 a, Vector2 b, Vector2 point)
        {
            const float epsilon = 1e-5f;
            return point.x >= Mathf.Min(a.x, b.x) - epsilon
                && point.x <= Mathf.Max(a.x, b.x) + epsilon
                && point.y >= Mathf.Min(a.y, b.y) - epsilon
                && point.y <= Mathf.Max(a.y, b.y) + epsilon;
        }

        private static bool PointInTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
        {
            const float epsilon = 1e-5f;
            float first = Cross(b - a, point - a);
            float second = Cross(c - b, point - b);
            float third = Cross(a - c, point - c);
            bool hasNegative = first < -epsilon || second < -epsilon || third < -epsilon;
            bool hasPositive = first > epsilon || second > epsilon || third > epsilon;
            return !(hasNegative && hasPositive);
        }

        private static float Cross(Vector2 left, Vector2 right) => left.x * right.y - left.y * right.x;

        private static bool ContainsInclusive(Rect rect, Vector2 point)
            => point.x >= rect.xMin && point.x <= rect.xMax && point.y >= rect.yMin && point.y <= rect.yMax;

        private static Rect NormalizeRect(Vector2 start, Vector2 end)
            => Rect.MinMaxRect(
                Mathf.Min(start.x, end.x),
                Mathf.Min(start.y, end.y),
                Mathf.Max(start.x, end.x),
                Mathf.Max(start.y, end.y));

        private static void DrawMarquee(Vector2 start, Vector2 end)
        {
            var rect = NormalizeRect(start, end);
            bool crossing = end.x < start.x;
            var outline = crossing ? new Color(0.15f, 0.75f, 1f, 1f) : new Color(0.3f, 1f, 0.45f, 1f);
            var fill = new Color(outline.r, outline.g, outline.b, 0.12f);
            Handles.BeginGUI();
            EditorGUI.DrawRect(rect, fill);
            const float thickness = 2f;
            EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMin, rect.width, thickness), outline);
            EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMax - thickness, rect.width, thickness), outline);
            EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMin, thickness, rect.height), outline);
            EditorGUI.DrawRect(new Rect(rect.xMax - thickness, rect.yMin, thickness, rect.height), outline);
            var labelRect = new Rect(rect.xMin, Mathf.Max(0f, rect.yMin - 19f), 156f, 18f);
            GUI.Label(labelRect, crossing ? "Cruzamento" : "Totalmente contidas", EditorStyles.miniBoldLabel);
            Handles.EndGUI();
        }

        private static void ResetPointerCapture()
        {
            int capturedControl = _capturedControlId;
            _pointerCaptured = false;
            _marqueeActive = false;
            _clickCount = 0;
            _capturedControlId = 0;
            if (capturedControl != 0 && GUIUtility.hotControl == capturedControl) GUIUtility.hotControl = 0;
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
