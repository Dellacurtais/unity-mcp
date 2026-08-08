using System;
using System.Collections.Generic;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Maicon.SceneAnalyzer.Editor.Modeling.Operations.Deform;
using Maicon.SceneAnalyzer.Editor.Modeling.UI;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.SceneUx
{
    [EditorTool("Asset Modeler Modeling", typeof(AssetModelerToolContext))]
    internal sealed class AssetModelerModelingTool : EditorTool
    {
        private const float VertexPickRadiusPixels = 14f;
        private const float EdgePickRadiusPixels = 12f;
        private const int WalkCap = 512;
        private const int DrawBudget = 4000;

        private static readonly Color VertexColor = new Color(1f, 0.62f, 0.1f, 1f);
        private static readonly Color EdgeColor = new Color(0.2f, 0.85f, 1f, 1f);
        private static readonly Color HoverColor = new Color(1f, 1f, 1f, 1f);
        private static readonly Color BoneColor = new Color(0.55f, 1f, 0.4f, 1f);

        private GUIContent _icon;
        private int _hoveredVertex = -1;
        private MeshEdgeKey? _hoveredEdge;
        private int _hoveredBone = -1;
        private float _lastPreviewValue = float.NaN;
        private bool _pointerCaptured;
        private bool _marqueeActive;
        private Vector2 _marqueeStart;
        private Vector2 _marqueeEnd;
        private int _capturedControlId;

        private enum PointerResult
        {
            None,
            Click,
            Marquee,
        }

        public override GUIContent toolbarIcon
            => _icon ?? (_icon = new GUIContent("Asset Modeler", EditorGUIUtility.IconContent("EditCollider").image, "Asset Modeler modeling tool"));

        public override void OnToolGUI(EditorWindow window)
        {
            if (!(window is SceneView sceneView)) return;
            var modeler = AssetModelerSceneController.AttachedWindow;
            var mode = AssetModelerModelingState.Mode;
            if (modeler == null)
            {
                DrawHint(sceneView, "Abra Window > Asset Modeler (Unity MCP) para modelar.");
                return;
            }
            var document = modeler.SceneDocument;
            var renderer = modeler.SceneRenderer;
            if (mode == AssetModelerEditMode.Object)
            {
                DrawHint(sceneView, "Modo Objeto: use os gizmos nativos da Unity.");
                return;
            }
            if (document?.WorkingMesh == null || renderer == null)
            {
                DrawHint(sceneView, "Crie uma cópia editável para habilitar a modelagem.");
                return;
            }

            AssetModelerSceneController.RefreshGeometry(document, renderer);
            var topology = AssetModelerSceneController.CurrentTopology;
            var vertices = AssetModelerSceneController.CurrentVertices;
            if (topology == null || vertices == null || vertices.Length == 0) return;

            var localToWorld = renderer.localToWorldMatrix;
            switch (mode)
            {
                case AssetModelerEditMode.Vertex: DoVertexMode(sceneView, modeler, localToWorld, vertices); break;
                case AssetModelerEditMode.Edge: DoEdgeMode(sceneView, modeler, localToWorld, topology, vertices); break;
                case AssetModelerEditMode.Face: break;
                case AssetModelerEditMode.Region: DoRegionMode(sceneView, modeler, renderer, topology, vertices); break;
                case AssetModelerEditMode.Bone: DoBoneMode(sceneView, modeler, renderer); break;
            }

            DrawManipulators(sceneView, modeler, localToWorld, topology, vertices, mode);
            AssetModelerMeshDiagnostics.Draw(renderer, topology, vertices);
        }

        private void DoVertexMode(SceneView sceneView, AssetModelerWindow modeler, Matrix4x4 localToWorld, Vector3[] vertices)
        {
            var current = Event.current;
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            if (current.type == EventType.Layout) HandleUtility.AddDefaultControl(controlId);

            int hovered = PickVertex(current.mousePosition, localToWorld, vertices);
            if (hovered != _hoveredVertex) { _hoveredVertex = hovered; sceneView.Repaint(); }

            var pointer = HandlePointer(sceneView, current, controlId, out var marqueeRect, out bool marqueeCrossing);
            if (pointer == PointerResult.Marquee)
            {
                bool remove = current.control || current.command;
                bool additive = current.shift && !remove;
                var picked = CollectVerticesInRect(marqueeRect, localToWorld, vertices, sceneView.camera);
                var expanded = AssetModelerModelingState.ExpandSymmetric(vertices, picked);
                modeler.SceneSelectElements(MeshElementKind.Vertex, expanded, additive, remove, marqueeCrossing ? "Área cruzada" : "Área contida");
            }
            else if (pointer == PointerResult.Click)
            {
                bool remove = current.control || current.command;
                bool additive = current.shift && !remove;
                if (hovered >= 0)
                {
                    var expanded = AssetModelerModelingState.ExpandSymmetric(vertices, new[] { hovered });
                    modeler.SceneSelectElements(MeshElementKind.Vertex, expanded, additive, remove, $"Vértice {hovered}");
                }
                else if (!additive && !remove)
                    modeler.SceneSelectElements(MeshElementKind.Vertex, Array.Empty<int>(), false, false, "Área vazia");
            }
            if (_marqueeActive) AssetModelerSceneController.DrawMarquee(_marqueeStart, _marqueeEnd);

            var selected = SelectedIndices(modeler, MeshElementKind.Vertex);
            using (new Handles.DrawingScope(VertexColor, localToWorld))
            {
                int drawn = 0;
                for (int i = 0; i < selected.Count && drawn < DrawBudget; i++, drawn++)
                {
                    int index = selected[i];
                    if (index >= 0 && index < vertices.Length)
                        Handles.DotHandleCap(0, vertices[index], Quaternion.identity, HandleUtility.GetHandleSize(vertices[index]) * 0.05f, EventType.Repaint);
                }
                if (_hoveredVertex >= 0 && _hoveredVertex < vertices.Length)
                    using (new Handles.DrawingScope(HoverColor))
                        Handles.DotHandleCap(0, vertices[_hoveredVertex], Quaternion.identity, HandleUtility.GetHandleSize(vertices[_hoveredVertex]) * 0.08f, EventType.Repaint);
            }
        }

        private void DoEdgeMode(SceneView sceneView, AssetModelerWindow modeler, Matrix4x4 localToWorld, HalfEdgeTopology topology, Vector3[] vertices)
        {
            var current = Event.current;
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            if (current.type == EventType.Layout) HandleUtility.AddDefaultControl(controlId);

            MeshEdgeKey? hovered = PickEdge(current.mousePosition, localToWorld, topology, vertices);
            if (!Nullable.Equals(hovered, _hoveredEdge)) { _hoveredEdge = hovered; sceneView.Repaint(); }

            if (current.type == EventType.KeyDown && hovered.HasValue)
            {
                if (current.keyCode == KeyCode.L)
                {
                    modeler.SceneSelectElements(MeshElementKind.Edge, CollectEdgeLoop(topology, vertices, hovered.Value), current.shift, false, "Loop");
                    current.Use();
                }
                else if (current.keyCode == KeyCode.R)
                {
                    modeler.SceneSelectElements(MeshElementKind.Edge, CollectEdgeRing(topology, vertices, hovered.Value), current.shift, false, "Ring");
                    current.Use();
                }
            }

            var pointer = HandlePointer(sceneView, current, controlId, out var marqueeRect, out bool marqueeCrossing);
            if (pointer == PointerResult.Marquee)
            {
                bool remove = current.control || current.command;
                bool additive = current.shift && !remove;
                var picked = CollectEdgesInRect(marqueeRect, marqueeCrossing, localToWorld, topology, vertices, sceneView.camera);
                modeler.SceneSelectElements(MeshElementKind.Edge, picked, additive, remove, marqueeCrossing ? "Área cruzada" : "Área contida");
            }
            else if (pointer == PointerResult.Click && hovered.HasValue)
            {
                bool remove = current.control || current.command;
                bool additive = current.shift && !remove;
                int[] pairs;
                string source;
                if (current.clickCount >= 2)
                {
                    pairs = CollectEdgeLoop(topology, vertices, hovered.Value);
                    source = "Loop";
                }
                else
                {
                    pairs = AssetModelerModelingState.ExpandSymmetric(vertices, new[] { hovered.Value.A, hovered.Value.B });
                    source = $"Aresta {hovered.Value}";
                }
                modeler.SceneSelectElements(MeshElementKind.Edge, pairs, additive, remove, source);
            }
            else if (pointer == PointerResult.Click && !current.shift && !current.control && !current.command)
                modeler.SceneSelectElements(MeshElementKind.Edge, Array.Empty<int>(), false, false, "Área vazia");
            if (_marqueeActive) AssetModelerSceneController.DrawMarquee(_marqueeStart, _marqueeEnd);

            var selected = SelectedIndices(modeler, MeshElementKind.Edge);
            using (new Handles.DrawingScope(EdgeColor, localToWorld))
            {
                int drawn = 0;
                for (int i = 0; i + 1 < selected.Count && drawn < DrawBudget; i += 2, drawn++)
                {
                    int a = selected[i];
                    int b = selected[i + 1];
                    if (a < 0 || b < 0 || a >= vertices.Length || b >= vertices.Length) continue;
                    Handles.DrawAAPolyLine(4f, vertices[a], vertices[b]);
                }
                if (_hoveredEdge.HasValue)
                {
                    var key = _hoveredEdge.Value;
                    if (key.A < vertices.Length && key.B < vertices.Length)
                        using (new Handles.DrawingScope(HoverColor))
                            Handles.DrawAAPolyLine(6f, vertices[key.A], vertices[key.B]);
                }
            }
        }

        private void DoRegionMode(SceneView sceneView, AssetModelerWindow modeler, Renderer renderer, HalfEdgeTopology topology, Vector3[] vertices)
        {
            var current = Event.current;
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            if (current.type == EventType.Layout) HandleUtility.AddDefaultControl(controlId);

            int hoveredFace = -1;
            var ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
            AssetModelerSceneController.TryPickFace(ray, renderer.transform.worldToLocalMatrix, topology, vertices, out hoveredFace);

            if (HandleSimpleClick(sceneView, current, controlId) && hoveredFace >= 0)
            {
                var region = FindRegionAtFace(modeler.SceneDocument, hoveredFace);
                if (region != null) modeler.SceneSelectRegion(region);
                else modeler.SceneStatus("Nenhuma região salva contém esta face. Salve regiões na aba Topologia.", MessageType.Warning);
            }

            var selected = SelectedIndices(modeler, MeshElementKind.Face);
            AssetModelerSceneController.DrawFaceSelection(renderer.localToWorldMatrix, topology, vertices, selected, hoveredFace);
        }

        private void DoBoneMode(SceneView sceneView, AssetModelerWindow modeler, Renderer renderer)
        {
            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned == null)
            {
                DoAnchorMode(sceneView, modeler, renderer);
                return;
            }
            var bones = skinned.bones;
            if (bones == null || bones.Length == 0) return;

            var current = Event.current;
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            if (current.type == EventType.Layout) HandleUtility.AddDefaultControl(controlId);

            int hovered = -1;
            float best = BonePickRadiusPixels();
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null) continue;
                float distance = Vector2.Distance(HandleUtility.WorldToGUIPoint(bones[i].position), current.mousePosition);
                if (distance < best) { best = distance; hovered = i; }
            }
            if (hovered != _hoveredBone) { _hoveredBone = hovered; sceneView.Repaint(); }

            if (HandleSimpleClick(sceneView, current, controlId) && hovered >= 0)
            {
                bool remove = current.control || current.command;
                bool additive = current.shift && !remove;
                var dominant = CollectDominantVertices(modeler.SceneDocument.WorkingMesh, hovered);
                modeler.SceneSelectElements(MeshElementKind.Vertex, dominant, additive, remove, $"Bone {bones[hovered].name}");
            }

            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null) continue;
                float size = HandleUtility.GetHandleSize(bones[i].position) * (i == _hoveredBone ? 0.09f : 0.05f);
                using (new Handles.DrawingScope(i == _hoveredBone ? HoverColor : BoneColor))
                    Handles.SphereHandleCap(0, bones[i].position, Quaternion.identity, size, EventType.Repaint);
            }

            var selected = SelectedIndices(modeler, MeshElementKind.Vertex);
            var mesh = modeler.SceneDocument.WorkingMesh;
            var vertices = AssetModelerSceneController.CurrentVertices;
            if (vertices == null || vertices.Length != mesh.vertexCount) return;
            using (new Handles.DrawingScope(VertexColor, renderer.localToWorldMatrix))
            {
                int drawn = 0;
                for (int i = 0; i < selected.Count && drawn < DrawBudget; i++, drawn++)
                {
                    int index = selected[i];
                    if (index >= 0 && index < vertices.Length)
                        Handles.DotHandleCap(0, vertices[index], Quaternion.identity, HandleUtility.GetHandleSize(vertices[index]) * 0.05f, EventType.Repaint);
                }
            }
        }

        private void DoAnchorMode(SceneView sceneView, AssetModelerWindow modeler, Renderer renderer)
        {
            var target = modeler.SceneTarget;
            if (target == null)
            {
                DrawHint(sceneView, "Selecione um objeto para editar anchors.");
                return;
            }
            var anchors = new List<Transform>();
            var all = target.GetComponentsInChildren<Transform>(false);
            for (int i = 0; i < all.Length && anchors.Count < 256; i++)
                if (all[i] != target.transform && all[i].GetComponent<Renderer>() == null)
                    anchors.Add(all[i]);
            if (anchors.Count == 0)
            {
                DrawHint(sceneView, "Nenhum anchor (transform filho sem renderer) encontrado.");
                return;
            }

            var current = Event.current;
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            if (current.type == EventType.Layout) HandleUtility.AddDefaultControl(controlId);

            int hovered = -1;
            float best = BonePickRadiusPixels();
            for (int i = 0; i < anchors.Count; i++)
            {
                float distance = Vector2.Distance(HandleUtility.WorldToGUIPoint(anchors[i].position), current.mousePosition);
                if (distance < best) { best = distance; hovered = i; }
            }
            if (hovered != _hoveredBone) { _hoveredBone = hovered; sceneView.Repaint(); }

            if (HandleSimpleClick(sceneView, current, controlId) && hovered >= 0)
            {
                bool remove = current.control || current.command;
                bool additive = current.shift && !remove;
                var vertices = AssetModelerSceneController.CurrentVertices;
                if (vertices != null && vertices.Length > 0)
                {
                    var anchorPosition = anchors[hovered].position;
                    float radius = HandleUtility.GetHandleSize(anchorPosition) * 0.15f;
                    var localToWorld = renderer.localToWorldMatrix;
                    var picked = new List<int>();
                    for (int i = 0; i < vertices.Length; i++)
                        if ((localToWorld.MultiplyPoint3x4(vertices[i]) - anchorPosition).sqrMagnitude <= radius * radius)
                            picked.Add(i);
                    modeler.SceneSelectElements(MeshElementKind.Vertex, picked, additive, remove, $"Anchor {anchors[hovered].name}");
                }
            }

            for (int i = 0; i < anchors.Count; i++)
            {
                float size = HandleUtility.GetHandleSize(anchors[i].position) * (i == _hoveredBone ? 0.09f : 0.05f);
                using (new Handles.DrawingScope(i == _hoveredBone ? HoverColor : BoneColor))
                    Handles.SphereHandleCap(0, anchors[i].position, Quaternion.identity, size, EventType.Repaint);
            }
        }

        private void DrawManipulators(
            SceneView sceneView,
            AssetModelerWindow modeler,
            Matrix4x4 localToWorld,
            HalfEdgeTopology topology,
            Vector3[] vertices,
            AssetModelerEditMode mode)
        {
            var workspace = modeler.SceneWorkspace;
            if (workspace == AssetModelerWorkspace.Topology && mode == AssetModelerEditMode.Face)
            {
                var selected = SelectedIndices(modeler, MeshElementKind.Face);
                if (selected.Count > 0 && TrySelectionFrame(topology, vertices, selected, out var centroid, out var normal))
                {
                    var tool = modeler.SceneTopologyTool;
                    if (tool == AssetModelerTopologyTool.ExtrudeFaces)
                        DistanceSlider(modeler, localToWorld.MultiplyPoint3x4(centroid), localToWorld.MultiplyVector(normal).normalized,
                            modeler.SceneExtrudeDistance, value => modeler.SceneExtrudeDistance = value, modeler);
                    else if (tool == AssetModelerTopologyTool.Bevel)
                        DistanceSlider(modeler, localToWorld.MultiplyPoint3x4(centroid), localToWorld.MultiplyVector(normal).normalized,
                            modeler.SceneBevelInset * SelectionScale(vertices, selected, topology), value =>
                            {
                                float scale = Mathf.Max(SelectionScale(vertices, selected, topology), 1e-5f);
                                modeler.SceneBevelInset = Mathf.Clamp(value / scale, 0f, 0.95f);
                            }, modeler);
                }
                return;
            }

            if (workspace == AssetModelerWorkspace.Deform)
            {
                var boundsCenter = localToWorld.MultiplyPoint3x4(BoundsCenter(vertices));
                EditorGUI.BeginChangeCheck();
                float radius = Handles.RadiusHandle(Quaternion.identity, boundsCenter, modeler.SceneDeformRadius, true);
                if (EditorGUI.EndChangeCheck()) modeler.SceneDeformRadius = Mathf.Max(0.001f, radius);

                var deformMode = modeler.SceneDeformMode;
                if (deformMode == MeshDeformMode.Grab)
                {
                    EditorGUI.BeginChangeCheck();
                    var moved = Handles.PositionHandle(boundsCenter, Quaternion.identity);
                    if (EditorGUI.EndChangeCheck())
                    {
                        var delta = moved - boundsCenter;
                        modeler.SceneDeformDirection = localToWorld.inverse.MultiplyVector(delta);
                        modeler.SceneDeformStrength = Mathf.Max(0f, localToWorld.inverse.MultiplyVector(delta).magnitude);
                        ThrottledPreview(modeler, modeler.SceneDeformStrength);
                    }
                }
                else if (deformMode == MeshDeformMode.Spline)
                {
                    var points = modeler.SceneSplinePoints;
                    bool changed = false;
                    for (int i = 0; i < points.Length; i++)
                    {
                        var world = localToWorld.MultiplyPoint3x4(points[i]);
                        EditorGUI.BeginChangeCheck();
                        world = Handles.PositionHandle(world, Quaternion.identity);
                        if (EditorGUI.EndChangeCheck())
                        {
                            points[i] = localToWorld.inverse.MultiplyPoint3x4(world);
                            changed = true;
                        }
                        if (i + 1 < points.Length)
                            Handles.DrawAAPolyLine(3f, localToWorld.MultiplyPoint3x4(points[i]), localToWorld.MultiplyPoint3x4(points[i + 1]));
                    }
                    if (changed)
                    {
                        modeler.SceneSplinePoints = points;
                        ThrottledPreview(modeler, points.Length + points[points.Length - 1].sqrMagnitude);
                    }
                }
                else if (deformMode == MeshDeformMode.Bend || deformMode == MeshDeformMode.Twist || deformMode == MeshDeformMode.Taper || deformMode == MeshDeformMode.Flatten)
                {
                    var axis = AxisVector(modeler.SceneDeformAxis);
                    var direction = localToWorld.MultiplyVector(axis).normalized;
                    using (new Handles.DrawingScope(new Color(1f, 0.8f, 0.2f, 1f)))
                    {
                        Handles.DrawLine(boundsCenter - direction, boundsCenter + direction);
                        Handles.ConeHandleCap(0, boundsCenter + direction, Quaternion.LookRotation(direction), HandleUtility.GetHandleSize(boundsCenter) * 0.12f, EventType.Repaint);
                    }
                    EditorGUI.BeginChangeCheck();
                    var slid = Handles.Slider(boundsCenter + direction * modeler.SceneDeformStrength, direction, HandleUtility.GetHandleSize(boundsCenter) * 1.2f, Handles.ArrowHandleCap, 0f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        float strength = Mathf.Max(0f, AssetModelerModelingState.SnapValue(Vector3.Dot(slid - boundsCenter, direction)));
                        modeler.SceneDeformStrength = strength;
                        ThrottledPreview(modeler, strength);
                    }
                }
            }

            if (workspace == AssetModelerWorkspace.SurfaceFit)
            {
                var fitTarget = modeler.SceneFitTarget;
                if (fitTarget == null) return;
                var editableCenter = localToWorld.MultiplyPoint3x4(BoundsCenter(vertices));
                var targetCenter = fitTarget.bounds.center;
                Handles.DrawAAPolyLine(2f, editableCenter, targetCenter);
                float handleSize = HandleUtility.GetHandleSize(targetCenter);
                const float displayScale = 20f;
                EditorGUI.BeginChangeCheck();
                var slid = Handles.Slider(targetCenter + Vector3.up * (modeler.SceneFitClearance * displayScale), Vector3.up, handleSize * 0.9f, Handles.ArrowHandleCap, 0f);
                if (EditorGUI.EndChangeCheck())
                {
                    float clearance = Mathf.Max(0f, AssetModelerModelingState.SnapValue(Vector3.Dot(slid - targetCenter, Vector3.up) / displayScale));
                    modeler.SceneFitClearance = clearance;
                    ThrottledPreview(modeler, clearance * displayScale);
                }
                Handles.Label(targetCenter + Vector3.up * handleSize, $"Clearance {modeler.SceneFitClearance:F3}");
            }
        }

        private void DistanceSlider(AssetModelerWindow modeler, Vector3 position, Vector3 direction, float currentValue, Action<float> assign, AssetModelerWindow previewOwner)
        {
            float handleSize = HandleUtility.GetHandleSize(position) * 1.4f;
            EditorGUI.BeginChangeCheck();
            var slid = Handles.Slider(position + direction * currentValue, direction, handleSize, Handles.ArrowHandleCap, 0f);
            if (!EditorGUI.EndChangeCheck()) return;
            float value = Mathf.Max(0f, Vector3.Dot(slid - position, direction));
            value = AssetModelerModelingState.SnapValue(value);
            assign(value);
            ThrottledPreview(previewOwner, value);
        }

        private void ThrottledPreview(AssetModelerWindow modeler, float value)
        {
            float threshold = AssetModelerModelingState.SnapEnabled ? AssetModelerModelingState.SnapStep * 0.5f : 0.01f;
            if (!float.IsNaN(_lastPreviewValue) && Mathf.Abs(value - _lastPreviewValue) < threshold) return;
            _lastPreviewValue = value;
            modeler.ScenePreviewCurrent();
        }

        private static bool HandleSimpleClick(SceneView sceneView, Event current, int controlId)
        {
            if (current.type == EventType.MouseDown && current.button == 0 && !current.alt)
            {
                GUIUtility.hotControl = controlId;
                current.Use();
                return false;
            }
            if (current.type == EventType.MouseUp && current.button == 0 && GUIUtility.hotControl == controlId)
            {
                GUIUtility.hotControl = 0;
                current.Use();
                sceneView.Repaint();
                return true;
            }
            if (current.type == EventType.MouseDrag && GUIUtility.hotControl == controlId) current.Use();
            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape && GUIUtility.hotControl == controlId)
            {
                GUIUtility.hotControl = 0;
                current.Use();
            }
            return false;
        }

        private PointerResult HandlePointer(SceneView sceneView, Event current, int controlId, out Rect rect, out bool crossing)
        {
            rect = default;
            crossing = false;
            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape && _pointerCaptured)
            {
                ResetToolPointer();
                current.Use();
                sceneView.Repaint();
                return PointerResult.None;
            }
            if (!_pointerCaptured && current.type == EventType.MouseDown && current.button == 0 && !current.alt)
            {
                _pointerCaptured = true;
                _marqueeActive = false;
                _marqueeStart = current.mousePosition;
                _marqueeEnd = _marqueeStart;
                _capturedControlId = controlId;
                GUIUtility.hotControl = controlId;
                current.Use();
                return PointerResult.None;
            }
            if (!_pointerCaptured || GUIUtility.hotControl != controlId) return PointerResult.None;
            if (current.type == EventType.MouseDrag && current.button == 0)
            {
                _marqueeEnd = current.mousePosition;
                if (!_marqueeActive && (_marqueeEnd - _marqueeStart).sqrMagnitude >= 25f) _marqueeActive = true;
                current.Use();
                sceneView.Repaint();
                return PointerResult.None;
            }
            if (current.type != EventType.MouseUp || current.button != 0) return PointerResult.None;
            var result = _marqueeActive ? PointerResult.Marquee : PointerResult.Click;
            rect = NormalizeRect(_marqueeStart, _marqueeEnd);
            crossing = _marqueeEnd.x < _marqueeStart.x;
            ResetToolPointer();
            current.Use();
            sceneView.Repaint();
            return result;
        }

        private void ResetToolPointer()
        {
            int captured = _capturedControlId;
            _pointerCaptured = false;
            _marqueeActive = false;
            _capturedControlId = 0;
            if (captured != 0 && GUIUtility.hotControl == captured) GUIUtility.hotControl = 0;
        }

        private static Rect NormalizeRect(Vector2 start, Vector2 end)
            => Rect.MinMaxRect(
                Mathf.Min(start.x, end.x),
                Mathf.Min(start.y, end.y),
                Mathf.Max(start.x, end.x),
                Mathf.Max(start.y, end.y));

        private static int[] CollectVerticesInRect(Rect rect, Matrix4x4 localToWorld, Vector3[] vertices, Camera camera)
        {
            var result = new List<int>();
            for (int i = 0; i < vertices.Length; i++)
            {
                var world = localToWorld.MultiplyPoint3x4(vertices[i]);
                if (camera.WorldToViewportPoint(world).z <= 0f) continue;
                if (rect.Contains(HandleUtility.WorldToGUIPoint(world))) result.Add(i);
            }
            return result.ToArray();
        }

        private static int[] CollectEdgesInRect(
            Rect rect,
            bool crossing,
            Matrix4x4 localToWorld,
            HalfEdgeTopology topology,
            Vector3[] vertices,
            Camera camera)
        {
            var result = new List<int>();
            foreach (var pair in topology.EdgeFaces)
            {
                var key = pair.Key;
                if (key.A >= vertices.Length || key.B >= vertices.Length) continue;
                var worldA = localToWorld.MultiplyPoint3x4(vertices[key.A]);
                var worldB = localToWorld.MultiplyPoint3x4(vertices[key.B]);
                if (camera.WorldToViewportPoint(worldA).z <= 0f || camera.WorldToViewportPoint(worldB).z <= 0f) continue;
                var a = HandleUtility.WorldToGUIPoint(worldA);
                var b = HandleUtility.WorldToGUIPoint(worldB);
                bool containsA = rect.Contains(a);
                bool containsB = rect.Contains(b);
                bool match = crossing
                    ? containsA || containsB || SegmentCrossesRect(a, b, rect)
                    : containsA && containsB;
                if (match)
                {
                    result.Add(key.A);
                    result.Add(key.B);
                }
            }
            return result.ToArray();
        }

        private static bool SegmentCrossesRect(Vector2 a, Vector2 b, Rect rect)
        {
            var topLeft = new Vector2(rect.xMin, rect.yMin);
            var topRight = new Vector2(rect.xMax, rect.yMin);
            var bottomRight = new Vector2(rect.xMax, rect.yMax);
            var bottomLeft = new Vector2(rect.xMin, rect.yMax);
            return SegmentsIntersect(a, b, topLeft, topRight)
                || SegmentsIntersect(a, b, topRight, bottomRight)
                || SegmentsIntersect(a, b, bottomRight, bottomLeft)
                || SegmentsIntersect(a, b, bottomLeft, topLeft);
        }

        private static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float abC = Cross2(b - a, c - a);
            float abD = Cross2(b - a, d - a);
            float cdA = Cross2(d - c, a - c);
            float cdB = Cross2(d - c, b - c);
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

        private static float Cross2(Vector2 left, Vector2 right) => left.x * right.y - left.y * right.x;

        private static int PickVertex(Vector2 mousePosition, Matrix4x4 localToWorld, Vector3[] vertices)
        {
            int best = -1;
            float bestDistance = VertexPickRadiusPixels;
            for (int i = 0; i < vertices.Length; i++)
            {
                var gui = HandleUtility.WorldToGUIPoint(localToWorld.MultiplyPoint3x4(vertices[i]));
                float distance = Vector2.Distance(gui, mousePosition);
                if (distance < bestDistance) { bestDistance = distance; best = i; }
            }
            return best;
        }

        private static MeshEdgeKey? PickEdge(Vector2 mousePosition, Matrix4x4 localToWorld, HalfEdgeTopology topology, Vector3[] vertices)
        {
            MeshEdgeKey? best = null;
            float bestDistance = EdgePickRadiusPixels;
            foreach (var pair in topology.EdgeFaces)
            {
                var key = pair.Key;
                if (key.A >= vertices.Length || key.B >= vertices.Length) continue;
                var a = HandleUtility.WorldToGUIPoint(localToWorld.MultiplyPoint3x4(vertices[key.A]));
                var b = HandleUtility.WorldToGUIPoint(localToWorld.MultiplyPoint3x4(vertices[key.B]));
                float distance = DistanceToSegment(mousePosition, a, b);
                if (distance < bestDistance) { bestDistance = distance; best = key; }
            }
            return best;
        }

        internal static int[] CollectEdgeLoop(HalfEdgeTopology topology, IReadOnlyList<Vector3> vertices, MeshEdgeKey start)
        {
            foreach (var loop in topology.BoundaryLoops())
                for (int i = 0; i < loop.Length; i++)
                {
                    int next = loop[(i + 1) % loop.Length];
                    if (new MeshEdgeKey(loop[i], next).Equals(start)) return loop;
                }

            var result = new List<MeshEdgeKey> { start };
            var visited = new HashSet<MeshEdgeKey> { start };
            var current = start;
            for (int step = 0; step < WalkCap; step++)
            {
                if (!TryLoopContinuation(topology, vertices, current, visited, out var next)) break;
                if (next.Equals(start)) break;
                result.Add(next);
                visited.Add(next);
                current = next;
            }
            return Flatten(result);
        }

        internal static int[] CollectEdgeRing(HalfEdgeTopology topology, IReadOnlyList<Vector3> vertices, MeshEdgeKey start)
        {
            var result = new List<MeshEdgeKey> { start };
            var visited = new HashSet<MeshEdgeKey> { start };
            var current = start;
            for (int step = 0; step < WalkCap; step++)
            {
                if (!TryRingContinuation(topology, vertices, current, visited, out var next)) break;
                result.Add(next);
                visited.Add(next);
                current = next;
            }
            return Flatten(result);
        }

        private static bool TryLoopContinuation(
            HalfEdgeTopology topology,
            IReadOnlyList<Vector3> vertices,
            MeshEdgeKey current,
            HashSet<MeshEdgeKey> visited,
            out MeshEdgeKey next)
        {
            next = default;
            float bestDot = 0.35f;
            var direction = EdgeDirection(vertices, current);
            if (!topology.EdgeFaces.TryGetValue(current, out var faces) || faces.Length != 2) return false;
            for (int f = 0; f < faces.Length; f++)
                foreach (var candidate in FaceEdgeKeys(topology.Faces[faces[f]]))
                {
                    if (candidate.Equals(current) || visited.Contains(candidate)) continue;
                    if (!topology.EdgeFaces.TryGetValue(candidate, out var candidateFaces) || candidateFaces.Length != 2) continue;
                    float dot = Mathf.Abs(Vector3.Dot(direction, EdgeDirection(vertices, candidate)));
                    if (dot > bestDot) { bestDot = dot; next = candidate; }
                }
            return !next.Equals(default(MeshEdgeKey)) && bestDot > 0.35f;
        }

        private static IEnumerable<MeshEdgeKey> FaceEdgeKeys(HalfEdgeTopology.Face face)
        {
            yield return new MeshEdgeKey(face.A, face.B);
            yield return new MeshEdgeKey(face.B, face.C);
            yield return new MeshEdgeKey(face.C, face.A);
        }

        private static bool TryRingContinuation(
            HalfEdgeTopology topology,
            IReadOnlyList<Vector3> vertices,
            MeshEdgeKey current,
            HashSet<MeshEdgeKey> visited,
            out MeshEdgeKey next)
        {
            next = default;
            if (!topology.EdgeFaces.TryGetValue(current, out var faces) || faces.Length != 2) return false;
            float bestDot = 0.5f;
            var direction = EdgeDirection(vertices, current);
            for (int f = 0; f < faces.Length; f++)
            {
                var face = topology.Faces[faces[f]];
                foreach (var candidate in new[] { new MeshEdgeKey(face.A, face.B), new MeshEdgeKey(face.B, face.C), new MeshEdgeKey(face.C, face.A) })
                {
                    if (candidate.Equals(current) || visited.Contains(candidate)) continue;
                    if (candidate.A == current.A || candidate.A == current.B || candidate.B == current.A || candidate.B == current.B)
                    {
                        float dot = Mathf.Abs(Vector3.Dot(direction, EdgeDirection(vertices, candidate)));
                        if (dot > bestDot) { bestDot = dot; next = candidate; }
                    }
                }
            }
            return !next.Equals(default(MeshEdgeKey)) && bestDot > 0.5f;
        }

        private static Vector3 EdgeDirection(IReadOnlyList<Vector3> vertices, MeshEdgeKey key)
        {
            if (key.A >= vertices.Count || key.B >= vertices.Count) return Vector3.right;
            var direction = vertices[key.B] - vertices[key.A];
            return direction.sqrMagnitude > 1e-12f ? direction.normalized : Vector3.right;
        }

        private static int[] Flatten(List<MeshEdgeKey> edges)
        {
            var result = new int[edges.Count * 2];
            for (int i = 0; i < edges.Count; i++)
            {
                result[i * 2] = edges[i].A;
                result[i * 2 + 1] = edges[i].B;
            }
            return result;
        }

        private static int[] CollectDominantVertices(Mesh mesh, int boneIndex)
        {
            var weights = mesh.boneWeights;
            var result = new List<int>();
            for (int i = 0; i < weights.Length; i++)
            {
                var weight = weights[i];
                int dominant = weight.boneIndex0;
                float strongest = weight.weight0;
                if (weight.weight1 > strongest) { strongest = weight.weight1; dominant = weight.boneIndex1; }
                if (weight.weight2 > strongest) { strongest = weight.weight2; dominant = weight.boneIndex2; }
                if (weight.weight3 > strongest) { dominant = weight.boneIndex3; }
                if (dominant == boneIndex) result.Add(i);
            }
            return result.ToArray();
        }

        private static string FindRegionAtFace(MeshEditDocument document, int faceIndex)
        {
            if (document == null) return null;
            var regions = document.Selection.Regions;
            for (int i = 0; i < regions.Count; i++)
                if (regions[i].Kind == MeshElementKind.Face && regions[i].Indices.Contains(faceIndex))
                    return regions[i].Name;
            return null;
        }

        private static IReadOnlyList<int> SelectedIndices(AssetModelerWindow modeler, MeshElementKind kind)
        {
            var document = modeler.SceneDocument;
            return document != null && document.Selection.Kind == kind
                ? document.Selection.Indices
                : (IReadOnlyList<int>)Array.Empty<int>();
        }

        private static bool TrySelectionFrame(HalfEdgeTopology topology, Vector3[] vertices, IReadOnlyList<int> faces, out Vector3 centroid, out Vector3 normal)
        {
            centroid = Vector3.zero;
            normal = Vector3.zero;
            int count = 0;
            for (int i = 0; i < faces.Count; i++)
            {
                int index = faces[i];
                if (index < 0 || index >= topology.Faces.Count) continue;
                var face = topology.Faces[index];
                if (face.A >= vertices.Length || face.B >= vertices.Length || face.C >= vertices.Length) continue;
                var a = vertices[face.A];
                var b = vertices[face.B];
                var c = vertices[face.C];
                centroid += (a + b + c) / 3f;
                normal += Vector3.Cross(b - a, c - a);
                count++;
            }
            if (count == 0) return false;
            centroid /= count;
            normal = normal.sqrMagnitude > 1e-12f ? normal.normalized : Vector3.up;
            return true;
        }

        private static float SelectionScale(Vector3[] vertices, IReadOnlyList<int> faces, HalfEdgeTopology topology)
        {
            if (!TrySelectionFrame(topology, vertices, faces, out var centroid, out _)) return 1f;
            float max = 0f;
            for (int i = 0; i < faces.Count; i++)
            {
                int index = faces[i];
                if (index < 0 || index >= topology.Faces.Count) continue;
                var face = topology.Faces[index];
                foreach (int v in new[] { face.A, face.B, face.C })
                    if (v < vertices.Length) max = Mathf.Max(max, (vertices[v] - centroid).magnitude);
            }
            return Mathf.Max(max, 1e-4f);
        }

        private static Vector3 BoundsCenter(Vector3[] vertices)
        {
            if (vertices.Length == 0) return Vector3.zero;
            var min = vertices[0];
            var max = vertices[0];
            for (int i = 1; i < vertices.Length; i++)
            {
                min = Vector3.Min(min, vertices[i]);
                max = Vector3.Max(max, vertices[i]);
            }
            return (min + max) * 0.5f;
        }

        private static Vector3 AxisVector(int axis)
            => axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;

        private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float length = ab.sqrMagnitude;
            if (length < 1e-8f) return Vector2.Distance(point, a);
            float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / length);
            return Vector2.Distance(point, a + ab * t);
        }

        private static float BonePickRadiusPixels() => 18f;

        private static void DrawHint(SceneView sceneView, string message)
        {
            Handles.BeginGUI();
            GUILayout.BeginArea(new Rect(12f, 12f, 320f, 28f), EditorStyles.helpBox);
            GUILayout.Label(message, EditorStyles.miniLabel);
            GUILayout.EndArea();
            Handles.EndGUI();
        }
    }
}
