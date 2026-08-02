using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Mcp
{
    internal static class MeshSelectionToolUtility
    {
        public static readonly JObject OutputSchema = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["kind"] = new JObject { ["type"] = "string" },
                ["indices"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" } },
                ["topologyRevision"] = new JObject { ["type"] = "integer" },
                ["regions"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
                ["links"] = new JObject { ["type"] = "integer" },
                ["constraints"] = new JObject { ["type"] = "integer" },
            },
            ["required"] = new JArray { "kind", "indices", "topologyRevision", "regions", "links", "constraints" },
            ["additionalProperties"] = false,
        };

        public static JObject Json(MeshEditDocument document)
        {
            var indices = new JArray();
            for (int i = 0; i < document.Selection.Indices.Count; i++) indices.Add(document.Selection.Indices[i]);
            var regions = new JArray();
            for (int i = 0; i < document.Selection.Regions.Count; i++) regions.Add(document.Selection.Regions[i].Name);
            return new JObject
            {
                ["kind"] = document.Selection.Kind.ToString().ToLowerInvariant(),
                ["indices"] = indices,
                ["topologyRevision"] = document.Selection.TopologyRevision,
                ["regions"] = regions,
                ["links"] = document.Selection.Links.Count,
                ["constraints"] = document.Selection.Constraints.Count,
            };
        }

        public static bool TryRevision(MeshEditDocument document, JObject arguments, out ToolResult failure)
        {
            failure = null;
            uint expected = (uint)((long?)arguments?["expectedTopologyRevision"] ?? document.Revision.Topology);
            if (expected == document.Revision.Topology) return true;
            failure = ToolResult.Error("STALE_MESH_REVISION", $"Expected topology revision {expected}, current {document.Revision.Topology}.");
            return false;
        }

        public static MeshElementKind ParseKind(string value)
        {
            if (!Enum.TryParse(value, true, out MeshElementKind kind))
                throw new InvalidOperationException($"SELECTION_KIND_INVALID: '{value}'.");
            return kind;
        }

        public static List<int> Indices(JArray array)
        {
            var values = new List<int>();
            if (array != null) for (int i = 0; i < array.Count; i++) values.Add((int)array[i]);
            return values;
        }

        public static void Validate(MeshEditDocument document, MeshElementKind kind, IReadOnlyList<int> values)
        {
            int count;
            switch (kind)
            {
                case MeshElementKind.Vertex: count = document.WorkingMesh.vertexCount; break;
                case MeshElementKind.Face: count = CountFaces(document.WorkingMesh); break;
                case MeshElementKind.Edge: count = HalfEdgeTopology.Build(document.WorkingMesh).EdgeFaces.Count; break;
                default: count = int.MaxValue; break;
            }
            for (int i = 0; i < values.Count; i++)
                if (values[i] < 0 || values[i] >= count)
                    throw new InvalidOperationException($"SELECTION_INDEX_OUT_OF_RANGE: {kind} {values[i]} is outside 0..{count - 1}.");
        }

        private static int CountFaces(Mesh mesh)
        {
            int count = 0;
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                if (mesh.GetTopology(subMesh) == MeshTopology.Triangles) count += mesh.GetIndices(subMesh, true).Length / 3;
            return count;
        }
    }

    public sealed class GetMeshSelectionTool : IMcpTool
    {
        public string Name => "mesh_get_selection";
        public string Description => "Returns the document element selection, named regions and constraint counts.";
        public JObject InputSchema => ModelingSessionToolSchemas.DocumentInput();
        public JObject OutputSchema => MeshSelectionToolUtility.OutputSchema;
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(Name, OutputSchema, McpCapability.Read, McpRiskLevel.Low, McpConfirmationPolicy.None, true);
        public ToolResult Execute(JObject arguments)
        {
            var document = ModelingSessionToolUtility.LoadDocument(arguments, out var failure);
            return failure ?? ToolResult.Ok("Mesh selection loaded.", MeshSelectionToolUtility.Json(document));
        }
    }

    public sealed class SelectMeshElementsTool : IMcpTool
    {
        public string Name => "mesh_select_elements";
        public string Description => "Selects vertices, edges or faces by stable indices for the current topology revision.";
        public JObject InputSchema => SelectionSchema();
        public JObject OutputSchema => MeshSelectionToolUtility.OutputSchema;
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(Name, OutputSchema, McpCapability.AssetWrite, McpRiskLevel.Low, McpConfirmationPolicy.None, false);
        public ToolResult Execute(JObject arguments)
        {
            var document = ModelingSessionToolUtility.LoadDocument(arguments, out var failure);
            if (failure != null || !MeshSelectionToolUtility.TryRevision(document, arguments, out failure)) return failure;
            try
            {
                var kind = MeshSelectionToolUtility.ParseKind((string)arguments["kind"]);
                var incoming = MeshSelectionToolUtility.Indices((JArray)arguments["indices"]);
                MeshSelectionToolUtility.Validate(document, kind, incoming);
                var result = new SortedSet<int>();
                string mode = ((string)arguments["mode"] ?? "replace").ToLowerInvariant();
                if (mode != "replace" && document.Selection.Kind == kind)
                    for (int i = 0; i < document.Selection.Indices.Count; i++) result.Add(document.Selection.Indices[i]);
                for (int i = 0; i < incoming.Count; i++)
                {
                    if (mode == "remove") result.Remove(incoming[i]);
                    else if (mode == "toggle" && !result.Add(incoming[i])) result.Remove(incoming[i]);
                    else result.Add(incoming[i]);
                }
                Undo.RecordObject(document, "Select mesh elements");
                document.Selection.SetSelection(kind, result, document.Revision.Topology);
                EditorUtility.SetDirty(document);
                return ToolResult.Ok("Mesh selection updated.", MeshSelectionToolUtility.Json(document));
            }
            catch (Exception exception) { return ModelingSessionToolUtility.Exception(exception); }
        }

        private static JObject SelectionSchema()
        {
            var schema = ModelingSessionToolSchemas.DocumentInput("kind", "indices", "expectedTopologyRevision");
            var properties = (JObject)schema["properties"];
            properties["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray("vertex", "edge", "face") };
            properties["indices"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer", ["minimum"] = 0 }, ["uniqueItems"] = true };
            properties["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray("replace", "add", "remove", "toggle"), ["default"] = "replace" };
            properties["expectedTopologyRevision"] = new JObject { ["type"] = "integer", ["minimum"] = 0 };
            return schema;
        }
    }

    public sealed class SelectMeshRegionTool : IMcpTool
    {
        public string Name => "mesh_select_region";
        public string Description => "Saves the current element selection as a named region or restores a named region.";
        public JObject InputSchema
        {
            get
            {
                var schema = ModelingSessionToolSchemas.DocumentInput("name", "action", "expectedTopologyRevision");
                var properties = (JObject)schema["properties"];
                properties["name"] = new JObject { ["type"] = "string", ["minLength"] = 1 };
                properties["action"] = new JObject { ["type"] = "string", ["enum"] = new JArray("save", "select") };
                properties["expectedTopologyRevision"] = new JObject { ["type"] = "integer", ["minimum"] = 0 };
                return schema;
            }
        }
        public JObject OutputSchema => MeshSelectionToolUtility.OutputSchema;
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(Name, OutputSchema, McpCapability.AssetWrite, McpRiskLevel.Low, McpConfirmationPolicy.None, false);
        public ToolResult Execute(JObject arguments)
        {
            var document = ModelingSessionToolUtility.LoadDocument(arguments, out var failure);
            if (failure != null || !MeshSelectionToolUtility.TryRevision(document, arguments, out failure)) return failure;
            try
            {
                Undo.RecordObject(document, "Edit mesh region");
                string name = (string)arguments["name"];
                if (string.Equals((string)arguments["action"], "save", StringComparison.OrdinalIgnoreCase)) document.Selection.SaveRegion(name);
                else if (!document.Selection.SelectRegion(name, document.Revision.Topology)) return ToolResult.Error("REGION_NOT_FOUND", $"Region '{name}' does not exist.");
                EditorUtility.SetDirty(document);
                return ToolResult.Ok("Mesh region updated.", MeshSelectionToolUtility.Json(document));
            }
            catch (Exception exception) { return ModelingSessionToolUtility.Exception(exception); }
        }
    }

    public sealed class LinkMeshRegionsTool : IMcpTool
    {
        public string Name => "mesh_link_regions";
        public string Description => "Links two named regions with stiffness and XYZ axis constraints for snapping/deformation solvers.";
        public JObject InputSchema
        {
            get
            {
                var schema = ModelingSessionToolSchemas.DocumentInput("sourceRegion", "targetRegion", "expectedTopologyRevision");
                var properties = (JObject)schema["properties"];
                properties["name"] = new JObject { ["type"] = "string" };
                properties["sourceRegion"] = new JObject { ["type"] = "string", ["minLength"] = 1 };
                properties["targetRegion"] = new JObject { ["type"] = "string", ["minLength"] = 1 };
                properties["stiffness"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1, ["default"] = 1 };
                properties["axisMask"] = VectorSchema();
                properties["expectedTopologyRevision"] = new JObject { ["type"] = "integer", ["minimum"] = 0 };
                return schema;
            }
        }
        public JObject OutputSchema => MeshSelectionToolUtility.OutputSchema;
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(Name, OutputSchema, McpCapability.AssetWrite, McpRiskLevel.Low, McpConfirmationPolicy.None, false);
        public ToolResult Execute(JObject arguments)
        {
            var document = ModelingSessionToolUtility.LoadDocument(arguments, out var failure);
            if (failure != null || !MeshSelectionToolUtility.TryRevision(document, arguments, out failure)) return failure;
            try
            {
                Undo.RecordObject(document, "Link mesh regions");
                document.Selection.Link(
                    (string)arguments["name"],
                    (string)arguments["sourceRegion"],
                    (string)arguments["targetRegion"],
                    (float?)arguments["stiffness"] ?? 1f,
                    ReadVector((JObject)arguments["axisMask"], Vector3.one));
                EditorUtility.SetDirty(document);
                return ToolResult.Ok("Mesh regions linked.", MeshSelectionToolUtility.Json(document));
            }
            catch (Exception exception) { return ModelingSessionToolUtility.Exception(exception); }
        }

        internal static JObject VectorSchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["x"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 },
                ["y"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 },
                ["z"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 },
            },
            ["additionalProperties"] = false,
        };

        internal static Vector3 ReadVector(JObject value, Vector3 fallback)
            => value == null ? fallback : new Vector3((float?)value["x"] ?? fallback.x, (float?)value["y"] ?? fallback.y, (float?)value["z"] ?? fallback.z);
    }

    public sealed class PaintMeshRegionConstraintTool : IMcpTool
    {
        public string Name => "mesh_paint_region_constraint";
        public string Description => "Assigns solver weight, rigidity and pinning to a named mesh region.";
        public JObject InputSchema
        {
            get
            {
                var schema = ModelingSessionToolSchemas.DocumentInput("region", "expectedTopologyRevision");
                var properties = (JObject)schema["properties"];
                properties["region"] = new JObject { ["type"] = "string", ["minLength"] = 1 };
                properties["weight"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1, ["default"] = 1 };
                properties["rigidity"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1, ["default"] = 0 };
                properties["pinned"] = new JObject { ["type"] = "boolean", ["default"] = false };
                properties["expectedTopologyRevision"] = new JObject { ["type"] = "integer", ["minimum"] = 0 };
                return schema;
            }
        }
        public JObject OutputSchema => MeshSelectionToolUtility.OutputSchema;
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => ModelingSessionToolUtility.Descriptor(Name, OutputSchema, McpCapability.AssetWrite, McpRiskLevel.Low, McpConfirmationPolicy.None, false);
        public ToolResult Execute(JObject arguments)
        {
            var document = ModelingSessionToolUtility.LoadDocument(arguments, out var failure);
            if (failure != null || !MeshSelectionToolUtility.TryRevision(document, arguments, out failure)) return failure;
            try
            {
                Undo.RecordObject(document, "Paint mesh constraint");
                document.Selection.SetConstraint((string)arguments["region"], (float?)arguments["weight"] ?? 1f, (float?)arguments["rigidity"] ?? 0f, (bool?)arguments["pinned"] ?? false);
                EditorUtility.SetDirty(document);
                return ToolResult.Ok("Mesh region constraint updated.", MeshSelectionToolUtility.Json(document));
            }
            catch (Exception exception) { return ModelingSessionToolUtility.Exception(exception); }
        }
    }
}
