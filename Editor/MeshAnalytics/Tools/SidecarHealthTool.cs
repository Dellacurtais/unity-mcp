using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.MeshAnalytics.Core;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.MeshAnalytics.Tools
{
    /// Quick connectivity check for the Python mesh-analytics sidecar.
    /// Returns the service status, version, and list of supported
    /// segmentation methods. Call this before `mesh_segment_via_sidecar`
    /// when the user isn't sure the service is up — the error from
    /// segment-on-missing-service includes start-up instructions, but a
    /// proactive ping is cheaper than a failed segment call.
    public sealed class SidecarHealthTool : IMcpTool
    {
        public string Name => "mesh_sidecar_health";
        public string Description =>
            "Pings the Python mesh-analytics sidecar at the configured URL (default 127.0.0.1:8766) and returns its status, version, and supported methods. Use to verify the sidecar is running before calling mesh_segment_via_sidecar — if it isn't, the response includes the command to start it.";

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

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            string url = (string)args["sidecarUrl"] ?? MeshSidecarClient.DefaultUrl;
            int timeout = (int?)args["timeoutSeconds"] ?? 5;

            var health = MeshSidecarClient.Health(url, timeout);
            if (!health.Ok)
            {
                return ToolResult.Error("SIDECAR_UNREACHABLE",
                    $"sidecar at {url} did not respond: {health.Error}. " +
                    $"Start it with `uvicorn main:app --host 127.0.0.1 --port 8766` from " +
                    $"`extensions/unity-scene-analyzer/Sidecar~/` (see README.md there for full setup).");
            }

            var methods = MeshSidecarClient.ListMethods(url, timeout);
            var methodsJson = new JArray();
            if (methods != null)
            {
                foreach (var m in methods)
                {
                    methodsJson.Add(new JObject
                    {
                        ["name"] = m.Name,
                        ["description"] = m.Description,
                        ["producesLabels"] = m.ProducesLabels,
                    });
                }
            }
            var payload = new JObject
            {
                ["url"] = url,
                ["service"] = health.Service,
                ["version"] = health.Version,
                ["methods"] = methodsJson,
            };
            return ToolResult.Ok(
                $"sidecar '{health.Service}' v{health.Version} reachable at {url}; " +
                $"{(methods?.Count ?? 0)} method(s) available",
                payload);
        }
    }
}
