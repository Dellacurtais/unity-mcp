using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class ApplyChangesetTool : IMcpTool
    {
        public string Name => "apply_changeset";
        public string Description =>
            "Applies an array of write operations atomically as ONE Undo group. Each op is { kind, args } where kind is create_primitive, instantiate_prefab, create_light, set_transform, parent_object, add_component, set_property, or delete_object. Use preview=true for a dry-run that returns the planned ops without mutating the scene. On failure mid-batch, reverts all applied ops.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["name"] = new JObject { ["type"] = "string", ["description"] = "Undo group label." },
                ["preview"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["ops"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["kind"] = new JObject { ["type"] = "string" },
                            ["args"] = new JObject { ["type"] = "object" },
                        },
                        ["required"] = new JArray { "kind" },
                    },
                },
            },
            ["required"] = new JArray { "ops" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var name = (string)args["name"] ?? "Apply Changeset";
            var preview = (bool?)args["preview"] ?? false;
            var ops = args["ops"] as JArray ?? new JArray();

            if (!preview && ops.Count > Settings.MaxBatchOps)
                return ToolResult.Error("BATCH_TOO_LARGE",
                    $"Batch has {ops.Count} ops; limit is {Settings.MaxBatchOps}. Split or raise the limit in the panel.");

            var report = ChangesetExecutor.Apply(name, ops, preview);
            var resultsArr = new JArray();
            foreach (var r in report.Results)
            {
                resultsArr.Add(new JObject
                {
                    ["ok"] = r.Ok,
                    ["instanceId"] = r.InstanceID,
                    ["summary"] = r.Summary,
                    ["error"] = r.Error,
                });
            }

            var payload = new JObject
            {
                ["preview"] = preview,
                ["failed"] = report.Failed,
                ["error"] = report.Error,
                ["failedAtIndex"] = report.FailedAtIndex,
                ["opsApplied"] = report.OpsApplied,
                ["results"] = resultsArr,
            };

            if (report.Failed)
                return new ToolResult { IsError = true, Text = $"changeset failed: {report.Error}", Json = payload };

            var verb = preview ? "would apply" : "applied";
            return ToolResult.Ok($"{verb} {ops.Count} ops as undo group '{name}'", payload);
        }
    }
}
