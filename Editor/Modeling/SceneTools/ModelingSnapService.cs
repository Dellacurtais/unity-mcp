using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.SceneTools
{
    public enum ModelingSnapKind
    {
        Grid,
        Vertex,
        Midpoint,
        Edge,
        Face,
        Surface,
        Bone,
        Socket,
    }

    [Serializable]
    public sealed class ModelingSnapSettings
    {
        public ModelingSnapKind Kind = ModelingSnapKind.Vertex;
        public float MaximumDistance = 0.25f;
        public float GridSize = 0.1f;
        public bool ConstrainX;
        public bool ConstrainY;
        public bool ConstrainZ;
    }

    public readonly struct ModelingSnapResult
    {
        public bool Found { get; }
        public Vector3 Point { get; }
        public Vector3 Normal { get; }
        public UnityEngine.Object Target { get; }
        public ModelingSnapKind Kind { get; }
        public float Distance { get; }

        public ModelingSnapResult(bool found, Vector3 point, Vector3 normal, UnityEngine.Object target, ModelingSnapKind kind, float distance)
        {
            Found = found;
            Point = point;
            Normal = normal;
            Target = target;
            Kind = kind;
            Distance = distance;
        }
    }

    public static class ModelingSnapService
    {
        public static ModelingSnapResult FindNearest(
            Vector3 worldPoint,
            IReadOnlyList<GameObject> targets,
            ModelingSnapSettings settings)
        {
            settings = settings ?? new ModelingSnapSettings();
            if (settings.Kind == ModelingSnapKind.Grid)
            {
                float grid = Mathf.Max(1e-6f, settings.GridSize);
                var snapped = new Vector3(
                    Mathf.Round(worldPoint.x / grid) * grid,
                    Mathf.Round(worldPoint.y / grid) * grid,
                    Mathf.Round(worldPoint.z / grid) * grid);
                return Result(worldPoint, ApplyAxisConstraint(worldPoint, snapped, settings), Vector3.up, null, settings.Kind);
            }

            var best = new ModelingSnapResult(false, worldPoint, Vector3.up, null, settings.Kind, float.PositiveInfinity);
            if (targets == null) return best;
            for (int rootIndex = 0; rootIndex < targets.Count; rootIndex++)
            {
                var root = targets[rootIndex];
                if (root == null) continue;
                if (settings.Kind == ModelingSnapKind.Bone || settings.Kind == ModelingSnapKind.Socket)
                {
                    var transforms = root.GetComponentsInChildren<Transform>(true);
                    for (int i = 0; i < transforms.Length; i++)
                    {
                        if (settings.Kind == ModelingSnapKind.Socket
                            && transforms[i].name.IndexOf("socket", StringComparison.OrdinalIgnoreCase) < 0
                            && transforms[i].name.IndexOf("attach", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        best = Choose(best, Result(worldPoint, transforms[i].position, transforms[i].up, transforms[i], settings.Kind));
                    }
                    continue;
                }

                var filters = root.GetComponentsInChildren<MeshFilter>(true);
                for (int filterIndex = 0; filterIndex < filters.Length; filterIndex++)
                    if (filters[filterIndex] != null && filters[filterIndex].sharedMesh != null && filters[filterIndex].sharedMesh.isReadable)
                        ScanMesh(worldPoint, filters[filterIndex].sharedMesh, filters[filterIndex].transform, filters[filterIndex], settings.Kind, ref best);
                var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (int rendererIndex = 0; rendererIndex < skinned.Length; rendererIndex++)
                {
                    if (skinned[rendererIndex] == null || skinned[rendererIndex].sharedMesh == null) continue;
                    var baked = new Mesh();
                    try
                    {
                        skinned[rendererIndex].BakeMesh(baked);
                        ScanMesh(worldPoint, baked, skinned[rendererIndex].transform, skinned[rendererIndex], settings.Kind, ref best);
                    }
                    finally { UnityEngine.Object.DestroyImmediate(baked); }
                }
            }
            if (best.Found && best.Distance <= settings.MaximumDistance)
            {
                var constrained = ApplyAxisConstraint(worldPoint, best.Point, settings);
                return new ModelingSnapResult(true, constrained, best.Normal, best.Target, best.Kind, Vector3.Distance(worldPoint, constrained));
            }
            return new ModelingSnapResult(false, worldPoint, Vector3.up, null, settings.Kind, float.PositiveInfinity);
        }

        public static void SnapTransform(Transform item, ModelingSnapResult result)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (!result.Found) throw new InvalidOperationException("No valid snap target was found.");
            Undo.RecordObject(item, "Snap transform");
            item.position = result.Point;
        }

        public static void FitByThreeAnchors(
            Transform item,
            Vector3 sourceOrigin,
            Vector3 sourceForward,
            Vector3 sourceUp,
            Vector3 targetOrigin,
            Vector3 targetForward,
            Vector3 targetUp,
            bool matchScale)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            var sourceFrame = Frame(sourceOrigin, sourceForward, sourceUp);
            var targetFrame = Frame(targetOrigin, targetForward, targetUp);
            var rotation = targetFrame.Rotation * Quaternion.Inverse(sourceFrame.Rotation);
            float scale = matchScale && sourceFrame.Scale > 1e-8f ? targetFrame.Scale / sourceFrame.Scale : 1f;
            Undo.RecordObject(item, "Fit accessory to anchors");
            item.rotation = rotation * item.rotation;
            if (matchScale) item.localScale *= scale;
            item.position = targetOrigin - item.TransformVector(sourceOrigin);
        }

        private static void ScanMesh(
            Vector3 worldPoint,
            Mesh mesh,
            Transform transform,
            UnityEngine.Object owner,
            ModelingSnapKind kind,
            ref ModelingSnapResult best)
        {
            var vertices = mesh.vertices;
            if (kind == ModelingSnapKind.Vertex)
            {
                for (int i = 0; i < vertices.Length; i++)
                    best = Choose(best, Result(worldPoint, transform.TransformPoint(vertices[i]), Vector3.up, owner, kind));
                return;
            }
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                if (mesh.GetTopology(subMesh) != MeshTopology.Triangles) continue;
                var indices = mesh.GetIndices(subMesh, true);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    var a = transform.TransformPoint(vertices[indices[i]]);
                    var b = transform.TransformPoint(vertices[indices[i + 1]]);
                    var c = transform.TransformPoint(vertices[indices[i + 2]]);
                    var normal = Vector3.Cross(b - a, c - a).normalized;
                    if (kind == ModelingSnapKind.Midpoint)
                    {
                        best = Choose(best, Result(worldPoint, (a + b) * 0.5f, normal, owner, kind));
                        best = Choose(best, Result(worldPoint, (b + c) * 0.5f, normal, owner, kind));
                        best = Choose(best, Result(worldPoint, (c + a) * 0.5f, normal, owner, kind));
                    }
                    else if (kind == ModelingSnapKind.Edge)
                    {
                        best = Choose(best, Result(worldPoint, ClosestOnSegment(worldPoint, a, b), normal, owner, kind));
                        best = Choose(best, Result(worldPoint, ClosestOnSegment(worldPoint, b, c), normal, owner, kind));
                        best = Choose(best, Result(worldPoint, ClosestOnSegment(worldPoint, c, a), normal, owner, kind));
                    }
                    else
                    {
                        var point = kind == ModelingSnapKind.Face ? (a + b + c) / 3f : ClosestOnTriangle(worldPoint, a, b, c);
                        best = Choose(best, Result(worldPoint, point, normal, owner, kind));
                    }
                }
            }
        }

        private static ModelingSnapResult Choose(ModelingSnapResult current, ModelingSnapResult candidate)
            => !current.Found || candidate.Distance < current.Distance ? candidate : current;

        private static ModelingSnapResult Result(Vector3 origin, Vector3 point, Vector3 normal, UnityEngine.Object target, ModelingSnapKind kind)
            => new ModelingSnapResult(true, point, normal, target, kind, Vector3.Distance(origin, point));

        private static Vector3 ApplyAxisConstraint(Vector3 origin, Vector3 target, ModelingSnapSettings settings)
        {
            if (settings.ConstrainX) { target.y = origin.y; target.z = origin.z; }
            if (settings.ConstrainY) { target.x = origin.x; target.z = origin.z; }
            if (settings.ConstrainZ) { target.x = origin.x; target.y = origin.y; }
            return target;
        }

        private static Vector3 ClosestOnSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            var delta = b - a;
            float denominator = delta.sqrMagnitude;
            if (denominator <= 1e-12f) return a;
            return a + delta * Mathf.Clamp01(Vector3.Dot(point - a, delta) / denominator);
        }

        private static Vector3 ClosestOnTriangle(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
        {
            // The three edge projections plus centroid are stable for editor snapping and avoid hidden raycasts.
            var candidates = new[]
            {
                ClosestOnSegment(point, a, b),
                ClosestOnSegment(point, b, c),
                ClosestOnSegment(point, c, a),
                (a + b + c) / 3f,
            };
            var best = candidates[0];
            float distance = (best - point).sqrMagnitude;
            for (int i = 1; i < candidates.Length; i++)
            {
                float candidate = (candidates[i] - point).sqrMagnitude;
                if (candidate >= distance) continue;
                distance = candidate;
                best = candidates[i];
            }
            return best;
        }

        private static AnchorFrame Frame(Vector3 origin, Vector3 forwardPoint, Vector3 upPoint)
        {
            var forward = forwardPoint - origin;
            var up = upPoint - origin;
            if (forward.sqrMagnitude <= 1e-12f || up.sqrMagnitude <= 1e-12f)
                throw new InvalidOperationException("ANCHORS_DEGENERATE: anchor directions must have non-zero length.");
            forward.Normalize();
            up = Vector3.ProjectOnPlane(up, forward).normalized;
            if (up.sqrMagnitude <= 1e-12f) throw new InvalidOperationException("ANCHORS_COLLINEAR: forward and up anchors cannot be collinear.");
            return new AnchorFrame
            {
                Rotation = Quaternion.LookRotation(forward, up),
                Scale = 0.5f * (Vector3.Distance(origin, forwardPoint) + Vector3.Distance(origin, upPoint)),
            };
        }

        private struct AnchorFrame
        {
            public Quaternion Rotation;
            public float Scale;
        }
    }
}
