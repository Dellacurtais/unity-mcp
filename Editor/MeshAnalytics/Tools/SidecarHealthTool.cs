using System;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Tools
{
    public sealed class SidecarHealthTool : IMcpTool
    {
        public string Name => "mesh_sidecar_health";
        public string Description =>
            "Pings the local Python mesh-analytics sidecar and returns its version and supported methods. " +
            "Only loopback HTTP URLs are accepted; requests are cooperatively cancellable.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["sidecarUrl"] = new JObject { ["type"] = "string", ["default"] = MeshSidecarClient.DefaultUrl },
                ["timeoutSeconds"] = new JObject { ["type"] = "integer", ["default"] = 5, ["minimum"] = 1, ["maximum"] = 60 },
            },
            ["additionalProperties"] = false,
        };

        public JObject OutputSchema => MeshToolSchemas.SidecarHealthOutput;

        public bool IsWrite => false;

        public ToolResult Execute(JObject args) => ToolResult.Error(
            "ASYNC_REQUIRED", "mesh_sidecar_health must run through ExecuteAsync.");

        public async Task<ToolResult> ExecuteAsync(JObject args)
        {
            var url = (string)args["sidecarUrl"] ?? MeshSidecarClient.DefaultUrl;
            var timeout = (int?)args["timeoutSeconds"] ?? 5;
            var context = ToolExecutionContext.Current;
            var cancellationToken = context != null ? context.CancellationToken : default;

            try
            {
                var health = await MeshSidecarClient.HealthAsync(url, timeout, cancellationToken);
                if (!health.Ok)
                {
                    return ToolResult.Error(health.ErrorCode ?? "SIDECAR_UNREACHABLE",
                        $"sidecar at {url} did not respond: {health.Error}. " +
                        "Start it with `uvicorn main:app --host 127.0.0.1 --port 8766` from Sidecar~/.");
                }

                var methods = await MeshSidecarClient.ListMethodsAsync(url, timeout, cancellationToken);
                var methodsJson = new JArray();
                foreach (var method in methods)
                {
                    methodsJson.Add(new JObject
                    {
                        ["name"] = method.Name,
                        ["description"] = method.Description,
                        ["producesLabels"] = method.ProducesLabels,
                    });
                }

                var payload = new JObject
                {
                    ["url"] = url,
                    ["service"] = health.Service,
                    ["version"] = health.Version,
                    ["methods"] = methodsJson,
                };
                return ToolResult.Ok(
                    $"sidecar '{health.Service}' v{health.Version} reachable at {url}; {methods.Count} method(s) available",
                    payload);
            }
            catch (OperationCanceledException)
            {
                return ToolResult.Error("CANCELLED", "mesh sidecar health request was cancelled.");
            }
            catch (Exception exception)
            {
                return ToolResult.Error("SIDECAR_ERROR", exception.Message);
            }
        }
    }
}
