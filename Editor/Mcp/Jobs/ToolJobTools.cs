using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Jobs
{
    internal static class ToolJobSchemas
    {
        public static JObject Input(bool includeTimeout)
        {
            var properties = new JObject
            {
                ["jobId"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 },
            };
            if (includeTimeout)
                properties["timeoutSeconds"] = new JObject
                {
                    ["type"] = "number",
                    ["minimum"] = 0.1,
                    ["maximum"] = 300,
                    ["default"] = 30,
                };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray { "jobId" },
                ["additionalProperties"] = false,
            };
        }

        public static JObject Output => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["jobId"] = new JObject { ["type"] = "string" },
                ["kind"] = new JObject { ["type"] = "string" },
                ["status"] = new JObject { ["type"] = "string" },
                ["progress"] = new JObject { ["type"] = "number" },
                ["message"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["error"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["result"] = new JObject(),
                ["input"] = new JObject { ["type"] = "object" },
                ["createdUtc"] = new JObject { ["type"] = "string" },
                ["startedUtc"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["completedUtc"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["canCancel"] = new JObject { ["type"] = "boolean" },
            },
            ["required"] = new JArray { "jobId", "kind", "status", "progress", "createdUtc", "canCancel" },
            ["additionalProperties"] = false,
        };

        public static ToolDescriptor ReadDescriptor(string name) => new ToolDescriptor
        {
            Name = name,
            Category = "job",
            Capabilities = McpCapability.Read,
            Risk = McpRiskLevel.Low,
            ConfirmationPolicy = McpConfirmationPolicy.None,
            EditorMode = McpEditorMode.Any,
            IsIdempotent = true,
            ExecutionRequirements = McpExecutionRequirement.None,
            HeadlessSafe = true,
        };

        public static ToolDescriptor CancelDescriptor(string name) => new ToolDescriptor
        {
            Name = name,
            Category = "job",
            Capabilities = McpCapability.ProjectSettings,
            Risk = McpRiskLevel.Medium,
            ConfirmationPolicy = McpConfirmationPolicy.None,
            EditorMode = McpEditorMode.Any,
            IsIdempotent = true,
            ExecutionRequirements = McpExecutionRequirement.None,
            HeadlessSafe = true,
        };

        public static ToolDescriptor WaitDescriptor(string name) => new ToolDescriptor
        {
            Name = name,
            Category = "job",
            Capabilities = McpCapability.Read,
            Risk = McpRiskLevel.Low,
            ConfirmationPolicy = McpConfirmationPolicy.None,
            EditorMode = McpEditorMode.Any,
            IsIdempotent = true,
            ExecutionRequirements = McpExecutionRequirement.MainThread | McpExecutionRequirement.EditorUpdates,
            HeadlessSafe = true,
        };
    }

    public sealed class GetToolJobTool : IMcpTool
    {
        public string Name => "get_tool_job";
        public string Description => "Returns any Unity MCP background job by jobId, independent of the subsystem that created it.";
        public JObject InputSchema => ToolJobSchemas.Input(false);
        public JObject OutputSchema => ToolJobSchemas.Output;
        public ToolDescriptor Descriptor => ToolJobSchemas.ReadDescriptor(Name);
        public bool IsWrite => false;

        public ToolResult Execute(JObject arguments)
        {
            var id = (string)arguments["jobId"];
            return ToolJobManager.TryGet(id, out var job)
                ? ToolResult.Ok($"tool job {job.Status}", job.ToJson())
                : ToolResult.Error("JOB_NOT_FOUND", "Unity MCP tool job not found: " + id);
        }
    }

    public sealed class WaitToolJobTool : IMcpTool
    {
        public string Name => "wait_tool_job";
        public string Description => "Waits cooperatively, without holding Unity's main thread, until any Unity MCP background job becomes terminal or the bounded wait expires.";
        public JObject InputSchema => ToolJobSchemas.Input(true);
        public JObject OutputSchema => ToolJobSchemas.Output;
        public ToolDescriptor Descriptor => ToolJobSchemas.WaitDescriptor(Name);
        public bool IsWrite => false;

        public ToolResult Execute(JObject arguments) =>
            throw new NotSupportedException("wait_tool_job uses asynchronous execution");

        public async Task<ToolResult> ExecuteAsync(JObject arguments)
        {
            var id = (string)arguments["jobId"];
            var seconds = Math.Max(0.1d, Math.Min(300d, (double?)arguments["timeoutSeconds"] ?? 30d));
            var context = ToolExecutionContext.Current;
            var record = await ToolJobManager.WaitAsync(
                id,
                TimeSpan.FromSeconds(seconds),
                context != null ? context.CancellationToken : default);
            return record != null
                ? ToolResult.Ok($"tool job {record.Status}", record.ToJson())
                : ToolResult.Error("JOB_NOT_FOUND", "Unity MCP tool job not found: " + id);
        }
    }

    public sealed class CancelToolJobTool : IMcpTool
    {
        public string Name => "cancel_tool_job";
        public string Description => "Requests cooperative cancellation of any cancellable Unity MCP background job.";
        public JObject InputSchema => ToolJobSchemas.Input(false);
        public JObject OutputSchema => ToolJobSchemas.Output;
        public ToolDescriptor Descriptor => ToolJobSchemas.CancelDescriptor(Name);
        public bool IsWrite => true;

        public ToolResult Execute(JObject arguments)
        {
            var id = (string)arguments["jobId"];
            if (!ToolJobManager.TryGet(id, out var existing))
                return ToolResult.Error("JOB_NOT_FOUND", "Unity MCP tool job not found: " + id);
            if (!ToolJobManager.Cancel(id, out var job))
            {
                var code = existing.CanCancel ? "JOB_NOT_CANCELLABLE" : "JOB_CANNOT_CANCEL";
                return ToolResult.Error(code, "Unity MCP tool job cannot be cancelled: " + id, existing.ToJson());
            }
            return ToolResult.Ok("tool job cancellation requested", job.ToJson());
        }
    }
}
