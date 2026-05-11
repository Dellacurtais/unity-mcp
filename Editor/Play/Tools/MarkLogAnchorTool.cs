using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Play.Tools
{
    public sealed class MarkLogAnchorTool : IMcpTool
    {
        public string Name => "mark_log_anchor";
        public string Description =>
            "Returns ConsoleCapture.CurrentSeq as an anchor — pass it as `sinceSeq` to read_runtime_log_delta later to fetch only console entries that arrived after this point. " +
            "Cheap (no work — just reads the counter). Call before any action whose log delta you want isolated.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            int seq = ConsoleCapture.CurrentSeq;
            return ToolResult.Ok($"anchor seq={seq}", new JObject { ["seq"] = seq });
        }
    }
}
