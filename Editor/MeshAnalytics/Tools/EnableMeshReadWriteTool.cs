using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Tools
{
    /// Flips an imported model's `Read/Write Enabled` flag on, then
    /// re-imports it. Required before any mesh-analytics detector can
    /// read vertex / triangle data — Unity's default for FBX / glb / OBJ
    /// is `isReadable = false` (CPU data stripped after upload to GPU to
    /// save runtime memory).
    ///
    /// This is the auto-recovery path for the `MESH_NOT_READABLE` error
    /// raised by inspect / detect / sample / sidecar tools. The LLM can
    /// chain detector → on MESH_NOT_READABLE → enable_read_write → retry.
    ///
    /// Idempotent: if the setting is already on, returns without
    /// re-importing. Only works for assets backed by a `ModelImporter`
    /// (FBX, OBJ, glb, dae, …); native `.asset` meshes and procedural
    /// runtime meshes need a different fix (out of scope).
    public sealed class EnableMeshReadWriteTool : IMcpTool
    {
        public string Name => "mesh_enable_read_write";
        public string Description =>
            "Re-imports the model asset behind a mesh with 'Read/Write Enabled' so mesh analytics tools can read its CPU-side data. Resolves either an instanceId (→ GameObject → mesh → asset) or an assetPath. Idempotent: a no-op when already enabled. Call this in response to a MESH_NOT_READABLE error, then retry the original detector. Only ModelImporter-backed assets (FBX/OBJ/glb/dae) are supported.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer", ["description"] = "GameObject id whose mesh asset should be made readable. Mutually exclusive with assetPath." },
                ["assetPath"] = new JObject { ["type"] = "string", ["description"] = "Direct asset path like 'Assets/Models/Sidewalk.fbx'. Mutually exclusive with instanceId." },
                ["reimport"] = new JObject { ["type"] = "boolean", ["default"] = true, ["description"] = "When false, only flips the importer flag without triggering a re-import (the change applies on the next Unity asset refresh)." },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            string assetPath = (string)args["assetPath"];
            int instanceId = (int?)args["instanceId"] ?? 0;
            bool reimport = (bool?)args["reimport"] ?? true;

            if (string.IsNullOrEmpty(assetPath) == (instanceId == 0))
                return ToolResult.Error("BAD_ARGS", "provide exactly one of instanceId or assetPath");

            // Resolve to an asset path. instanceId path: GameObject → mesh
            // → AssetDatabase.GetAssetPath(mesh).
            if (string.IsNullOrEmpty(assetPath))
            {
                var go = MeshResolver.ResolveGameObject(instanceId);
                if (go == null)
                    return ToolResult.Error("NOT_FOUND", $"no GameObject with instanceId {instanceId}");
                var mesh = MeshResolver.ResolveMesh(go);
                if (mesh == null)
                    return ToolResult.Error("NOT_FOUND", $"GameObject '{go.name}' has no mesh");
                assetPath = AssetDatabase.GetAssetPath(mesh);
                if (string.IsNullOrEmpty(assetPath))
                    return ToolResult.Error("NOT_AN_ASSET",
                        $"mesh '{mesh.name}' is procedural / runtime — not backed by an asset file. Read/Write can't be set via importer.");
            }

            var importer = AssetImporter.GetAtPath(assetPath);
            if (importer == null)
                return ToolResult.Error("NOT_FOUND", $"no importer for '{assetPath}'");

            if (!(importer is ModelImporter model))
            {
                return ToolResult.Error("WRONG_IMPORTER",
                    $"asset '{assetPath}' uses {importer.GetType().Name}, not ModelImporter. " +
                    $"Read/Write toggling is only wired for FBX/OBJ/glb/dae assets. " +
                    $"For native .asset meshes, edit the Mesh asset's Read/Write Enabled directly.");
            }

            if (!AssetDatabase.IsOpenForEdit(assetPath, out var editMessage, StatusQueryOptions.ForceUpdate))
                return ToolResult.Error("ASSET_NOT_EDITABLE", $"'{assetPath}' is not editable: {editMessage}");

            bool wasEnabled = model.isReadable;
            if (!wasEnabled)
            {
                Undo.RegisterCompleteObjectUndo(model, "Enable Mesh Read/Write");
                try
                {
                    model.isReadable = true;
                    EditorUtility.SetDirty(model);
                    if (reimport)
                        model.SaveAndReimport();
                    else
                        AssetDatabase.WriteImportSettingsIfDirty(assetPath);
                }
                catch (System.Exception exception)
                {
                    model.isReadable = wasEnabled;
                    EditorUtility.SetDirty(model);
                    AssetDatabase.WriteImportSettingsIfDirty(assetPath);
                    return ToolResult.Error("REIMPORT_FAILED", exception.Message,
                        new JObject { ["assetPath"] = assetPath, ["restoredReadable"] = wasEnabled });
                }
            }

            var payload = new JObject
            {
                ["assetPath"] = assetPath,
                ["wasEnabled"] = wasEnabled,
                ["isEnabledNow"] = model.isReadable,
                ["reimported"] = !wasEnabled && reimport,
                ["savedAssets"] = false,
                ["warning"] = !wasEnabled && reimport
                    ? "SaveAndReimport is synchronous and may pause the Editor for large model assets."
                    : null,
            };
            string summary = wasEnabled
                ? $"'{assetPath}' was already Read/Write Enabled — no-op"
                : (reimport
                    ? $"'{assetPath}' Read/Write Enabled set, asset re-imported"
                    : $"'{assetPath}' Read/Write Enabled set, deferred reimport");
            return ToolResult.Ok(summary, payload);
        }
    }
}
