using System;
using System.Collections.Generic;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
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
            set
            {
                if (Mode == value) return;
                SessionState.SetInt(ModeKey, (int)value);
                SynchronizeActiveTool(value);
                SceneView.RepaintAll();
            }
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

        public static float SnapValue(float value, float maximumStep)
        {
            if (!SnapEnabled) return value;
            float step = Mathf.Min(SnapStep, Mathf.Max(0.0001f, maximumStep));
            return Mathf.Round(value / step) * step;
        }

        public static void ActivateMode(AssetModelerEditMode mode)
        {
            if (!AssetModelerToolContext.IsActive)
                ToolManager.SetActiveContext(typeof(AssetModelerToolContext));
            if (Mode == mode) SynchronizeActiveTool(mode);
            else Mode = mode;
        }

        public static int[] ExpandSymmetric(IReadOnlyList<Vector3> vertices, IEnumerable<int> selection, float tolerance = 1e-4f)
        {
            var result = new SortedSet<int>();
            if (selection != null) foreach (int index in selection) if (index >= 0) result.Add(index);
            if (!Symmetry || vertices == null) return ToArray(result);

            float inverse = 1f / Mathf.Max(tolerance, 1e-6f);
            var buckets = new Dictionary<MirrorBucket, List<int>>();
            for (int i = 0; i < vertices.Count; i++)
            {
                var key = BucketKey(vertices[i], inverse);
                if (!buckets.TryGetValue(key, out var list)) buckets.Add(key, list = new List<int>(2));
                list.Add(i);
            }

            var seeds = new List<int>(result);
            for (int i = 0; i < seeds.Count; i++)
            {
                int index = seeds[i];
                if (index >= vertices.Count) continue;
                var source = vertices[index];
                var key = BucketKey(source, inverse);
                if (!buckets.TryGetValue(key, out var list)) continue;
                var mirrored = new Vector3(-source.x, source.y, source.z);
                float squaredTolerance = tolerance * tolerance;
                for (int j = 0; j < list.Count; j++)
                    if ((vertices[list[j]] - mirrored).sqrMagnitude <= squaredTolerance) result.Add(list[j]);
            }
            return ToArray(result);
        }

        internal static int[] ExpandSymmetricEdges(
            IReadOnlyList<Vector3> vertices,
            IEnumerable<int> encodedEdges,
            float tolerance = 1e-4f)
        {
            var edges = MeshSelectionEncoding.EdgePairs(encodedEdges);
            if (!Symmetry || vertices == null) return MeshSelectionEncoding.FlattenEdges(edges).ToArray();

            var expanded = new HashSet<MeshEdgeKey>(edges);
            for (int i = 0; i < edges.Count; i++)
            {
                int[] first = ExpandSymmetric(vertices, new[] { edges[i].A }, tolerance);
                int[] second = ExpandSymmetric(vertices, new[] { edges[i].B }, tolerance);
                int mirroredA = FindMirrorCandidate(vertices, edges[i].A, first, tolerance);
                int mirroredB = FindMirrorCandidate(vertices, edges[i].B, second, tolerance);
                if (mirroredA >= 0 && mirroredB >= 0 && mirroredA != mirroredB)
                    expanded.Add(new MeshEdgeKey(mirroredA, mirroredB));
            }
            return MeshSelectionEncoding.FlattenEdges(expanded.OrderBy(edge => edge.A).ThenBy(edge => edge.B)).ToArray();
        }

        private static void SynchronizeActiveTool(AssetModelerEditMode mode)
        {
            ToolManager.RefreshAvailableTools();
            if (!AssetModelerToolContext.IsActive) return;
            if (mode == AssetModelerEditMode.Object)
            {
                Tools.current = Tool.Move;
                ToolManager.RestorePreviousPersistentTool();
            }
            else ToolManager.SetActiveTool(typeof(AssetModelerModelingTool));
        }

        private static int FindMirrorCandidate(
            IReadOnlyList<Vector3> vertices,
            int sourceIndex,
            IReadOnlyList<int> candidates,
            float tolerance)
        {
            if (sourceIndex < 0 || sourceIndex >= vertices.Count) return -1;
            var source = vertices[sourceIndex];
            var mirrored = new Vector3(-source.x, source.y, source.z);
            float bestDistance = tolerance * tolerance;
            int best = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                int candidate = candidates[i];
                if (candidate < 0 || candidate >= vertices.Count) continue;
                float distance = (vertices[candidate] - mirrored).sqrMagnitude;
                if (distance > bestDistance) continue;
                bestDistance = distance;
                best = candidate;
            }
            return best;
        }

        private static MirrorBucket BucketKey(Vector3 vertex, float inverse)
            => new MirrorBucket(
                Mathf.RoundToInt(Mathf.Abs(vertex.x) * inverse),
                Mathf.RoundToInt(vertex.y * inverse),
                Mathf.RoundToInt(vertex.z * inverse));

        private readonly struct MirrorBucket : IEquatable<MirrorBucket>
        {
            private readonly int _x;
            private readonly int _y;
            private readonly int _z;

            public MirrorBucket(int x, int y, int z) { _x = x; _y = y; _z = z; }
            public bool Equals(MirrorBucket other) => _x == other._x && _y == other._y && _z == other._z;
            public override bool Equals(object obj) => obj is MirrorBucket other && Equals(other);
            public override int GetHashCode() => unchecked(((_x * 397) ^ _y) * 397 ^ _z);
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
