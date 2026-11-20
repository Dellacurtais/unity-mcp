using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Modeling;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Rigging
{
    internal static class RigToolSchemas
    {
        public static JObject Target(bool clips = false)
        {
            var properties = new JObject
            {
                ["target"] = new JObject { ["type"] = "object" },
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["rendererIndex"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["outputFolder"] = new JObject { ["type"] = "string" },
                ["worldTolerance"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
            };
            if (clips)
            {
                properties["clipAssetPaths"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "string" },
                    ["minItems"] = 1,
                };
                properties["sampleCount"] = new JObject { ["type"] = "integer", ["minimum"] = 2, ["maximum"] = 128, ["default"] = 12 };
                properties["splitPartsRoot"] = new JObject { ["type"] = "object" };
                properties["splitManifestAssetPath"] = new JObject { ["type"] = "string" };
                properties["splitTolerance"] = new JObject { ["type"] = "number", ["minimum"] = 0 };
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = false,
            };
        }

        public static ToolDescriptor Read(string name) => new ToolDescriptor
        {
            Name = name,
            Category = "rigging",
            Capabilities = McpCapability.Read,
            Risk = McpRiskLevel.Low,
            ConfirmationPolicy = McpConfirmationPolicy.None,
            EditorMode = McpEditorMode.EditModeOnly,
            IsIdempotent = true,
            ExecutionRequirements = McpExecutionRequirement.MainThread,
            HeadlessSafe = true,
        };

        public static ToolDescriptor Preview(string name) => new ToolDescriptor
        {
            Name = name, Category = "rigging", Capabilities = McpCapability.Read, Risk = McpRiskLevel.Low,
            ConfirmationPolicy = McpConfirmationPolicy.None, EditorMode = McpEditorMode.EditModeOnly, IsIdempotent = true,
            ExecutionRequirements = McpExecutionRequirement.MainThread | McpExecutionRequirement.SceneView | McpExecutionRequirement.EditorUpdates,
            HeadlessSafe = false,
        };

        public static ToolDescriptor Write(string name) => new ToolDescriptor
        {
            Name = name, Category = "rigging", Capabilities = McpCapability.AssetWrite | McpCapability.SceneWrite,
            Risk = McpRiskLevel.High, ConfirmationPolicy = McpConfirmationPolicy.Required,
            EditorMode = McpEditorMode.EditModeOnly, IsIdempotent = false,
            ExecutionRequirements = McpExecutionRequirement.MainThread, HeadlessSafe = true,
        };

        public static GameObject ResolveNested(JObject arguments, string property, out string error)
        {
            error = null; var reference = ToolTargetRef.FromJson(arguments?[property]); var value = ToolTargetResolver.Resolve(reference, out error);
            return value as GameObject ?? (value as Component)?.gameObject;
        }
    }

    public sealed class RigInspectTool : IMcpTool
    {
        public string Name => "rig_inspect";
        public string Description => "Inspects a Humanoid or Generic skinned character, weights, bones, bind poses and avatar without modifying assets.";
        public JObject InputSchema => RigToolSchemas.Target();
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => RigToolSchemas.Read(Name);
        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error);
            if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            try
            {
                var report = RigInspectorService.Inspect(target);
                return ToolResult.Ok(report.IsValid ? "Rig inspection passed." : "Rig inspection found errors.", report.ToJson());
            }
            catch (Exception exception) { return ToolResult.Error("RIG_INSPECTION_FAILED", exception.Message); }
        }
    }

    public sealed class RigValidateClipsTool : IMcpTool
    {
        public string Name => "rig_validate_clips";
        public string Description => "Samples standalone AnimationClips against a rig and validates baked vertices and bounds; always restores the original pose.";
        public JObject InputSchema => RigToolSchemas.Target(true);
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => RigToolSchemas.Read(Name);
        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error);
            if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            var paths = arguments?["clipAssetPaths"] as JArray;
            if (paths == null || paths.Count == 0) return ToolResult.Error("CLIPS_REQUIRED", "clipAssetPaths must contain at least one .anim or FBX clip path.");
            var clips = new List<AnimationClip>();
            for (int i = 0; i < paths.Count; i++)
            {
                string path = (string)paths[i];
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null) return ToolResult.Error("CLIP_NOT_FOUND", $"AnimationClip not found at '{path}'.");
                clips.Add(clip);
            }
            try
            {
                var splitRoot = RigToolSchemas.ResolveNested(arguments, "splitPartsRoot", out _);
                var manifestPath = (string)arguments?["splitManifestAssetPath"];
                var manifest = string.IsNullOrWhiteSpace(manifestPath) ? null : AssetDatabase.LoadAssetAtPath<Maicon.SceneAnalyzer.Editor.Modeling.SmartSplit.SmartBodySplitManifest>(manifestPath);
                var report = RigClipValidationService.Validate(target, clips, (int?)arguments?["sampleCount"] ?? 12, splitRoot, manifest, (float?)arguments?["splitTolerance"] ?? 1e-5f);
                return ToolResult.Ok(report.IsValid ? "All clips passed rig validation." : "Clip validation found errors.", report.ToJson());
            }
            catch (Exception exception) { return ToolResult.Error("RIG_CLIP_VALIDATION_FAILED", exception.Message); }
        }
    }

    public sealed class RigCreateEditableSkinTool : IMcpTool
    {
        public string Name => "rig_create_editable_skin";
        public string Description => "Duplicates an imported skinned mesh to a standalone .asset and assigns it to the renderer with Undo.";
        public JObject InputSchema => RigToolSchemas.Target();
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => RigToolSchemas.Write(Name);
        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error);
            if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            var context = RigTargetResolver.Resolve(target);
            int index = (int?)arguments?["rendererIndex"] ?? 0;
            if (index < 0 || index >= context.Renderers.Length) return ToolResult.Error("RENDERER_NOT_FOUND", $"rendererIndex {index} is out of range.");
            try
            {
                var mesh = RigAssetSafety.CreateEditableSkinCopy(context.Renderers[index], (string)arguments?["outputFolder"]);
                return ToolResult.Ok("Editable skin copy created.", new JObject { ["assetPath"] = AssetDatabase.GetAssetPath(mesh), ["mesh"] = mesh.name });
            }
            catch (Exception exception) { return ToolResult.Error("EDITABLE_SKIN_CREATE_FAILED", exception.Message); }
        }
    }

    public sealed class RigApplyWeightOperationTool : IMcpTool
    {
        public string Name => "rig_apply_weight_operation";
        public string Description => "Applies a deterministic radial/flood skin-weight operation to an editable .asset mesh; supports all BoneWeight1 influences and Undo.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["target"] = new JObject { ["type"] = "object" }, ["instanceId"] = new JObject { ["type"] = "integer" },
                ["rendererIndex"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["boneIndex"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray(Enum.GetNames(typeof(SkinWeightPaintMode))) },
                ["localCenter"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" }, ["minItems"] = 3, ["maxItems"] = 3 },
                ["radius"] = new JObject { ["type"] = "number", ["exclusiveMinimum"] = 0 }, ["strength"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 },
                ["falloff"] = new JObject { ["type"] = "number", ["exclusiveMinimum"] = 0 }, ["maximumInfluences"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 32 },
                ["pruneThreshold"] = new JObject { ["type"] = "number", ["minimum"] = 0 }, ["mirrorX"] = new JObject { ["type"] = "boolean" },
                ["xRay"] = new JObject { ["type"] = "boolean" },
                ["lockedBoneIndices"] = IntegerArraySchema(), ["lockedVertexIndices"] = IntegerArraySchema(), ["includedVertexIndices"] = IntegerArraySchema(),
            },
            ["required"] = new JArray("boneIndex", "mode"), ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => RigToolSchemas.Write(Name);
        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error);
            if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            var context = RigTargetResolver.Resolve(target);
            int rendererIndex = (int?)arguments?["rendererIndex"] ?? 0;
            if (rendererIndex < 0 || rendererIndex >= context.Renderers.Length) return ToolResult.Error("RENDERER_NOT_FOUND", $"rendererIndex {rendererIndex} is out of range.");
            if (!Enum.TryParse((string)arguments?["mode"], true, out SkinWeightPaintMode mode)) return ToolResult.Error("WEIGHT_MODE_INVALID", "Unknown mode.");
            var center = ReadVector(arguments?["localCenter"] as JArray);
            var settings = new SkinWeightBrushSettings
            {
                Mode = mode, Radius = (float?)arguments?["radius"] ?? 0.08f, Strength = (float?)arguments?["strength"] ?? 0.25f,
                Falloff = (float?)arguments?["falloff"] ?? 1f, MaximumInfluences = (int?)arguments?["maximumInfluences"] ?? 8,
                PruneThreshold = (float?)arguments?["pruneThreshold"] ?? 0.001f, MirrorX = (bool?)arguments?["mirrorX"] ?? false,
                XRay = (bool?)arguments?["xRay"] ?? false,
            };
            try
            {
                var result = SkinWeightEditorService.ApplyRadialBrush(context.Renderers[rendererIndex], (int)arguments["boneIndex"], center, settings,
                    ReadSet(arguments?["lockedBoneIndices"] as JArray), ReadSet(arguments?["lockedVertexIndices"] as JArray), null, ReadSet(arguments?["includedVertexIndices"] as JArray));
                return ToolResult.Ok("Skin weights updated.", new JObject
                {
                    ["affectedVertices"] = result.AffectedVertices, ["prunedInfluences"] = result.PrunedInfluences,
                    ["maximumInfluences"] = result.MaximumInfluences, ["meshAssetPath"] = result.MeshAssetPath,
                    ["beforeHash"] = result.BeforeHash, ["afterHash"] = result.AfterHash, ["undoRegistered"] = result.UndoRegistered,
                });
            }
            catch (Exception exception) { return ToolResult.Error("WEIGHT_OPERATION_FAILED", exception.Message); }
        }

        private static Vector3 ReadVector(JArray array) => array != null && array.Count == 3
            ? new Vector3((float)array[0], (float)array[1], (float)array[2]) : Vector3.zero;
        private static JObject IntegerArraySchema() => new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer", ["minimum"] = 0 }, ["uniqueItems"] = true };
        private static HashSet<int> ReadSet(JArray array) => array == null ? null : new HashSet<int>(array.Select(value => (int)value));
    }

    public sealed class RigRebuildBindPosesTool : IMcpTool
    {
        public string Name => "rig_rebuild_bindposes";
        public string Description => "Rebuilds bind poses on an editable skinned mesh from its current skeleton matrices.";
        public JObject InputSchema => RigToolSchemas.Target(); public bool IsWrite => true; public ToolDescriptor Descriptor => RigToolSchemas.Write(Name);
        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error); if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            var context = RigTargetResolver.Resolve(target); int index = (int?)arguments?["rendererIndex"] ?? 0;
            if (index < 0 || index >= context.Renderers.Length) return ToolResult.Error("RENDERER_NOT_FOUND", "rendererIndex is out of range.");
            try { string before = context.BindPoseHash; float maximumDelta = BindPoseService.Rebuild(context.Renderers[index], (float?)arguments?["worldTolerance"] ?? 1e-4f); var refreshed = RigTargetResolver.Resolve(context.Root); return ToolResult.Ok("Bind poses rebuilt.", new JObject { ["bindPoseCount"] = context.Renderers[index].sharedMesh.bindposeCount, ["maximumWorldDelta"] = maximumDelta, ["beforeHash"] = before, ["afterHash"] = refreshed.BindPoseHash }); }
            catch (Exception exception) { return ToolResult.Error("BINDPOSE_REBUILD_FAILED", exception.Message); }
        }
    }

    public sealed class RigTransferWeightsTool : IMcpTool
    {
        public string Name => "rig_transfer_weights";
        public string Description => "Transfers all BoneWeight1 influences from a source renderer to an editable destination by index or closest barycentric surface.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["target"] = new JObject { ["type"] = "object" }, ["instanceId"] = new JObject { ["type"] = "integer" },
                ["sourceTarget"] = new JObject { ["type"] = "object" }, ["rendererIndex"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["sourceRendererIndex"] = new JObject { ["type"] = "integer", ["minimum"] = 0 }, ["maximumDistance"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                ["maximumInfluences"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 32 }, ["pruneThreshold"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                ["includedVertexIndices"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer", ["minimum"] = 0 }, ["uniqueItems"] = true }, ["mirrorRegionX"] = new JObject { ["type"] = "boolean" },
            }, ["required"] = new JArray("sourceTarget"), ["additionalProperties"] = false,
        };
        public bool IsWrite => true; public ToolDescriptor Descriptor => RigToolSchemas.Write(Name);
        public ToolResult Execute(JObject arguments)
        {
            var destinationTarget = ToolTargetResolver.ResolveGameObject(arguments, out var error); if (destinationTarget == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            var sourceRef = ToolTargetRef.FromJson(arguments?["sourceTarget"]); var sourceObject = ToolTargetResolver.Resolve(sourceRef, out error);
            var sourceTarget = sourceObject as GameObject ?? (sourceObject as Component)?.gameObject;
            if (sourceTarget == null) return ToolResult.Error("SOURCE_TARGET_NOT_FOUND", error ?? "sourceTarget does not resolve to a GameObject.");
            var sourceContext = RigTargetResolver.Resolve(sourceTarget); var destinationContext = RigTargetResolver.Resolve(destinationTarget);
            int sourceIndex = (int?)arguments?["sourceRendererIndex"] ?? 0, destinationIndex = (int?)arguments?["rendererIndex"] ?? 0;
            if (sourceIndex < 0 || sourceIndex >= sourceContext.Renderers.Length || destinationIndex < 0 || destinationIndex >= destinationContext.Renderers.Length)
                return ToolResult.Error("RENDERER_NOT_FOUND", "A renderer index is out of range.");
            try
            {
                var included = arguments?["includedVertexIndices"] as JArray;
                var report = SkinWeightTransferService.Transfer(sourceContext.Renderers[sourceIndex], destinationContext.Renderers[destinationIndex], new WeightTransferOptions
                {
                    MaximumDistance = (float?)arguments?["maximumDistance"] ?? float.PositiveInfinity,
                    MaximumInfluences = (int?)arguments?["maximumInfluences"] ?? 8,
                    PruneThreshold = (float?)arguments?["pruneThreshold"] ?? 1e-6f,
                    IncludedVertices = included == null ? null : new HashSet<int>(included.Select(value => (int)value)), MirrorRegionX = (bool?)arguments?["mirrorRegionX"] ?? false,
                });
                return ToolResult.Ok("Weights transferred.", new JObject { ["valid"] = report.IsValid, ["vertexCount"] = report.VertexCount, ["exactMatches"] = report.ExactVertexMatches, ["surfaceMatches"] = report.SurfaceMatches, ["preservedVertices"] = report.PreservedVertexCount, ["maximumDistance"] = report.MaximumDistance, ["meanDistance"] = report.MeanDistance, ["beforeHash"] = report.BeforeHash, ["afterHash"] = report.AfterHash });
            }
            catch (Exception exception) { return ToolResult.Error("WEIGHT_TRANSFER_FAILED", exception.Message); }
        }
    }

    public sealed class RigEditSkeletonTool : IMcpTool
    {
        public string Name => "rig_edit_skeleton";
        public string Description => "Creates, renames, reparents or moves a skeleton bone by stable relative path with Undo.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["target"] = new JObject { ["type"] = "object" }, ["instanceId"] = new JObject { ["type"] = "integer" },
                ["action"] = new JObject { ["type"] = "string", ["enum"] = new JArray("create", "rename", "reparent", "move", "setRootBone", "captureBindPose", "restoreBindPose", "removeUnusedBones") },
                ["bonePath"] = new JObject { ["type"] = "string" }, ["parentPath"] = new JObject { ["type"] = "string" }, ["name"] = new JObject { ["type"] = "string" },
                ["localPosition"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" }, ["minItems"] = 3, ["maxItems"] = 3 },
                ["localEuler"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" }, ["minItems"] = 3, ["maxItems"] = 3 },
                ["rendererIndex"] = new JObject { ["type"] = "integer", ["minimum"] = 0 }, ["snapshotAssetPath"] = new JObject { ["type"] = "string" }, ["outputFolder"] = new JObject { ["type"] = "string" },
                ["proofClipAssetPaths"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
            }, ["required"] = new JArray("action"), ["additionalProperties"] = false,
        };
        public bool IsWrite => true; public ToolDescriptor Descriptor => RigToolSchemas.Write(Name);
        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error); if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            var context = RigTargetResolver.Resolve(target); string action = ((string)arguments?["action"] ?? string.Empty).ToLowerInvariant();
            var bone = Find(context.Root.transform, (string)arguments?["bonePath"]); var parent = Find(context.Root.transform, (string)arguments?["parentPath"]);
            int rendererIndex = (int?)arguments?["rendererIndex"] ?? 0;
            if (rendererIndex < 0 || rendererIndex >= context.Renderers.Length) return ToolResult.Error("RENDERER_NOT_FOUND", "rendererIndex is out of range.");
            var renderer = context.Renderers[rendererIndex];
            try
            {
                JObject extra = null;
                switch (action)
                {
                    case "create": bone = SkeletonEditorService.CreateBone(parent ?? context.Root.transform, (string)arguments?["name"], ReadVector(arguments?["localPosition"] as JArray)); break;
                    case "rename": if (bone == null) throw new InvalidOperationException("BONE_NOT_FOUND"); SkeletonEditorService.RenameBone(bone, (string)arguments?["name"]); break;
                    case "reparent": if (bone == null || parent == null) throw new InvalidOperationException("BONE_OR_PARENT_NOT_FOUND"); SkeletonEditorService.ReparentBone(bone, parent); break;
                    case "move": if (bone == null) throw new InvalidOperationException("BONE_NOT_FOUND"); SkeletonEditorService.MoveBone(bone, ReadVector(arguments?["localPosition"] as JArray), Quaternion.Euler(ReadVector(arguments?["localEuler"] as JArray))); break;
                    case "setrootbone": if (bone == null) throw new InvalidOperationException("BONE_NOT_FOUND"); SkeletonEditorService.SetRootBone(renderer, bone); break;
                    case "capturebindpose":
                        var captured = BindPoseService.Capture(renderer, context.Root, (string)arguments?["outputFolder"]); extra = new JObject { ["snapshotAssetPath"] = AssetDatabase.GetAssetPath(captured) }; break;
                    case "restorebindpose":
                        var snapshot = AssetDatabase.LoadAssetAtPath<RigBindPoseSnapshot>((string)arguments?["snapshotAssetPath"]); if (snapshot == null) throw new InvalidOperationException("BINDPOSE_SNAPSHOT_NOT_FOUND"); BindPoseService.Restore(renderer, context.Root, snapshot); break;
                    case "removeunusedbones":
                        var paths = arguments?["proofClipAssetPaths"] as JArray; var proof = paths == null ? Array.Empty<AnimationClip>() : paths.Select(value => AssetDatabase.LoadAssetAtPath<AnimationClip>((string)value)).Where(value => value != null).ToArray();
                        var prune = SkeletonEditorService.RemoveUnusedBones(renderer, context.Root, proof); extra = new JObject { ["removedBoneCount"] = prune.RemovedBoneCount, ["removedBonePaths"] = new JArray(prune.RemovedBonePaths) }; break;
                    default: return ToolResult.Error("SKELETON_ACTION_INVALID", "Unknown action.");
                }
                var payload = extra ?? new JObject(); payload["bonePath"] = RigTargetResolver.RelativePath(bone, context.Root.transform); var refreshed = RigTargetResolver.Resolve(context.Root); payload["hierarchyHash"] = refreshed.HierarchyHash; payload["stateHash"] = refreshed.StateHash;
                return ToolResult.Ok("Skeleton updated.", payload);
            }
            catch (Exception exception) { return ToolResult.Error("SKELETON_EDIT_FAILED", exception.Message); }
        }
        private static Transform Find(Transform root, string path) => string.IsNullOrEmpty(path) ? root : root.Find(path);
        private static Vector3 ReadVector(JArray array) => array != null && array.Count == 3 ? new Vector3((float)array[0], (float)array[1], (float)array[2]) : Vector3.zero;
    }

    public sealed class AnimationCapturePoseTool : IMcpTool
    {
        public string Name => "animation_capture_pose"; public string Description => "Captures the current transform and Humanoid muscle pose into a RigPoseLibrary asset.";
        public JObject InputSchema => new JObject { ["type"] = "object", ["properties"] = new JObject { ["target"] = new JObject { ["type"] = "object" }, ["instanceId"] = new JObject { ["type"] = "integer" }, ["libraryAssetPath"] = new JObject { ["type"] = "string" }, ["poseName"] = new JObject { ["type"] = "string" } }, ["required"] = new JArray("libraryAssetPath", "poseName"), ["additionalProperties"] = false };
        public bool IsWrite => true; public ToolDescriptor Descriptor => RigToolSchemas.Write(Name);
        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error); if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            var library = AssetDatabase.LoadAssetAtPath<RigPoseLibrary>((string)arguments?["libraryAssetPath"]); if (library == null) return ToolResult.Error("POSE_LIBRARY_NOT_FOUND", "libraryAssetPath does not resolve.");
            try { var pose = RigPoseLibraryService.Capture(target, (string)arguments?["poseName"]); RigPoseLibraryService.Add(library, pose); return ToolResult.Ok("Pose captured.", new JObject { ["poseId"] = pose.Id, ["transformCount"] = pose.Transforms.Count, ["muscleCount"] = pose.HumanMuscles?.Length ?? 0 }); }
            catch (Exception exception) { return ToolResult.Error("POSE_CAPTURE_FAILED", exception.Message); }
        }
    }

    public sealed class AnimationApplyPoseTool : IMcpTool
    {
        public string Name => "animation_apply_pose"; public string Description => "Applies a hash-compatible RigPoseLibrary pose to a character with Undo.";
        public JObject InputSchema => new JObject { ["type"] = "object", ["properties"] = new JObject { ["target"] = new JObject { ["type"] = "object" }, ["instanceId"] = new JObject { ["type"] = "integer" }, ["libraryAssetPath"] = new JObject { ["type"] = "string" }, ["poseId"] = new JObject { ["type"] = "string" } }, ["required"] = new JArray("libraryAssetPath", "poseId"), ["additionalProperties"] = false };
        public bool IsWrite => true; public ToolDescriptor Descriptor => RigToolSchemas.Write(Name);
        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error); if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            var library = AssetDatabase.LoadAssetAtPath<RigPoseLibrary>((string)arguments?["libraryAssetPath"]); var pose = library?.Find((string)arguments?["poseId"]);
            if (pose == null) return ToolResult.Error("POSE_NOT_FOUND", "Pose library or poseId was not found.");
            try { RigPoseLibraryService.Apply(target, pose); return ToolResult.Ok("Pose applied.", new JObject { ["poseId"] = pose.Id }); }
            catch (Exception exception) { return ToolResult.Error("POSE_APPLY_FAILED", exception.Message); }
        }
    }

    public sealed class AnimationTransformClipTool : IMcpTool
    {
        public string Name => "animation_transform_clip"; public string Description => "Creates a standalone .anim using duplicate, trim, time scale, reverse, mirror, loop closure, root-motion extract/remove, additive reference, sequential merge, or curve remapping while preserving curves and events.";
        public JObject InputSchema => new JObject { ["type"] = "object", ["properties"] = new JObject
        {
            ["clipAssetPath"] = new JObject { ["type"] = "string" }, ["operation"] = new JObject { ["type"] = "string", ["enum"] = new JArray(Enum.GetNames(typeof(AnimationClipTransformOperation))) },
            ["startTime"] = new JObject { ["type"] = "number", ["minimum"] = 0 }, ["endTime"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
            ["timeScale"] = new JObject { ["type"] = "number", ["exclusiveMinimum"] = 0 }, ["outputFolder"] = new JObject { ["type"] = "string" },
            ["referenceClipAssetPath"] = new JObject { ["type"] = "string" }, ["additionalClipAssetPaths"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
            ["sourcePathPrefix"] = new JObject { ["type"] = "string" }, ["destinationPathPrefix"] = new JObject { ["type"] = "string" },
        }, ["required"] = new JArray("clipAssetPath", "operation"), ["additionalProperties"] = false };
        public bool IsWrite => true; public ToolDescriptor Descriptor => RigToolSchemas.Write(Name);
        public ToolResult Execute(JObject arguments)
        {
            var source = AssetDatabase.LoadAssetAtPath<AnimationClip>((string)arguments?["clipAssetPath"]); if (source == null) return ToolResult.Error("CLIP_NOT_FOUND", "clipAssetPath does not resolve.");
            if (!Enum.TryParse((string)arguments?["operation"], true, out AnimationClipTransformOperation operation)) return ToolResult.Error("CLIP_OPERATION_INVALID", "Unknown operation.");
            try
            {
                var additional = arguments?["additionalClipAssetPaths"] as JArray;
                var output = AnimationClipStudioService.Transform(source, new AnimationClipTransformSettings
                {
                    Operation = operation, StartTime = (float?)arguments?["startTime"] ?? 0f, EndTime = (float?)arguments?["endTime"] ?? source.length, TimeScale = (float?)arguments?["timeScale"] ?? 1f,
                    OutputFolder = (string)arguments?["outputFolder"] ?? "Assets/AssetModeler/Rigging/Clips", ReferenceClip = AssetDatabase.LoadAssetAtPath<AnimationClip>((string)arguments?["referenceClipAssetPath"]),
                    AdditionalClips = additional == null ? new List<AnimationClip>() : additional.Select(value => AssetDatabase.LoadAssetAtPath<AnimationClip>((string)value)).Where(value => value != null).ToList(),
                    SourcePathPrefix = (string)arguments?["sourcePathPrefix"] ?? string.Empty, DestinationPathPrefix = (string)arguments?["destinationPathPrefix"] ?? string.Empty,
                });
                return ToolResult.Ok("Standalone clip created.", new JObject { ["assetPath"] = AssetDatabase.GetAssetPath(output), ["length"] = output.length, ["curveCount"] = AnimationUtility.GetCurveBindings(output).Length });
            }
            catch (Exception exception) { return ToolResult.Error("CLIP_TRANSFORM_FAILED", exception.Message); }
        }
    }

    public sealed class RigAnalyzeRetargetTool : IMcpTool
    {
        public string Name => "rig_analyze_retarget"; public string Description => "Analyzes Humanoid or Generic source/destination rigs and creates a reviewable hash-bound retarget profile.";
        public JObject InputSchema => RetargetSchema(false); public bool IsWrite => true; public ToolDescriptor Descriptor => RigToolSchemas.Write(Name);
        public ToolResult Execute(JObject arguments)
        {
            var source = RigToolSchemas.ResolveNested(arguments, "sourceTarget", out var error); if (source == null) return ToolResult.Error("SOURCE_TARGET_NOT_FOUND", error);
            var destination = RigToolSchemas.ResolveNested(arguments, "destinationTarget", out error); if (destination == null) return ToolResult.Error("DESTINATION_TARGET_NOT_FOUND", error);
            try
            {
                var report = RigRetargetAnalyzer.Analyze(source, destination, (string)arguments?["outputFolder"]);
                return ToolResult.Ok("Retarget profile created.", new JObject { ["profileAssetPath"] = AssetDatabase.GetAssetPath(report.Profile), ["sourceBoneCount"] = report.SourceBoneCount, ["mappedBoneCount"] = report.MappedBoneCount, ["unmappedBoneCount"] = report.UnmappedBoneCount, ["averageConfidence"] = report.AverageConfidence });
            }
            catch (Exception exception) { return ToolResult.Error("RETARGET_ANALYSIS_FAILED", exception.Message); }
        }
        internal static JObject RetargetSchema(bool clip) => new JObject { ["type"] = "object", ["properties"] = new JObject
        {
            ["sourceTarget"] = new JObject { ["type"] = "object" }, ["destinationTarget"] = new JObject { ["type"] = "object" },
            ["profileAssetPath"] = new JObject { ["type"] = "string" }, ["clipAssetPath"] = new JObject { ["type"] = "string" },
            ["additionalClipAssetPaths"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
            ["outputFolder"] = new JObject { ["type"] = "string" }, ["frameRate"] = new JObject { ["type"] = "number", ["minimum"] = 1, ["maximum"] = 120 },
        }, ["required"] = clip ? new JArray("sourceTarget", "destinationTarget", "profileAssetPath", "clipAssetPath") : new JArray("sourceTarget", "destinationTarget"), ["additionalProperties"] = false };
    }

    public sealed class RigApplyRetargetPoseTool : IMcpTool
    {
        public string Name => "rig_apply_retarget_pose"; public string Description => "Applies a reviewed, hierarchy-hash-compatible retarget profile to the current pose with Undo.";
        public JObject InputSchema { get { var schema = RigAnalyzeRetargetTool.RetargetSchema(false); schema["required"] = new JArray("sourceTarget", "destinationTarget", "profileAssetPath"); return schema; } } public bool IsWrite => true; public ToolDescriptor Descriptor => RigToolSchemas.Write(Name);
        public ToolResult Execute(JObject arguments)
        {
            var source = RigToolSchemas.ResolveNested(arguments, "sourceTarget", out var error); var destination = RigToolSchemas.ResolveNested(arguments, "destinationTarget", out error);
            if (source == null || destination == null) return ToolResult.Error("RETARGET_TARGET_NOT_FOUND", error);
            var profile = AssetDatabase.LoadAssetAtPath<RigRetargetProfile>((string)arguments?["profileAssetPath"]); if (profile == null) return ToolResult.Error("RETARGET_PROFILE_NOT_FOUND", "profileAssetPath does not resolve.");
            try { RigRetargetService.ApplyPose(source, destination, profile); var metrics = RigRetargetService.Measure(source, destination, profile); return ToolResult.Ok("Retarget pose applied.", new JObject { ["profile"] = profile.name, ["headPositionError"] = metrics.HeadPositionError, ["leftHandPositionError"] = metrics.LeftHandPositionError, ["rightHandPositionError"] = metrics.RightHandPositionError, ["leftFootPositionError"] = metrics.LeftFootPositionError, ["rightFootPositionError"] = metrics.RightFootPositionError, ["maximumRotationError"] = metrics.MaximumRotationError }); }
            catch (Exception exception) { return ToolResult.Error("RETARGET_POSE_FAILED", exception.Message); }
        }
    }

    public sealed class RigRetargetClipTool : IMcpTool
    {
        public string Name => "rig_retarget_clip"; public string Description => "Samples a source clip through a reviewed retarget profile and bakes a standalone destination .anim.";
        public JObject InputSchema => RigAnalyzeRetargetTool.RetargetSchema(true); public bool IsWrite => true; public ToolDescriptor Descriptor => RigToolSchemas.Write(Name);
        public ToolResult Execute(JObject arguments)
        {
            var source = RigToolSchemas.ResolveNested(arguments, "sourceTarget", out var error); var destination = RigToolSchemas.ResolveNested(arguments, "destinationTarget", out error);
            if (source == null || destination == null) return ToolResult.Error("RETARGET_TARGET_NOT_FOUND", error);
            var profile = AssetDatabase.LoadAssetAtPath<RigRetargetProfile>((string)arguments?["profileAssetPath"]); var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>((string)arguments?["clipAssetPath"]);
            if (profile == null || clip == null) return ToolResult.Error("RETARGET_ASSET_NOT_FOUND", "profileAssetPath or clipAssetPath does not resolve.");
            try
            {
                var additional = arguments?["additionalClipAssetPaths"] as JArray; var clips = new List<AnimationClip> { clip };
                if (additional != null) clips.AddRange(additional.Select(value => AssetDatabase.LoadAssetAtPath<AnimationClip>((string)value)).Where(value => value != null));
                var outputs = RigRetargetService.RetargetClips(source, destination, profile, clips, (float?)arguments?["frameRate"] ?? 30f, (string)arguments?["outputFolder"]);
                return ToolResult.Ok("Retarget clips baked.", new JObject { ["assetPaths"] = new JArray(outputs.Select(AssetDatabase.GetAssetPath)), ["clipCount"] = outputs.Length });
            }
            catch (Exception exception) { return ToolResult.Error("RETARGET_CLIP_FAILED", exception.Message); }
        }
    }

    public sealed class RigCreateIkTool : IMcpTool
    {
        public string Name => "rig_create_ik"; public string Description => "Creates a native Two Bone, Aim or FABRIK binding with Scene View target and hint handles.";
        public JObject InputSchema => IkSchema(false); public bool IsWrite => true; public ToolDescriptor Descriptor => RigToolSchemas.Write(Name);
        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error); if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            var context = RigTargetResolver.Resolve(target); if (!Enum.TryParse((string)arguments?["solver"], true, out RigIkSolverType solver)) solver = RigIkSolverType.TwoBone;
            var root = context.Root.transform.Find((string)arguments?["rootPath"]); var mid = context.Root.transform.Find((string)arguments?["midPath"]); var tip = context.Root.transform.Find((string)arguments?["tipPath"]);
            try
            {
                var binding = RigIkService.Create(context.Root, root, mid, tip, solver); string adapterMessage = AnimationRiggingAdapter.Status; Component constraint = null;
                var chainPaths = arguments?["chainPaths"] as JArray;
                if (chainPaths != null)
                {
                    binding.Chain.Clear();
                    foreach (var token in chainPaths)
                    {
                        var chainBone = context.Root.transform.Find((string)token); if (chainBone == null) throw new InvalidOperationException("IK_CHAIN_BONE_NOT_FOUND: " + (string)token); binding.Chain.Add(chainBone);
                    }
                    NativeRigIkSolver.CaptureRestPose(binding);
                }
                if (Enum.TryParse((string)arguments?["placementPreset"], true, out RigIkPlacementPreset preset)) RigIkService.ApplyPlacementPreset(binding, preset);
                if ((bool?)arguments?["useAnimationRigging"] == true && AnimationRiggingAdapter.IsAvailable)
                    AnimationRiggingAdapter.TryCreateConstraint(binding, out constraint, out adapterMessage);
                return ToolResult.Ok("IK binding created.", new JObject { ["bindingInstanceId"] = binding.gameObject.GetMcpInstanceId(), ["targetInstanceId"] = binding.Target.gameObject.GetMcpInstanceId(), ["animationRiggingAvailable"] = AnimationRiggingAdapter.IsAvailable, ["animationRiggingConstraintInstanceId"] = constraint != null ? constraint.gameObject.GetMcpInstanceId() : 0, ["animationRiggingMessage"] = adapterMessage });
            }
            catch (Exception exception) { return ToolResult.Error("IK_CREATE_FAILED", exception.Message); }
        }
        internal static JObject IkSchema(bool bake) => new JObject { ["type"] = "object", ["properties"] = new JObject
        {
            ["target"] = new JObject { ["type"] = "object" }, ["instanceId"] = new JObject { ["type"] = "integer" }, ["rootPath"] = new JObject { ["type"] = "string" }, ["midPath"] = new JObject { ["type"] = "string" }, ["tipPath"] = new JObject { ["type"] = "string" },
            ["solver"] = new JObject { ["type"] = "string", ["enum"] = new JArray(Enum.GetNames(typeof(RigIkSolverType))) }, ["targetPosition"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" }, ["minItems"] = 3, ["maxItems"] = 3 },
            ["chainPaths"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["minItems"] = 2 }, ["placementPreset"] = new JObject { ["type"] = "string", ["enum"] = new JArray(Enum.GetNames(typeof(RigIkPlacementPreset))) },
            ["useAnimationRigging"] = new JObject { ["type"] = "boolean" },
            ["clipAssetPath"] = new JObject { ["type"] = "string" }, ["frameRate"] = new JObject { ["type"] = "number", ["minimum"] = 1, ["maximum"] = 120 }, ["outputFolder"] = new JObject { ["type"] = "string" },
        }, ["required"] = bake ? new JArray("clipAssetPath") : new JArray("rootPath", "tipPath"), ["additionalProperties"] = false };
    }

    public sealed class RigPreviewIkTool : IMcpTool
    {
        public string Name => "rig_preview_ik"; public string Description => "Moves a native IK target and solves the current preview pose without creating assets.";
        public JObject InputSchema { get { var schema = RigCreateIkTool.IkSchema(false); schema["required"] = new JArray(); return schema; } } public bool IsWrite => true; public ToolDescriptor Descriptor => RigToolSchemas.Write(Name);
        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error); if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            var binding = target.GetComponent<RigIkBinding>() ?? target.GetComponentInChildren<RigIkBinding>(true); if (binding == null) return ToolResult.Error("IK_BINDING_NOT_FOUND", "Target has no RigIkBinding.");
            var position = arguments?["targetPosition"] as JArray;
            try { if (position != null && position.Count == 3) { Undo.RecordObject(binding.Target, "Move IK target"); binding.Target.position = new Vector3((float)position[0], (float)position[1], (float)position[2]); } NativeRigIkSolver.Solve(binding); return ToolResult.Ok("IK preview solved.", new JObject { ["tipError"] = Vector3.Distance(binding.Tip.position, binding.Target.position) }); }
            catch (Exception exception) { return ToolResult.Error("IK_PREVIEW_FAILED", exception.Message); }
        }
    }

    public sealed class RigBakeIkClipTool : IMcpTool
    {
        public string Name => "rig_bake_ik_clip"; public string Description => "Samples a clip, solves a native IK binding per frame and bakes standalone rotation curves.";
        public JObject InputSchema { get { var schema = RigCreateIkTool.IkSchema(true); return schema; } } public bool IsWrite => true; public ToolDescriptor Descriptor => RigToolSchemas.Write(Name);
        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error); if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            var binding = target.GetComponent<RigIkBinding>() ?? target.GetComponentInChildren<RigIkBinding>(true); var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>((string)arguments?["clipAssetPath"]);
            if (binding == null || clip == null) return ToolResult.Error("IK_ASSET_NOT_FOUND", "RigIkBinding or clipAssetPath was not found.");
            try { var character = binding.CharacterRoot != null ? binding.CharacterRoot.gameObject : RigTargetResolver.Resolve(target).Root; var output = RigIkService.Bake(character, binding, clip, (float?)arguments?["frameRate"] ?? 30f, (string)arguments?["outputFolder"]); return ToolResult.Ok("IK clip baked.", new JObject { ["assetPath"] = AssetDatabase.GetAssetPath(output), ["curveCount"] = AnimationUtility.GetCurveBindings(output).Length }); }
            catch (Exception exception) { return ToolResult.Error("IK_BAKE_FAILED", exception.Message); }
        }
    }

    public sealed class RigAnalyzeAutoRigTool : IMcpTool
    {
        public string Name => "rig_analyze_auto_rig"; public string Description => "Builds a deterministic humanoid landmark proposal locally, or validates an opt-in versioned AI JSON proposal, without modifying assets.";
        public JObject InputSchema => AutoRigSchema(false); public bool IsWrite => false; public ToolDescriptor Descriptor => RigToolSchemas.Read(Name);
        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error); if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            try
            {
                var proposal = !string.IsNullOrWhiteSpace((string)arguments?["aiProposalJson"])
                    ? RigAiProposalService.ImportJson(target, (string)arguments?["aiProposalJson"])
                    : HumanoidAutoRigAnalyzer.Analyze(target);
                var payload = proposal.ToJson();
                if ((bool?)arguments?["includeAiRequest"] == true)
                    payload["aiRequest"] = RigAiProposalService.BuildRequest(target, (arguments?["viewAssetPaths"] as JArray)?.Values<string>());
                return ToolResult.Ok("Auto-rig proposal ready.", payload);
            }
            catch (Exception exception) { return ToolResult.Error("AUTO_RIG_ANALYSIS_FAILED", exception.Message); }
        }
        internal static JObject AutoRigSchema(bool apply) => new JObject { ["type"] = "object", ["properties"] = new JObject
        {
            ["target"] = new JObject { ["type"] = "object" }, ["instanceId"] = new JObject { ["type"] = "integer" }, ["aiProposalJson"] = new JObject { ["type"] = "string" },
            ["includeAiRequest"] = new JObject { ["type"] = "boolean", ["default"] = false },
            ["viewAssetPaths"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
            ["meshHash"] = new JObject { ["type"] = "string" }, ["proposalHash"] = new JObject { ["type"] = "string" }, ["outputFolder"] = new JObject { ["type"] = "string" },
        }, ["required"] = apply ? new JArray("meshHash", "proposalHash") : new JArray(), ["additionalProperties"] = false };
    }

    public sealed class RigPreviewAutoRigTool : IMcpTool
    {
        public string Name => "rig_preview_auto_rig"; public string Description => "Shows the hash-compatible auto-rig landmark and skeleton proposal in the Scene View.";
        public JObject InputSchema => RigAnalyzeAutoRigTool.AutoRigSchema(false); public bool IsWrite => false; public ToolDescriptor Descriptor => RigToolSchemas.Preview(Name);
        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error); if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            try { var proposal = RigAutoRigProposalRegistry.Get(target) ?? HumanoidAutoRigAnalyzer.Analyze(target); AutoRigPreviewController.Show(proposal); RigAnimationStudioWindow.Open(target); return ToolResult.Ok("Auto-rig preview active.", proposal.ToJson()); }
            catch (Exception exception) { return ToolResult.Error("AUTO_RIG_PREVIEW_FAILED", exception.Message); }
        }
    }

    public sealed class RigApplyAutoRigTool : IMcpTool
    {
        public string Name => "rig_apply_auto_rig"; public string Description => "Creates an editable skinned mesh, skeleton and normalized initial weights from an exact reviewed auto-rig proposal hash; imported assets remain unchanged.";
        public JObject InputSchema => RigAnalyzeAutoRigTool.AutoRigSchema(true); public bool IsWrite => true; public ToolDescriptor Descriptor => RigToolSchemas.Write(Name);
        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error); if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            try
            {
                var proposal = RigAutoRigProposalRegistry.Get(target) ?? HumanoidAutoRigAnalyzer.Analyze(target);
                var result = HumanoidAutoRigService.Apply(proposal, (string)arguments?["proposalHash"], (string)arguments?["meshHash"], (string)arguments?["outputFolder"]);
                var validation = RigInspectorService.Inspect(result.Renderer.gameObject);
                return ToolResult.Ok("Auto rig applied.", new JObject { ["meshAssetPath"] = result.MeshAssetPath, ["profileAssetPath"] = result.ProfileAssetPath, ["avatarAssetPath"] = result.AvatarAssetPath, ["validationClipAssetPath"] = result.ValidationClipAssetPath, ["avatarStatus"] = result.AvatarStatus, ["boneCount"] = result.BoneCount, ["vertexCount"] = result.VertexCount, ["rigValid"] = validation.IsValid, ["rigIssues"] = validation.ToJson()["issues"] });
            }
            catch (Exception exception) { return ToolResult.Error("AUTO_RIG_APPLY_FAILED", exception.Message); }
        }
    }
}
