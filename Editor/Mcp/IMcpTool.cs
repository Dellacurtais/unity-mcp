using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    /// Contract for an MCP tool exposed by the plugin. Implementations describe
    /// themselves via Name/Description/InputSchema. Tools normally implement
    /// the synchronous Execute (the framework dispatches it onto the editor
    /// main thread). Tools that need cooperative async — wait for a Unity
    /// callback without blocking the main thread — override ExecuteAsync
    /// directly so the dispatcher pump stays responsive while they wait.
    public interface IMcpTool
    {
        string Name { get; }
        string Description { get; }
        JObject InputSchema { get; }
        bool IsWrite { get; }

        ToolResult Execute(JObject arguments);

        /// Default: run the sync `Execute` on the editor main thread via the
        /// dispatcher. Override when the tool needs to await something that
        /// would otherwise block the main thread (e.g. polling
        /// `EditorApplication.isCompiling` after `AssetDatabase.Refresh`).
        Task<ToolResult> ExecuteAsync(JObject arguments)
            => MainThreadDispatcher.RunAsync(() => Execute(arguments));
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
