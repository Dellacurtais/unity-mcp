using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.SceneUx
{
    internal enum AssetModelerEditMode
    {
        Object = 0,
        Vertex = 1,
        Edge = 2,
        Face = 3,
        Region = 4,
        Bone = 5,
    }

    internal enum ModelingHandleOrientation
    {
        Global = 0,
        Local = 1,
        Normal = 2,
    }

    internal static class AssetModelerModelingState
    {
        private const string ModeKey = "AssetModeler.SceneUx.Mode";
        private const string OrientationKey = "AssetModeler.SceneUx.Orientation";
        private const string SymmetryKey = "AssetModeler.SceneUx.Symmetry";
        private const string SnapEnabledKey = "AssetModeler.SceneUx.SnapEnabled";
        private const string SnapStepKey = "AssetModeler.SceneUx.SnapStep";
        private const string ShowBoundariesKey = "AssetModeler.SceneUx.ShowBoundaries";
        private const string ShowNonManifoldKey = "AssetModeler.SceneUx.ShowNonManifold";
        private const string ShowFlippedKey = "AssetModeler.SceneUx.ShowFlippedNormals";
        private const string ShowUvSeamsKey = "AssetModeler.SceneUx.ShowUvSeams";

        public static AssetModelerEditMode Mode
        {
            get => (AssetModelerEditMode)SessionState.GetInt(ModeKey, (int)AssetModelerEditMode.Face);
            set { if (Mode == value) return; SessionState.SetInt(ModeKey, (int)value); SceneView.RepaintAll(); }
        }

        public static ModelingHandleOrientation Orientation
        {
            get => (ModelingHandleOrientation)SessionState.GetInt(OrientationKey, (int)ModelingHandleOrientation.Normal);
            set { if (Orientation == value) return; SessionState.SetInt(OrientationKey, (int)value); SceneView.RepaintAll(); }
        }

        public static bool Symmetry
        {
            get => SessionState.GetBool(SymmetryKey, false);
            set { if (Symmetry == value) return; SessionState.SetBool(SymmetryKey, value); SceneView.RepaintAll(); }
        }

        public static bool SnapEnabled
        {
            get => SessionState.GetBool(SnapEnabledKey, false);
            set { if (SnapEnabled == value) return; SessionState.SetBool(SnapEnabledKey, value); SceneView.RepaintAll(); }
        }

        public static float SnapStep
        {
            get => SessionState.GetFloat(SnapStepKey, 0.05f);
            set { SessionState.SetFloat(SnapStepKey, Mathf.Max(0.001f, value)); }
        }

        public static bool ShowBoundaries
        {
            get => SessionState.GetBool(ShowBoundariesKey, true);
            set { if (ShowBoundaries == value) return; SessionState.SetBool(ShowBoundariesKey, value); SceneView.RepaintAll(); }
        }

        public static bool ShowNonManifold
        {
            get => SessionState.GetBool(ShowNonManifoldKey, true);
            set { if (ShowNonManifold == value) return; SessionState.SetBool(ShowNonManifoldKey, value); SceneView.RepaintAll(); }
        }

        public static bool ShowFlippedNormals
        {
            get => SessionState.GetBool(ShowFlippedKey, false);
            set { if (ShowFlippedNormals == value) return; SessionState.SetBool(ShowFlippedKey, value); SceneView.RepaintAll(); }
        }

        public static bool ShowUvSeams
        {
            get => SessionState.GetBool(ShowUvSeamsKey, false);
            set { if (ShowUvSeams == value) return; SessionState.SetBool(ShowUvSeamsKey, value); SceneView.RepaintAll(); }
        }

        public static float SnapValue(float value)
        {
            if (!SnapEnabled) return value;
            float step = SnapStep;
            return Mathf.Round(value / step) * step;
        }

        public static void ActivateMode(AssetModelerEditMode mode)
        {
            if (!AssetModelerToolContext.IsActive)
                ToolManager.SetActiveContext(typeof(AssetModelerToolContext));
            Mode = mode;
        }

        public static int[] ExpandSymmetric(IReadOnlyList<Vector3> vertices, IEnumerable<int> selection, float tolerance = 1e-4f)
        {
            var result = new SortedSet<int>();
            if (selection != null) foreach (int index in selection) if (index >= 0) result.Add(index);
            if (!Symmetry || vertices == null) return ToArray(result);

            float inverse = 1f / Mathf.Max(tolerance, 1e-6f);
            var buckets = new Dictionary<long, List<int>>();
            for (int i = 0; i < vertices.Count; i++)
            {
                long key = BucketKey(vertices[i], inverse);
                if (!buckets.TryGetValue(key, out var list)) buckets.Add(key, list = new List<int>(2));
                list.Add(i);
            }

            var seeds = new List<int>(result);
            for (int i = 0; i < seeds.Count; i++)
            {
                int index = seeds[i];
                if (index >= vertices.Count) continue;
                long key = BucketKey(vertices[index], inverse);
                if (!buckets.TryGetValue(key, out var list)) continue;
                for (int j = 0; j < list.Count; j++) result.Add(list[j]);
            }
            return ToArray(result);
        }

        private static long BucketKey(Vector3 vertex, float inverse)
        {
            long x = (long)Mathf.Round(Mathf.Abs(vertex.x) * inverse);
            long y = (long)Mathf.Round(vertex.y * inverse);
            long z = (long)Mathf.Round(vertex.z * inverse);
            return (x * 73856093L) ^ (y * 19349663L) ^ (z * 83492791L);
        }

        private static int[] ToArray(SortedSet<int> values)
        {
            var result = new int[values.Count];
            values.CopyTo(result);
            return result;
        }
    }

    internal static class AssetModelerShortcuts
    {
        [Shortcut("Asset Modeler/Modo Objeto", typeof(SceneView), KeyCode.Alpha1)]
        private static void ObjectMode() => AssetModelerModelingState.ActivateMode(AssetModelerEditMode.Object);

        [Shortcut("Asset Modeler/Modo Vertice", typeof(SceneView), KeyCode.Alpha2)]
        private static void VertexMode() => AssetModelerModelingState.ActivateMode(AssetModelerEditMode.Vertex);

        [Shortcut("Asset Modeler/Modo Aresta", typeof(SceneView), KeyCode.Alpha3)]
        private static void EdgeMode() => AssetModelerModelingState.ActivateMode(AssetModelerEditMode.Edge);

        [Shortcut("Asset Modeler/Modo Face", typeof(SceneView), KeyCode.Alpha4)]
        private static void FaceMode() => AssetModelerModelingState.ActivateMode(AssetModelerEditMode.Face);

        [Shortcut("Asset Modeler/Modo Regiao", typeof(SceneView), KeyCode.Alpha5)]
        private static void RegionMode() => AssetModelerModelingState.ActivateMode(AssetModelerEditMode.Region);

        [Shortcut("Asset Modeler/Modo Bone", typeof(SceneView), KeyCode.Alpha6)]
        private static void BoneMode() => AssetModelerModelingState.ActivateMode(AssetModelerEditMode.Bone);

        [Shortcut("Asset Modeler/Alternar Simetria", typeof(SceneView), KeyCode.X, ShortcutModifiers.Alt)]
        private static void ToggleSymmetry() => AssetModelerModelingState.Symmetry = !AssetModelerModelingState.Symmetry;

        [Shortcut("Asset Modeler/Alternar Snap", typeof(SceneView), KeyCode.S, ShortcutModifiers.Alt)]
        private static void ToggleSnap() => AssetModelerModelingState.SnapEnabled = !AssetModelerModelingState.SnapEnabled;
    }
}
