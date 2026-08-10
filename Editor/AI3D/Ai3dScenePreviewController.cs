using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.AI3D
{
    [InitializeOnLoad]
    public static class Ai3dScenePreviewController
    {
        private enum PreviewTool
        {
            Move,
            Rotate,
            Scale,
        }

        private sealed class MeshDraw
        {
            public Mesh Mesh;
            public Matrix4x4 RelativeMatrix;
            public Material[] Materials;
        }

        private static readonly List<MeshDraw> Draws = new List<MeshDraw>();
        private static GameObject _asset;
        private static string _label;
        private static Bounds _localBounds;
        private static Vector3 _position;
        private static Quaternion _rotation = Quaternion.identity;
        private static Vector3 _scale = Vector3.one;
        private static PreviewTool _tool;

        static Ai3dScenePreviewController()
        {
            SceneView.duringSceneGui += Draw;
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
            EditorApplication.quitting += Clear;
        }

        public static GameObject ActiveAsset => _asset;

        public static void Show(GameObject asset, string label = null)
        {
            Clear();
            if (asset == null) return;
            _asset = asset;
            _label = string.IsNullOrEmpty(label) ? asset.name : label;
            _rotation = Quaternion.identity;
            _scale = Vector3.one;
            BuildDraws(asset);
            var view = SceneView.lastActiveSceneView;
            _position = view != null ? view.pivot : Vector3.zero;
            SceneView.RepaintAll();
            Frame(view);
        }

        public static void Clear()
        {
            _asset = null;
            _label = null;
            Draws.Clear();
            _localBounds = new Bounds(Vector3.zero, Vector3.one);
            SceneView.RepaintAll();
        }

        public static void Frame(SceneView view = null)
        {
            if (_asset == null) return;
            view = view ?? SceneView.lastActiveSceneView;
            if (view == null) return;
            view.Frame(WorldBounds(), false);
            view.Repaint();
        }

        private static void BuildDraws(GameObject root)
        {
            var initialized = false;
            var rootInverse = root.transform.worldToLocalMatrix;
            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                var mesh = filters[i].sharedMesh;
                var renderer = filters[i].GetComponent<MeshRenderer>();
                if (mesh == null || renderer == null) continue;
                var relative = rootInverse * filters[i].transform.localToWorldMatrix;
                Draws.Add(new MeshDraw { Mesh = mesh, RelativeMatrix = relative, Materials = renderer.sharedMaterials });
                Encapsulate(ref _localBounds, ref initialized, TransformBounds(mesh.bounds, relative));
            }
            var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++)
            {
                var mesh = skinned[i].sharedMesh;
                if (mesh == null) continue;
                var relative = rootInverse * skinned[i].transform.localToWorldMatrix;
                Draws.Add(new MeshDraw { Mesh = mesh, RelativeMatrix = relative, Materials = skinned[i].sharedMaterials });
                Encapsulate(ref _localBounds, ref initialized, TransformBounds(mesh.bounds, relative));
            }
            if (!initialized) _localBounds = new Bounds(Vector3.zero, Vector3.one);
        }

        private static void Draw(SceneView sceneView)
        {
            if (_asset == null || Draws.Count == 0) return;
            var baseMatrix = BaseMatrix();
            if (Event.current.type == EventType.Repaint)
            {
                var fallback = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
                for (int drawIndex = 0; drawIndex < Draws.Count; drawIndex++)
                {
                    var draw = Draws[drawIndex];
                    var matrix = baseMatrix * draw.RelativeMatrix;
                    for (int subMesh = 0; subMesh < draw.Mesh.subMeshCount; subMesh++)
                    {
                        var material = draw.Materials != null && draw.Materials.Length > 0
                            ? draw.Materials[Mathf.Min(subMesh, draw.Materials.Length - 1)]
                            : fallback;
                        if (material != null)
                            Graphics.DrawMesh(draw.Mesh, matrix, material, 0, sceneView.camera, subMesh);
                    }
                }
                Handles.color = new Color(0.1f, 0.85f, 1f, 0.9f);
                var worldBounds = WorldBounds();
                Handles.DrawWireCube(worldBounds.center, worldBounds.size);
                Handles.Label(worldBounds.center + Vector3.up * worldBounds.extents.y, "AI 3D Preview — " + _label);
            }

            EditorGUI.BeginChangeCheck();
            var position = _position;
            var rotation = _rotation;
            var scale = _scale;
            switch (_tool)
            {
                case PreviewTool.Rotate:
                    rotation = Handles.RotationHandle(_rotation, _position);
                    break;
                case PreviewTool.Scale:
                    scale = Handles.ScaleHandle(
                        _scale, _position, _rotation, HandleUtility.GetHandleSize(_position));
                    break;
                default:
                    var handleRotation = Tools.pivotRotation == PivotRotation.Local ? _rotation : Quaternion.identity;
                    position = Handles.PositionHandle(_position, handleRotation);
                    break;
            }
            if (EditorGUI.EndChangeCheck())
            {
                _position = position;
                _rotation = rotation;
                _scale = new Vector3(
                    Mathf.Max(0.0001f, scale.x),
                    Mathf.Max(0.0001f, scale.y),
                    Mathf.Max(0.0001f, scale.z));
                sceneView.Repaint();
            }

            Handles.BeginGUI();
            var rect = new Rect(12f, 12f, 330f, 96f);
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);
            GUI.Label(new Rect(22f, 18f, 300f, 18f), "AI 3D candidate: " + _label, EditorStyles.boldLabel);
            _tool = (PreviewTool)GUI.Toolbar(
                new Rect(22f, 40f, 214f, 24f), (int)_tool, new[] { "Mover", "Rotacionar", "Escala" });
            if (GUI.Button(new Rect(242f, 40f, 80f, 24f), "Frame")) Frame(sceneView);
            if (GUI.Button(new Rect(22f, 70f, 104f, 24f), "Abrir Studio")) Ai3dStudioWindow.Open();
            if (GUI.Button(new Rect(132f, 70f, 104f, 24f), "Reset transform"))
            {
                _rotation = Quaternion.identity;
                _scale = Vector3.one;
                _position = sceneView.pivot;
                Frame(sceneView);
            }
            if (GUI.Button(new Rect(242f, 70f, 80f, 24f), "Limpar")) Clear();
            Handles.EndGUI();
        }

        private static Matrix4x4 BaseMatrix()
            => Matrix4x4.TRS(_position, _rotation, _scale) * Matrix4x4.Translate(-_localBounds.center);

        private static Bounds WorldBounds() => TransformBounds(_localBounds, BaseMatrix());

        private static void Encapsulate(ref Bounds target, ref bool initialized, Bounds value)
        {
            if (!initialized)
            {
                target = value;
                initialized = true;
            }
            else target.Encapsulate(value);
        }

        private static Bounds TransformBounds(Bounds bounds, Matrix4x4 matrix)
        {
            var center = matrix.MultiplyPoint3x4(bounds.center);
            var extents = bounds.extents;
            var axisX = matrix.MultiplyVector(new Vector3(extents.x, 0f, 0f));
            var axisY = matrix.MultiplyVector(new Vector3(0f, extents.y, 0f));
            var axisZ = matrix.MultiplyVector(new Vector3(0f, 0f, extents.z));
            var worldExtents = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
            return new Bounds(center, worldExtents * 2f);
        }
    }
}
