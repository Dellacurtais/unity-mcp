using System;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Maicon.SceneAnalyzer.Editor.Modeling.Commands;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Mcp
{
    internal static class ModelingModifierToolSchemas
    {
        public static JObject Input(params string[] required)
        {
            var schema = ModelingSessionToolSchemas.DocumentInput(required);
            var properties = (JObject)schema["properties"];
            properties["expectedTopologyRevision"] = new JObject { ["type"] = "integer", ["minimum"] = 0 };
            properties["expectedGeometryRevision"] = new JObject { ["type"] = "integer", ["minimum"] = 0 };
            return schema;
        }
    }

    public sealed class ReorderMeshModifierTool : IMcpTool
    {
        public string Name => "mesh_reorder_modifier";
        public string Description => "Reorders a recorded modifier and transactionally rebuilds the editable mesh.";
        public JObject InputSchema
        {
            get
            {
                var schema = ModelingModifierToolSchemas.Input("fromIndex", "toIndex", "expectedTopologyRevision", "expectedGeometryRevision");
                var properties = (JObject)schema["properties"];
                properties["fromIndex"] = new JObject { ["type"] = "integer", ["minimum"] = 0 };
                properties["toIndex"] = new JObject { ["type"] = "integer", ["minimum"] = 0 };
                return schema;
            }
        }
        public JObject OutputSchema => BeginMeshEditSessionTool.ContextOutput;
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(Name, OutputSchema, McpCapability.AssetWrite | McpCapability.Destructive, McpRiskLevel.High, McpConfirmationPolicy.Required, false);
        public ToolResult Execute(JObject arguments)
        {
            var document = ModelingSessionToolUtility.LoadDocument(arguments, out var failure);
            if (failure != null) return failure;
            try
            {
                ModelingCommandService.ReorderModifier(document, (int)arguments["fromIndex"], (int)arguments["toIndex"], ModelingSessionToolUtility.Revision(arguments));
                return ToolResult.Ok("Modifier reordered and mesh rebuilt.", BeginMeshEditSessionTool.ContextJson(document));
            }
            catch (Exception exception) { return ModelingSessionToolUtility.Exception(exception); }
        }
    }

    public sealed class RemoveMeshModifierTool : IMcpTool
    {
        public string Name => "mesh_remove_modifier";
        public string Description => "Removes a recorded modifier and transactionally rebuilds the editable mesh.";
        public JObject InputSchema
        {
            get
            {
                var schema = ModelingModifierToolSchemas.Input("index", "expectedTopologyRevision", "expectedGeometryRevision");
                ((JObject)schema["properties"])["index"] = new JObject { ["type"] = "integer", ["minimum"] = 0 };
                return schema;
            }
        }
        public JObject OutputSchema => BeginMeshEditSessionTool.ContextOutput;
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(Name, OutputSchema, McpCapability.AssetWrite | McpCapability.Destructive, McpRiskLevel.High, McpConfirmationPolicy.Required, false);
        public ToolResult Execute(JObject arguments)
        {
            var document = ModelingSessionToolUtility.LoadDocument(arguments, out var failure);
            if (failure != null) return failure;
            try
            {
                ModelingCommandService.RemoveModifier(document, (int)arguments["index"], ModelingSessionToolUtility.Revision(arguments));
                return ToolResult.Ok("Modifier removed and mesh rebuilt.", BeginMeshEditSessionTool.ContextJson(document));
            }
            catch (Exception exception) { return ModelingSessionToolUtility.Exception(exception); }
        }
    }
}
