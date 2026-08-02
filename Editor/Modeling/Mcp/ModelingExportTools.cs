using System;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Maicon.SceneAnalyzer.Editor.Modeling.Assets;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Mcp
{
    public sealed class ExportMeshFbxTool : IMcpTool
    {
        public string Name => "mesh_export_fbx";
        public string Description => "Exports a selected hierarchy through Unity's optional FBX Exporter package and reports when that package is unavailable.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["target"] = SchemaHelpers.TargetRefSchema,
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["outputPath"] = new JObject { ["type"] = "string", ["pattern"] = "^Assets/.*\\.fbx$" },
            },
            ["required"] = new JArray { "outputPath" },
            ["oneOf"] = new JArray
            {
                new JObject { ["required"] = new JArray { "target" } },
                new JObject { ["required"] = new JArray { "instanceId" } },
            },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["outputPath"] = new JObject { ["type"] = "string" },
                ["exporterAvailable"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray { "outputPath", "exporterAvailable" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(
            Name,
            OutputSchema,
            McpCapability.AssetWrite,
            McpRiskLevel.Medium,
            McpConfirmationPolicy.Required,
            false);

        public ToolResult Execute(JObject arguments)
        {
            var target = ToolTargetResolver.ResolveGameObject(arguments, out var error);
            if (target == null) return ToolResult.Error("TARGET_NOT_FOUND", error);
            try
            {
                var path = FbxExportAdapter.Export(target, (string)arguments["outputPath"]);
                return ToolResult.Ok("FBX exported through the optional Unity FBX Exporter.", new JObject
                {
                    ["outputPath"] = path,
                    ["exporterAvailable"] = true,
                });
            }
            catch (Exception exception)
            {
                return ModelingSessionToolUtility.Exception(exception);
            }
        }
    }
}
