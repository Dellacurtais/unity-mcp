using System.Linq;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class ApplyChangesetTool : IMcpTool
    {
        public string Name => "apply_changeset";
        public string Description
        {
            get
            {
                var kinds = string.Join(", ", ChangesetExecutor.KnownKinds.OrderBy(k => k));
                return "Applies write operations atomically as ONE Undo group. Each op: { kind, args, label? }. " +
                       "preview=true validates kind, label refs, and instanceIds without mutating state. " +
                       "On failure mid-batch, reverts scene mutations AND deletes any newly-created assets. " +
                       $"Known kinds ({ChangesetExecutor.KnownKinds.Count}): {kinds}.";
            }
        }

        public JObject InputSchema
        {
            get
            {
                var kindEnum = new JArray(ChangesetExecutor.KnownKinds.OrderBy(k => k).Cast<object>().ToArray());
                return new JObject
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
                                    ["kind"] = new JObject
                                    {
                                        ["type"] = "string",
                                        ["enum"] = kindEnum,
                                    },
                                    ["args"] = new JObject { ["type"] = "object" },
                                    ["label"] = new JObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "Optional name; later ops can use { ref: \"<label>\" } to reference this op's instanceId.",
                                    },
                                },
                                ["required"] = new JArray { "kind" },
                            },
                        },
                    },
                    ["required"] = new JArray { "ops" },
                    ["additionalProperties"] = false,
                };
            }
        }

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
                var entry = new JObject
                {
                    ["ok"] = r.Ok,
                    ["instanceId"] = r.InstanceID,
                    ["summary"] = r.Summary,
                    ["error"] = r.Error,
                };
                if (!string.IsNullOrEmpty(r.ExceptionType)) entry["exceptionType"] = r.ExceptionType;
                if (!string.IsNullOrEmpty(r.CreatedAssetPath)) entry["createdAssetPath"] = r.CreatedAssetPath;
                resultsArr.Add(entry);
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
