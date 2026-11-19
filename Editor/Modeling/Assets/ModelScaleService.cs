using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Assets
{
    public enum ModelScaleMode { BakeCurrentScale, TargetDimension }
    public enum ModelScaleAxis { X, Y, Z }

    [Serializable]
    public sealed class ModelScaleSettings
    {
        public ModelScaleMode Mode = ModelScaleMode.BakeCurrentScale;
        public ModelScaleAxis Axis = ModelScaleAxis.Y;
        public float TargetDimension = 1.8f;
        public string OutputFolder = "Assets/AssetModeler/Normalized";
    }

    public sealed class ModelScalePreview
    {
        public string SourceHash { get; internal set; }
        public string PreviewHash { get; internal set; }
        public Bounds BoundsBefore { get; internal set; }
        public Bounds BoundsAfter { get; internal set; }
        public float ScaleFactor { get; internal set; }
        public float VisualScaleFactor { get; internal set; }
        public Vector3 SourceRootScale { get; internal set; }
        public int RendererCount { get; internal set; }
        public int SkinnedRendererCount { get; internal set; }
        public IReadOnlyList<string> Warnings { get; internal set; }
    }

    public sealed class ModelScaleResult
    {
        public GameObject Root { get; internal set; }
        public ModelScalePreview Preview { get; internal set; }
        public IReadOnlyList<string> CreatedAssetPaths { get; internal set; }
    }

    /// <summary>
    /// Changes model units on an independent hierarchy and independent mutable
    /// assets. Dimensions are in root axes, including its local scale but not
    /// rotation, position or the scale inherited from its parent.
    /// </summary>
    public static class ModelScaleService
    {
        private const float Epsilon = 1e-6f;

        public static ModelScalePreview Preview(GameObject root, ModelScaleSettings settings)
        {
            settings = settings ?? new ModelScaleSettings();
            ValidateSettings(settings);
            ValidateHierarchy(root);
            var nativeBounds = MeasureGeometry(root);
            var rootScale = root.transform.localScale.x;
            var length = nativeBounds.size[(int)settings.Axis];
            if (settings.Mode == ModelScaleMode.TargetDimension && length <= Epsilon)
                throw Error("MODEL_DIMENSION_EMPTY", "The selected model axis has zero or negligible size.");
            float factor = settings.Mode == ModelScaleMode.BakeCurrentScale
                ? rootScale : settings.TargetDimension / length;
            if (!Finite(factor) || factor < Epsilon || factor > 1e6f)
                throw Error("MODEL_SCALE_OUT_OF_RANGE", "The resulting uniform factor must be in 0.000001..1000000.");
            var warnings = new List<string>
            {
                "Dimensions use mesh rest geometry in root axes; animated poses may exceed these bounds.",
                "Parent scale is preserved. Root localScale 1 does not imply world scale 1.",
                "Undo removes the scene copy; generated assets and prefab are retained so other references stay valid.",
            };
            if (root.GetComponentsInChildren<MonoBehaviour>(true).Length != 0)
                warnings.Add("Custom scripts and their settings are copied; gameplay distances and other custom dimensional fields are not rescaled.");
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            var result = new ModelScalePreview
            {
                SourceHash = SourceHash(root),
                BoundsBefore = Scaled(nativeBounds, rootScale),
                BoundsAfter = Scaled(nativeBounds, factor),
                ScaleFactor = factor,
                VisualScaleFactor = factor / rootScale,
                SourceRootScale = root.transform.localScale,
                RendererCount = renderers.Length,
                SkinnedRendererCount = renderers.Count(value => value is SkinnedMeshRenderer),
                Warnings = warnings,
            };
            result.PreviewHash = Hash(result.SourceHash + "|" + (int)settings.Mode + "|" + (int)settings.Axis
                + "|" + settings.TargetDimension.ToString("R", CultureInfo.InvariantCulture)
                + "|" + NormalizedFolder(settings.OutputFolder) + "|" + factor.ToString("R", CultureInfo.InvariantCulture));
            return result;
        }

        public static ModelScaleResult Apply(GameObject root, ModelScaleSettings settings,
            string expectedSourceHash, string approvedPreviewHash)
        {
            settings = settings ?? new ModelScaleSettings();
            var preview = Preview(root, settings);
            if (string.IsNullOrEmpty(expectedSourceHash) || preview.SourceHash != expectedSourceHash)
                throw Error("STALE_MODEL_SOURCE", "The model, hierarchy, parent, controller, clip or collider changed after preview.");
            if (string.IsNullOrEmpty(approvedPreviewHash) || preview.PreviewHash != approvedPreviewHash)
                throw Error("STALE_MODEL_PREVIEW", "Generate a matching model-scale preview before applying.");

            GameObject copy = null;
            string folder = null;
            var assets = new List<Object>();
            var paths = new List<string>();
            var remap = new Dictionary<Object, Object>();
            try
            {
                // All geometry/rig work and validation happen before asset writes.
                copy = CloneInactive(root, remap);
                ScaleHierarchy(copy, preview.ScaleFactor, assets, remap);
                ModelScaleAnimation.Scale(root, copy, preview.ScaleFactor, assets, remap);
                RemapAssetReferences(copy, remap);
                var actual = MeasureGeometry(copy);
                if (!Approximately(actual.center, preview.BoundsAfter.center)
                    || !Approximately(actual.size, preview.BoundsAfter.size))
                    throw Error("MODEL_SCALE_VALIDATION_FAILED", "The copied model dimensions differ from the approved preview.");

                var parentFolder = AssetModelerAssetPaths.ValidateAndEnsureFolder(NormalizedFolder(settings.OutputFolder));
                folder = AssetDatabase.GenerateUniqueAssetPath(parentFolder + "/" + AssetModelerAssetPaths.SafeName(root.name) + "_Normalized");
                var folderParent = folder.Substring(0, folder.LastIndexOf('/'));
                if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(folderParent, Path.GetFileName(folder))))
                    throw Error("MODEL_SCALE_SAVE_FAILED", "Could not create the output folder.");
                foreach (var asset in assets)
                {
                    var extension = asset is AnimationClip ? ".anim" : asset is AnimatorOverrideController ? ".overrideController" : ".asset";
                    var path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + AssetModelerAssetPaths.SafeName(asset.name) + extension);
                    AssetDatabase.CreateAsset(asset, path);
                    paths.Add(path);
                }
                copy.name = root.name + "_Normalized";
                copy.transform.SetParent(root.transform.parent, false);
                copy.transform.localPosition = root.transform.localPosition;
                copy.transform.localRotation = root.transform.localRotation;
                copy.transform.localScale = Vector3.one;
                copy.hideFlags = HideFlags.None;
                copy.SetActive(root.activeSelf);
                var activatedBounds = MeasureGeometry(copy);
                if (copy.transform.localScale != Vector3.one
                    || !Approximately(activatedBounds.center, preview.BoundsAfter.center)
                    || !Approximately(activatedBounds.size, preview.BoundsAfter.size))
                    throw Error("MODEL_ACTIVATION_CHANGED_GEOMETRY", "An Editor callback changed the normalized copy when it was activated; generated assets were rolled back.");
                var prefabPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + AssetModelerAssetPaths.SafeName(copy.name) + ".prefab");
                if (PrefabUtility.SaveAsPrefabAsset(copy, prefabPath) == null)
                    throw Error("MODEL_SCALE_SAVE_FAILED", "Could not save the normalized prefab.");
                paths.Add(prefabPath);
                foreach (var asset in assets) EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();
                Undo.IncrementCurrentGroup();
                int group = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Normalize model scale (copy)");
                Undo.RegisterCreatedObjectUndo(copy, "Create normalized model");
                Undo.CollapseUndoOperations(group);
                return new ModelScaleResult { Root = copy, Preview = preview, CreatedAssetPaths = paths };
            }
            catch
            {
                if (copy != null) Object.DestroyImmediate(copy);
                if (!string.IsNullOrEmpty(folder)) AssetDatabase.DeleteAsset(folder);
                foreach (var asset in assets)
                    if (asset != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(asset))) Object.DestroyImmediate(asset);
                throw;
            }
        }

        private static void ValidateSettings(ModelScaleSettings settings)
        {
            if (!Enum.IsDefined(typeof(ModelScaleMode), settings.Mode) || !Enum.IsDefined(typeof(ModelScaleAxis), settings.Axis))
                throw Error("MODEL_SCALE_SETTINGS_INVALID", "Unknown scale mode or axis.");
            if (!Finite(settings.TargetDimension) || settings.TargetDimension <= Epsilon)
                throw Error("MODEL_DIMENSION_INVALID", "Target dimension must be finite and greater than zero.");
            var folder = NormalizedFolder(settings.OutputFolder);
            if (!folder.StartsWith("Assets/", StringComparison.Ordinal)
                || folder.Split('/').Any(part => string.IsNullOrEmpty(part) || part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw Error("MODEL_OUTPUT_INVALID", "Output must be a valid folder beneath Assets/.");
        }

        private static void ValidateHierarchy(GameObject root)
        {
            if (root == null) throw Error("MODEL_TARGET_REQUIRED", "Select a complete model root.");
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw Error("MODEL_EDIT_MODE_REQUIRED", "Normalize a model in stable Edit Mode.");
            var scale = root.transform.localScale;
            if (!UniformPositive(scale))
                throw Error("MODEL_ROOT_SCALE_UNSUPPORTED", "Root scale must be positive and uniform; nonuniform or mirrored roots require a separate conversion.");
            var transforms = root.GetComponentsInChildren<Transform>(true);
            foreach (var transform in transforms)
            {
                if (!Finite(transform.localPosition) || !Finite(transform.localScale)
                    || transform.localScale.x <= Epsilon || transform.localScale.y <= Epsilon || transform.localScale.z <= Epsilon)
                    throw Error("MODEL_TRANSFORM_UNSUPPORTED", "All descendant transforms must have finite positions and positive nonzero scales.");
                if (transform.GetComponents<Component>().Any(component => component == null))
                    throw Error("MODEL_MISSING_SCRIPT", "Remove or repair missing scripts before creating a normalized copy.");
            }
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.sharedMaterials.Any(material => material != null && !EditorUtility.IsPersistent(material)))
                    throw Error("MODEL_TRANSIENT_MATERIAL", "Save transient materials as assets before exporting a normalized prefab.");
                Mesh mesh;
                if (renderer is SkinnedMeshRenderer skin)
                {
                    mesh = skin.sharedMesh;
                    if (skin.rootBone != null && !Inside(skin.rootBone, root.transform))
                        throw Error("MODEL_EXTERNAL_BONE", "The selected root does not contain the renderer's root bone.");
                    if (skin.bones.Any(bone => bone == null || !Inside(bone, root.transform)))
                        throw Error("MODEL_EXTERNAL_BONE", "All skin bones must be present inside the selected hierarchy; optimized/hidden or external bones are unsupported.");
                    if (mesh != null && mesh.bindposeCount != skin.bones.Length)
                        throw Error("MODEL_BINDPOSE_MISMATCH", "Skin bone and bindpose counts must match.");
                }
                else if (renderer is MeshRenderer) mesh = RendererMeshUtility.GetSharedMesh(renderer);
                else throw Error("MODEL_RENDERER_UNSUPPORTED", "Only MeshRenderer and SkinnedMeshRenderer are supported: " + renderer.GetType().Name);
                ValidateMesh(mesh);
            }
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                if (filter.sharedMesh != null) ValidateMesh(filter.sharedMesh);
            foreach (var lod in root.GetComponentsInChildren<LODGroup>(true))
                if (lod.GetLODs().Any(level => level.renderers.Any(renderer => renderer != null && !Inside(renderer.transform, root.transform))))
                    throw Error("MODEL_EXTERNAL_LOD_RENDERER", "Every LOD renderer must be inside the selected model hierarchy.");
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider is MeshCollider meshCollider) ValidateMesh(meshCollider.sharedMesh);
                else if (!(collider is BoxCollider) && !(collider is SphereCollider) && !(collider is CapsuleCollider) && !(collider is CharacterController))
                    throw Error("MODEL_COLLIDER_UNSUPPORTED", "Collider cannot be rescaled safely: " + collider.GetType().Name);
            }
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                var typeName = component.GetType().FullName ?? "";
                if (component is Cloth || component is Joint || component is Rigidbody || component is Collider2D
                    || typeName.StartsWith("UnityEngine.Animations.", StringComparison.Ordinal)
                    || typeName.StartsWith("UnityEngine.Animations.Rigging.", StringComparison.Ordinal))
                    throw Error("MODEL_COMPONENT_UNSUPPORTED", "This component needs an explicit dimensional conversion policy: " + typeName);
            }
            ModelScaleAnimation.Validate(root);
        }

        private static void ValidateMesh(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable || mesh.vertexCount == 0)
                throw Error("MODEL_MESH_UNREADABLE", "Every renderer/collider must have a nonempty readable mesh.");
            foreach (var vertex in mesh.vertices)
                if (!Finite(vertex)) throw Error("MODEL_MESH_INVALID", "Mesh has a non-finite vertex.");
        }

        private static GameObject CloneInactive(GameObject source, Dictionary<Object, Object> remap)
        {
            var holder = new GameObject("ModelScale_Staging") { hideFlags = HideFlags.HideAndDontSave };
            holder.SetActive(false);
            GameObject clone = null;
            try
            {
                clone = Object.Instantiate(source, holder.transform, false);
                clone.name = source.name;
                clone.SetActive(false);
                clone.transform.SetParent(null, false);
                clone.hideFlags = HideFlags.HideAndDontSave;
                var originals = source.GetComponentsInChildren<Transform>(true);
                var copies = clone.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < originals.Length; i++)
                {
                    remap[originals[i]] = copies[i];
                    remap[originals[i].gameObject] = copies[i].gameObject;
                    var before = originals[i].GetComponents<Component>();
                    var after = copies[i].GetComponents<Component>();
                    for (int j = 0; j < before.Length; j++) remap[before[j]] = after[j];
                }
                return clone;
            }
            catch { if (clone != null) Object.DestroyImmediate(clone); throw; }
            finally { Object.DestroyImmediate(holder); }
        }

        private static void ScaleHierarchy(GameObject copy, float factor, List<Object> assets, Dictionary<Object, Object> remap)
        {
            foreach (var transform in copy.GetComponentsInChildren<Transform>(true))
                if (transform != copy.transform) transform.localPosition *= factor;
            copy.transform.localScale = Vector3.one;
            foreach (var filter in copy.GetComponentsInChildren<MeshFilter>(true))
                if (filter.sharedMesh != null) filter.sharedMesh = CopyMesh(filter.sharedMesh, factor, assets, remap);
            foreach (var skin in copy.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                skin.sharedMesh = CopyMesh(skin.sharedMesh, factor, assets, remap);
                skin.localBounds = Scaled(skin.localBounds, factor);
            }
            foreach (var collider in copy.GetComponentsInChildren<Collider>(true))
            {
                if (collider is MeshCollider mesh) { mesh.sharedMesh = CopyMesh(mesh.sharedMesh, factor, assets, remap); }
                else if (collider is BoxCollider box) { box.center *= factor; box.size *= factor; }
                else if (collider is SphereCollider sphere) { sphere.center *= factor; sphere.radius *= factor; }
                else if (collider is CapsuleCollider capsule) { capsule.center *= factor; capsule.radius *= factor; capsule.height *= factor; }
                else if (collider is CharacterController controller)
                {
                    controller.center *= factor; controller.height *= factor; controller.radius *= factor;
                    controller.stepOffset *= factor; controller.skinWidth *= factor;
                    controller.minMoveDistance *= factor;
                }
            }
            foreach (var lod in copy.GetComponentsInChildren<LODGroup>(true))
            {
                lod.localReferencePoint *= factor;
                lod.size *= factor;
            }
        }

        private static Mesh CopyMesh(Mesh source, float factor, List<Object> assets, Dictionary<Object, Object> remap)
        {
            if (remap.TryGetValue(source, out var existing)) return (Mesh)existing;
            var mesh = Object.Instantiate(source);
            mesh.name = source.name + "_Normalized";
            assets.Add(mesh);
            remap[source] = mesh;
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++) vertices[i] *= factor;
            mesh.vertices = vertices;
            var scale = Matrix4x4.Scale(Vector3.one * factor);
            var inverse = Matrix4x4.Scale(Vector3.one / factor);
            var bindposes = mesh.bindposes;
            for (int i = 0; i < bindposes.Length; i++) bindposes[i] = scale * bindposes[i] * inverse;
            mesh.bindposes = bindposes;
            // Uniform positive scale preserves normal/tangent directions. Only
            // positional deltas carry model units; frame weights remain intact.
            mesh.ClearBlendShapes();
            var deltaVertices = new Vector3[source.vertexCount];
            var deltaNormals = new Vector3[source.vertexCount];
            var deltaTangents = new Vector3[source.vertexCount];
            for (int shape = 0; shape < source.blendShapeCount; shape++)
                for (int frame = 0; frame < source.GetBlendShapeFrameCount(shape); frame++)
                {
                    source.GetBlendShapeFrameVertices(shape, frame, deltaVertices, deltaNormals, deltaTangents);
                    for (int i = 0; i < deltaVertices.Length; i++) deltaVertices[i] *= factor;
                    mesh.AddBlendShapeFrame(source.GetBlendShapeName(shape), source.GetBlendShapeFrameWeight(shape, frame), deltaVertices, deltaNormals, deltaTangents);
                }
            for (int i = 0; i < source.subMeshCount; i++)
            {
                var descriptor = source.GetSubMesh(i);
                descriptor.bounds = Scaled(descriptor.bounds, factor);
                mesh.SetSubMesh(i, descriptor, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            }
            mesh.bounds = Scaled(source.bounds, factor);
            return mesh;
        }

        internal static void RemapAssetReferences(GameObject copy, IReadOnlyDictionary<Object, Object> remap)
        {
            foreach (var component in copy.GetComponentsInChildren<Component>(true))
            {
                var serialized = new SerializedObject(component);
                var property = serialized.GetIterator();
                bool changed = false;
                while (property.Next(true))
                    if (property.propertyType == SerializedPropertyType.ObjectReference
                        && property.objectReferenceValue != null && remap.TryGetValue(property.objectReferenceValue, out var replacement))
                    {
                        property.objectReferenceValue = replacement;
                        changed = true;
                    }
                if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static Bounds MeasureGeometry(GameObject root)
        {
            bool hasVertex = false;
            var result = new Bounds();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = RendererMeshUtility.GetSharedMesh(renderer);
                if (mesh == null) continue;
                var matrix = root.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                foreach (var vertex in mesh.vertices)
                {
                    var point = matrix.MultiplyPoint3x4(vertex);
                    if (!Finite(point)) throw Error("MODEL_BOUNDS_INVALID", "Model dimensions overflowed.");
                    if (!hasVertex) { result = new Bounds(point, Vector3.zero); hasVertex = true; }
                    else result.Encapsulate(point);
                }
            }
            if (!hasVertex) throw Error("MODEL_RENDERER_REQUIRED", "Select a hierarchy containing readable mesh renderers.");
            return result;
        }

        private static string SourceHash(GameObject root)
        {
            var data = new StringBuilder(ToolTargetRef.Capture(root).ToJson().ToString());
            for (var parent = root.transform.parent; parent != null; parent = parent.parent)
                data.Append('|').Append(parent.GetInstanceID()).Append(EditorJsonUtility.ToJson(parent));
            var hashedAssets = new HashSet<Object>();
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                data.Append('|').Append(component.GetType().AssemblyQualifiedName).Append(EditorJsonUtility.ToJson(component));
                if (component is Transform) data.Append(EditorJsonUtility.ToJson(component.gameObject));
                var serialized = new SerializedObject(component);
                var property = serialized.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var asset = property.objectReferenceValue;
                    if (asset == null || !hashedAssets.Add(asset)) continue;
                    if (asset is Mesh mesh) data.Append(MeshFingerprint.Compute(mesh));
                    else if (asset is AnimationClip || asset is RuntimeAnimatorController || asset is Avatar)
                    {
                        data.Append(EditorJsonUtility.ToJson(asset));
                        var path = AssetDatabase.GetAssetPath(asset);
                        if (!string.IsNullOrEmpty(path))
                        {
                            data.Append(AssetDatabase.GetAssetDependencyHash(path));
                            if (asset is Avatar && AssetImporter.GetAtPath(path) is ModelImporter importer)
                                data.Append(EditorJsonUtility.ToJson(importer));
                            foreach (var subAsset in AssetDatabase.LoadAllAssetsAtPath(path))
                                if (subAsset != null) data.Append(EditorJsonUtility.ToJson(subAsset));
                        }
                    }
                }
            }
            foreach (var clip in ModelScaleAnimation.Clips(root)) data.Append(EditorJsonUtility.ToJson(clip));
            return Hash(data.ToString());
        }

        internal static bool Inside(Transform child, Transform root) => child == root || child.IsChildOf(root);
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        internal static bool UniformPositive(Vector3 value) => Finite(value) && value.x > Epsilon
            && Mathf.Abs(value.x - value.y) <= 1e-5f * Mathf.Max(Mathf.Abs(value.x), Mathf.Abs(value.y))
            && Mathf.Abs(value.x - value.z) <= 1e-5f * Mathf.Max(Mathf.Abs(value.x), Mathf.Abs(value.z));
        internal static InvalidOperationException Error(string code, string message) => new InvalidOperationException(code + ": " + message);
        private static Bounds Scaled(Bounds value, float factor) => new Bounds(value.center * factor, value.size * factor);
        private static bool Approximately(Vector3 a, Vector3 b) => (a - b).magnitude <= 1e-4f * Mathf.Max(1f, a.magnitude, b.magnitude);
        private static string NormalizedFolder(string folder) => (string.IsNullOrWhiteSpace(folder) ? "Assets/AssetModeler/Normalized" : folder).Replace('\\', '/').TrimEnd('/');
        private static string Hash(string value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
    }
}
