using Maicon.SceneAnalyzer.Editor.Core;
using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Maicon.SceneAnalyzer.Editor.Modeling.SceneTools
{
    public enum SceneArrayMode
    {
        Linear,
        Radial,
        Grid,
    }

    [Serializable]
    public sealed class SceneArraySettings
    {
        public SceneArrayMode Mode = SceneArrayMode.Linear;
        public int Count = 2;
        public Vector3 Step = Vector3.right;
        public bool AttachedByBounds;
        public Vector3Int GridCount = new Vector3Int(2, 1, 1);
        public Vector3 GridSpacing = Vector3.one;
        public Vector3 RadialAxis = Vector3.up;
        public float RadialAngle = 360f;
        public bool IncludeSource = true;
    }

    public static class SceneObjectOperations
    {
        public static IReadOnlyList<GameObject> CreateArray(GameObject source, SceneArraySettings settings)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            settings = settings ?? new SceneArraySettings();
            var transforms = BuildArrayTransforms(source, settings);
            var created = new List<GameObject>();
            int start = settings.IncludeSource ? 1 : 0;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Asset Modeler Array");
            for (int i = start; i < transforms.Count; i++)
            {
                var clone = UnityEngine.Object.Instantiate(source, source.transform.parent);
                clone.name = source.name + "_" + i;
                clone.transform.SetPositionAndRotation(transforms[i].position, transforms[i].rotation);
                clone.transform.localScale = source.transform.localScale;
                Undo.RegisterCreatedObjectUndo(clone, "Create array item");
                created.Add(clone);
            }
            Undo.CollapseUndoOperations(group);
            return created;
        }

        public static void Align(IReadOnlyList<Transform> items, int axis, bool useMaximum, bool useMinimum)
        {
            if (items == null || items.Count < 2) return;
            if (axis < 0 || axis > 2) throw new ArgumentOutOfRangeException(nameof(axis));
            var target = items[0].position[axis];
            for (int i = 1; i < items.Count; i++)
                target = useMaximum ? Mathf.Max(target, items[i].position[axis])
                    : useMinimum ? Mathf.Min(target, items[i].position[axis])
                    : target + items[i].position[axis];
            if (!useMaximum && !useMinimum) target /= items.Count;
            Undo.RecordObjects(ToObjects(items), "Align objects");
            for (int i = 0; i < items.Count; i++)
            {
                var position = items[i].position;
                position[axis] = target;
                items[i].position = position;
            }
        }

        public static void Distribute(IReadOnlyList<Transform> items, int axis)
        {
            if (items == null || items.Count < 3) return;
            var sorted = new List<Transform>(items);
            sorted.Sort((left, right) => left.position[axis].CompareTo(right.position[axis]));
            float minimum = sorted[0].position[axis];
            float maximum = sorted[sorted.Count - 1].position[axis];
            Undo.RecordObjects(ToObjects(sorted), "Distribute objects");
            for (int i = 1; i < sorted.Count - 1; i++)
            {
                var position = sorted[i].position;
                position[axis] = Mathf.Lerp(minimum, maximum, i / (float)(sorted.Count - 1));
                sorted[i].position = position;
            }
        }

        public static GameObject Mirror(GameObject source, int axis, float worldPlane, bool copy)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (axis < 0 || axis > 2) throw new ArgumentOutOfRangeException(nameof(axis));
            var target = copy ? UnityEngine.Object.Instantiate(source, source.transform.parent) : source;
            if (copy)
            {
                target.name = source.name + "_Mirror";
                Undo.RegisterCreatedObjectUndo(target, "Mirror object");
            }
            else Undo.RecordObject(target.transform, "Mirror object");
            var position = target.transform.position;
            position[axis] = 2f * worldPlane - position[axis];
            target.transform.position = position;
            var scale = target.transform.localScale;
            scale[axis] = -scale[axis];
            target.transform.localScale = scale;
            return target;
        }

        public static void SetParentPreserveWorld(Transform child, Transform parent)
        {
            if (child == null) throw new ArgumentNullException(nameof(child));
            Undo.SetTransformParent(child, parent, "Reparent object");
        }

        public static bool PlaceOnSurface(
            Transform item,
            Ray ray,
            LayerMask layerMask,
            float offset,
            bool alignUp,
            out RaycastHit hit)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (!Physics.Raycast(ray, out hit, float.PositiveInfinity, layerMask, QueryTriggerInteraction.Ignore)) return false;
            Undo.RecordObject(item, "Place object on surface");
            item.position = hit.point + hit.normal * offset;
            if (alignUp) item.rotation = Quaternion.FromToRotation(item.up, hit.normal) * item.rotation;
            return true;
        }

        public static GameObject MergeStaticMeshes(
            IReadOnlyList<GameObject> roots,
            string outputFolder,
            bool compactMaterials = false,
            string resultName = null,
            bool removeSources = false)
        {
            if (roots == null || roots.Count == 0) throw new ArgumentException("Select at least one object to merge.", nameof(roots));
            var filters = new List<MeshFilter>();
            var filterIds = new HashSet<int>();
            GameObject primaryRoot = null;
            for (int rootIndex = 0; rootIndex < roots.Count; rootIndex++)
            {
                if (roots[rootIndex] == null) continue;
                if (primaryRoot == null) primaryRoot = roots[rootIndex];
                if (roots[rootIndex].GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
                    throw new InvalidOperationException("SKINNED_MERGE_REQUIRES_SHARED_SKELETON: use Smart Body Split/recombine for rigged meshes.");
                var rootFilters = roots[rootIndex].GetComponentsInChildren<MeshFilter>(true);
                for (int filterIndex = 0; filterIndex < rootFilters.Length; filterIndex++)
                    if (rootFilters[filterIndex] != null && filterIds.Add(rootFilters[filterIndex].GetMcpInstanceId()))
                        filters.Add(rootFilters[filterIndex]);
            }
            filters.RemoveAll(filter => filter == null || filter.sharedMesh == null || filter.GetComponent<MeshRenderer>() == null);
            if (filters.Count == 0) throw new InvalidOperationException("No static MeshFilter/MeshRenderer pairs were found.");

            var rootName = string.IsNullOrWhiteSpace(resultName) ? "MergedMesh" : resultName.Trim();
            var root = new GameObject(rootName);
            root.transform.SetPositionAndRotation(primaryRoot.transform.position, primaryRoot.transform.rotation);
            root.transform.localScale = Vector3.one;
            var rootWorldToLocal = root.transform.worldToLocalMatrix;
            var combines = new List<CombineInstance>();
            var materials = new List<Material>();
            var compactGroups = new Dictionary<int, List<CombineInstance>>();
            var compactOrder = new List<int>();
            var compactGroupMaterials = new Dictionary<int, Material>();
            for (int filterIndex = 0; filterIndex < filters.Count; filterIndex++)
            {
                var renderer = filters[filterIndex].GetComponent<MeshRenderer>();
                for (int subMesh = 0; subMesh < filters[filterIndex].sharedMesh.subMeshCount; subMesh++)
                {
                    Material material = subMesh < renderer.sharedMaterials.Length ? renderer.sharedMaterials[subMesh] : null;
                    var combine = new CombineInstance
                    {
                        mesh = filters[filterIndex].sharedMesh,
                        subMeshIndex = subMesh,
                        transform = rootWorldToLocal * filters[filterIndex].transform.localToWorldMatrix,
                    };
                    if (compactMaterials)
                    {
                        int materialKey = material != null ? material.GetMcpInstanceId() : int.MinValue;
                        if (!compactGroups.TryGetValue(materialKey, out var group))
                        {
                            group = new List<CombineInstance>();
                            compactGroups.Add(materialKey, group);
                            compactOrder.Add(materialKey);
                            compactGroupMaterials.Add(materialKey, material);
                        }
                        group.Add(combine);
                    }
                    else
                    {
                        materials.Add(material);
                        combines.Add(combine);
                    }
                }
            }

            var mesh = new Mesh { name = rootName, indexFormat = IndexFormat.UInt32 };
            var temporaryMeshes = new List<Mesh>();
            try
            {
                if (compactMaterials)
                {
                    for (int i = 0; i < compactOrder.Count; i++)
                    {
                        int key = compactOrder[i];
                        var materialMesh = new Mesh
                        {
                            name = $"MergedMaterial_{i}",
                            indexFormat = IndexFormat.UInt32,
                        };
                        materialMesh.CombineMeshes(compactGroups[key].ToArray(), true, true, false);
                        temporaryMeshes.Add(materialMesh);
                        materials.Add(compactGroupMaterials[key]);
                        combines.Add(new CombineInstance
                        {
                            mesh = materialMesh,
                            subMeshIndex = 0,
                            transform = Matrix4x4.identity,
                        });
                    }
                }
                mesh.CombineMeshes(combines.ToArray(), false, true, false);
            }
            finally
            {
                for (int i = 0; i < temporaryMeshes.Count; i++)
                    UnityEngine.Object.DestroyImmediate(temporaryMeshes[i]);
            }
            mesh.RecalculateBounds();
            var folder = AssetModelerAssetPaths.ValidateAndEnsureFolder(outputFolder);
            var meshPath = AssetModelerAssetPaths.UniqueAssetPath(folder, mesh.name, ".asset");
            AssetDatabase.CreateAsset(mesh, meshPath);
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            root.AddComponent<MeshRenderer>().sharedMaterials = materials.ToArray();
            root.layer = primaryRoot.layer;
            try { root.tag = primaryRoot.tag; }
            catch (UnityException) { }
            GameObjectUtility.SetStaticEditorFlags(root, GameObjectUtility.GetStaticEditorFlags(primaryRoot));
            Undo.RegisterCreatedObjectUndo(root, "Merge meshes");
            AssetDatabase.SaveAssets();
            Selection.activeGameObject = root;
            if (removeSources)
                for (int i = 0; i < roots.Count; i++)
                    if (roots[i] != null) Undo.DestroyObjectImmediate(roots[i]);
            return root;
        }

        public static void CenterPivot(GameObject target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            var filter = target.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) throw new InvalidOperationException("Center Pivot requires a MeshFilter on the selected object.");
            var assetPath = AssetDatabase.GetAssetPath(filter.sharedMesh);
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith(AssetModelerAssetPaths.DefaultRoot + "/", StringComparison.Ordinal))
                throw new InvalidOperationException("SOURCE_ASSET_PROTECTED: create an editable copy before changing the mesh pivot.");
            var mesh = filter.sharedMesh;
            var center = mesh.bounds.center;
            var descendants = target.GetComponentsInChildren<Transform>(true);
            var descendantPositions = new Vector3[descendants.Length];
            var descendantRotations = new Quaternion[descendants.Length];
            var undoObjects = new UnityEngine.Object[descendants.Length + 1];
            undoObjects[0] = mesh;
            for (int i = 0; i < descendants.Length; i++)
            {
                undoObjects[i + 1] = descendants[i];
                descendantPositions[i] = descendants[i].position;
                descendantRotations[i] = descendants[i].rotation;
            }
            Undo.RegisterCompleteObjectUndo(undoObjects, "Center mesh pivot");
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++) vertices[i] -= center;
            mesh.vertices = vertices;
            target.transform.position += target.transform.TransformVector(center);
            for (int i = 0; i < descendants.Length; i++)
            {
                if (descendants[i] == target.transform) continue;
                descendants[i].SetPositionAndRotation(descendantPositions[i], descendantRotations[i]);
            }
            mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);
        }

        internal static List<Pose> BuildArrayTransforms(GameObject source, SceneArraySettings settings)
        {
            var result = new List<Pose>();
            if (settings.Mode == SceneArrayMode.Grid)
            {
                var count = new Vector3Int(Mathf.Max(1, settings.GridCount.x), Mathf.Max(1, settings.GridCount.y), Mathf.Max(1, settings.GridCount.z));
                var spacing = settings.GridSpacing;
                if (settings.AttachedByBounds)
                {
                    var size = RendererBounds(source).size;
                    spacing = new Vector3(size.x + spacing.x, size.y + spacing.y, size.z + spacing.z);
                }
                for (int z = 0; z < count.z; z++)
                for (int y = 0; y < count.y; y++)
                for (int x = 0; x < count.x; x++)
                    result.Add(new Pose(source.transform.position + Vector3.Scale(new Vector3(x, y, z), spacing), source.transform.rotation));
                return result;
            }

            int total = Mathf.Max(1, settings.Count);
            if (settings.Mode == SceneArrayMode.Radial)
            {
                var axis = settings.RadialAxis.sqrMagnitude > 1e-8f ? settings.RadialAxis.normalized : Vector3.up;
                for (int i = 0; i < total; i++)
                {
                    float angle = total == 1 ? 0f : settings.RadialAngle * i / total;
                    var rotation = Quaternion.AngleAxis(angle, axis);
                    result.Add(new Pose(source.transform.position + rotation * settings.Step, rotation * source.transform.rotation));
                }
                return result;
            }

            var step = settings.Step;
            if (settings.AttachedByBounds)
            {
                var bounds = RendererBounds(source);
                var direction = step.sqrMagnitude > 1e-8f ? step.normalized : Vector3.right;
                var attachedDistance = Mathf.Abs(direction.x) * bounds.size.x
                    + Mathf.Abs(direction.y) * bounds.size.y
                    + Mathf.Abs(direction.z) * bounds.size.z;
                step = direction * attachedDistance;
            }
            for (int i = 0; i < total; i++) result.Add(new Pose(source.transform.position + step * i, source.transform.rotation));
            return result;
        }

        private static Bounds RendererBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return new Bounds(root.transform.position, Vector3.zero);
            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        private static UnityEngine.Object[] ToObjects(IReadOnlyList<Transform> transforms)
        {
            var result = new UnityEngine.Object[transforms.Count];
            for (int i = 0; i < result.Length; i++) result[i] = transforms[i];
            return result;
        }
    }
}
