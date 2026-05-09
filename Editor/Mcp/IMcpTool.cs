using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    /// Contract for an MCP tool exposed by the plugin. Implementations describe
    /// themselves via Name/Description/InputSchema and are invoked synchronously
    /// on the editor main thread.
    public interface IMcpTool
    {
        string Name { get; }
        string Description { get; }
        JObject InputSchema { get; }
        bool IsWrite { get; }

        ToolResult Execute(JObject arguments);
    }

    public sealed class ToolResult
    {
        public bool IsError;
        public string Text;
        public JToken Json;

        /// When set, overrides the default content layout and is sent verbatim
        /// as the tool call's `content` array. Use for non-text outputs (image,
        /// resource) that need explicit MCP content-block typing.
        public JArray ContentBlocks;

        public static ToolResult Ok(string text, JToken json = null)
            => new ToolResult { Text = text, Json = json };

        public static ToolResult Error(string code, string message, JToken data = null)
        {
            var payload = new JObject { ["code"] = code, ["message"] = message };
            if (data != null) payload["data"] = data;
            return new ToolResult { IsError = true, Text = $"[{code}] {message}", Json = payload };
        }

        public static ToolResult Image(string base64, string mimeType, string textSummary)
        {
            return new ToolResult
            {
                Text = textSummary,
                ContentBlocks = new JArray
                {
                    new JObject { ["type"] = "text", ["text"] = textSummary },
                    new JObject { ["type"] = "image", ["data"] = base64, ["mimeType"] = mimeType },
                },
            };
        }
    }
}
