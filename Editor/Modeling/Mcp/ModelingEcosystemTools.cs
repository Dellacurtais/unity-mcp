using System;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Mcp
{
    public sealed class StageExternalModelAssetTool : IMcpTool
    {
        public string Name => "mesh_stage_external_asset";
        public string Description => "Copies an approved 3D file into Asset Modeler staging, imports it synchronously and rejects invalid or unreadable meshes.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["sourcePath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["outputFolder"] = new JObject { ["type"] = "string", ["pattern"] = "^Assets(?:/|$)" },
                ["maximumBytes"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 2147483648L },
            },
            ["required"] = new JArray { "sourcePath" },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["assetPath"] = new JObject { ["type"] = "string" },
                ["byteCount"] = new JObject { ["type"] = "integer" },
                ["meshCount"] = new JObject { ["type"] = "integer" },
                ["vertexCount"] = new JObject { ["type"] = "integer" },
                ["triangleCount"] = new JObject { ["type"] = "integer" },
                ["issues"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
                ["valid"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray { "assetPath", "byteCount", "meshCount", "vertexCount", "triangleCount", "issues", "valid" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(Name, OutputSchema, McpCapability.AssetWrite, McpRiskLevel.High, McpConfirmationPolicy.Required, false);
        public ToolResult Execute(JObject arguments)
        {
            try
            {
                var result = AssetModelerStagingService.Stage(
                    (string)arguments["sourcePath"],
                    (string)arguments["outputFolder"],
                    (long?)arguments["maximumBytes"] ?? 1024L * 1024L * 1024L);
                return ToolResult.Ok("External model staged and validated.", new JObject
                {
                    ["assetPath"] = result.AssetPath,
                    ["byteCount"] = result.ByteCount,
                    ["meshCount"] = result.MeshCount,
                    ["vertexCount"] = result.VertexCount,
                    ["triangleCount"] = result.TriangleCount,
                    ["issues"] = new JArray(result.Issues),
                    ["valid"] = result.IsValid,
                });
            }
            catch (Exception exception) { return ModelingSessionToolUtility.Exception(exception); }
        }
    }
}
