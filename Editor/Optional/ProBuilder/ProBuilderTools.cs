using System;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Optional.ProBuilder
{
    public sealed class CreateProBuilderShapeTool : IMcpTool
    {
        public string Name => "create_probuilder_shape";
        public string Description => "Creates an editable ProBuilder primitive using ShapeGenerator.CreateShape.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["name"] = new JObject { ["type"] = "string", ["default"] = "ProBuilder Shape" },
                ["shape"] = new JObject { ["type"] = "string", ["enum"] = new JArray(Enum.GetNames(typeof(ShapeType))), ["default"] = "Cube" },
                ["pivot"] = new JObject { ["type"] = "string", ["enum"] = new JArray(Enum.GetNames(typeof(PivotLocation))), ["default"] = "Center" },
                ["position"] = SchemaHelpers.Vec3Schema,
                ["scale"] = SchemaHelpers.Vec3Schema,
                ["parentInstanceId"] = new JObject { ["type"] = "integer" },
                ["parentTarget"] = SchemaHelpers.TargetRefSchema,
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;

        public ToolResult Execute(JObject arguments)
        {
            if (!Enum.TryParse((string)arguments["shape"] ?? "Cube", true, out ShapeType shape))
                return ToolResult.Error("BAD_SHAPE", (string)arguments["shape"]);
            if (!Enum.TryParse((string)arguments["pivot"] ?? "Center", true, out PivotLocation pivot))
                return ToolResult.Error("BAD_PIVOT", (string)arguments["pivot"]);
            var parent = OptionalToolSupport.ResolveOptionalGameObject(
                arguments["parentTarget"],
                (int?)arguments["parentInstanceId"],
                "parent",
                out var parentError);
            if (parentError != null) return parentError;
            ProBuilderMesh mesh = null;
            try
            {
                mesh = ShapeGenerator.CreateShape(shape, pivot);
                if (mesh == null) return ToolResult.Error("CREATE_PROBUILDER_FAILED", "ShapeGenerator returned null");
                using (var mutation = new MutationScope("Create ProBuilder Shape"))
                {
                    mutation.RegisterCreated(mesh.gameObject);
                    mesh.gameObject.name = (string)arguments["name"] ?? shape.ToString();
                    if (parent != null)
                    {
                        SceneManager.MoveGameObjectToScene(mesh.gameObject, parent.scene);
                        mutation.SetParent(mesh.transform, parent.transform);
                    }
                    mesh.transform.position = JsonHelpers.ParseVector3(arguments["position"], Vector3.zero);
                    mesh.transform.localScale = JsonHelpers.ParseVector3(arguments["scale"], Vector3.one);
                    mutation.Touch(mesh.transform);
                    mutation.Complete();
                }
                return ToolResult.Ok($"created ProBuilder {shape}", MeshJson(mesh));
            }
            catch (Exception exception)
            {
                if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh.gameObject);
                return ToolResult.Error("CREATE_PROBUILDER_FAILED", exception.Message);
            }
        }

        internal static JObject MeshJson(ProBuilderMesh mesh) => new JObject
        {
            ["target"] = ToolTargetRef.Capture(mesh)?.ToJson(),
            ["gameObject"] = ToolTargetRef.Capture(mesh != null ? mesh.gameObject : null)?.ToJson(),
            ["vertexCount"] = mesh?.vertexCount ?? 0,
            ["faceCount"] = mesh?.faceCount ?? 0,
            ["edgeCount"] = mesh?.edgeCount ?? 0,
            ["indexCount"] = mesh?.indexCount ?? 0,
            ["meshSyncState"] = mesh != null ? mesh.meshSyncState.ToString() : null,
        };
    }

    public sealed class RefreshProBuilderMeshTool : IMcpTool
    {
        public string Name => "refresh_probuilder_mesh";
        public string Description => "Rebuilds the Unity Mesh and all derived ProBuilder attributes with Undo.";
        public JObject InputSchema => OptionalToolSupport.TargetSchema();
        public bool IsWrite => true;

        public ToolResult Execute(JObject arguments)
        {
            var gameObject = OptionalToolSupport.ResolveGameObject(arguments, out var error);
            if (gameObject == null) return error;
            var mesh = gameObject.GetComponent<ProBuilderMesh>();
            if (mesh == null) return ToolResult.Error("PROBUILDER_MESH_NOT_FOUND", gameObject.name);
            try
            {
                using (var mutation = new MutationScope("Refresh ProBuilder Mesh"))
                {
                    mutation.Record(mesh);
                    mesh.ToMesh();
                    mesh.Refresh(RefreshMask.All);
                    mutation.Complete();
                }
                return ToolResult.Ok("refreshed ProBuilder mesh", CreateProBuilderShapeTool.MeshJson(mesh));
            }
            catch (Exception exception) { return ToolResult.Error("PROBUILDER_REFRESH_FAILED", exception.Message); }
        }
    }

    public sealed class ValidateProBuilderSetupTool : IMcpTool
    {
        public string Name => "validate_probuilder_setup";
        public string Description => "Validates ProBuilder scene meshes and Mesh synchronization state.";
        public JObject InputSchema => OptionalToolSupport.EmptySchema();
        public bool IsWrite => false;

        public ToolResult Execute(JObject arguments)
        {
            var issues = new JArray();
            var meshes = OptionalToolSupport.FindSceneComponents(typeof(ProBuilderMesh)).Cast<ProBuilderMesh>().ToArray();
            foreach (var mesh in meshes)
            {
                if (mesh.vertexCount == 0 || mesh.faceCount == 0)
                    issues.Add(new JObject { ["severity"] = "error", ["code"] = "EMPTY_PROBUILDER_MESH", ["target"] = ToolTargetRef.Capture(mesh)?.ToJson() });
                if (mesh.meshSyncState != MeshSyncState.InSync)
                    issues.Add(new JObject { ["severity"] = "warning", ["code"] = "PROBUILDER_MESH_OUT_OF_SYNC", ["state"] = mesh.meshSyncState.ToString(), ["target"] = ToolTargetRef.Capture(mesh)?.ToJson() });
            }
            return ToolResult.Ok($"{issues.Count} ProBuilder issue(s)", new JObject
            {
                ["valid"] = !issues.Any(token => string.Equals((string)token["severity"], "error", StringComparison.Ordinal)),
                ["meshCount"] = meshes.Length,
                ["issues"] = issues,
            });
        }
    }
}
