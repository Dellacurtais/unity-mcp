using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    public interface IMcpTool
    {
        string Name { get; }
        string Description { get; }
        JObject InputSchema { get; }
        bool IsWrite { get; }

        ToolDescriptor Descriptor => ToolDescriptor.Infer(this);
        JObject OutputSchema => ToolOutputContracts.For(Name);

        ToolResult Execute(JObject arguments);

        Task<ToolResult> ExecuteAsync(JObject arguments)
        {
            var context = ToolExecutionContext.Current;
            return MainThreadDispatcher.RunAsync(
                () => Execute(arguments),
                context != null ? context.CancellationToken : default);
        }
    }

    public sealed class ToolResult
    {
        public bool IsError;
        public string Text;
        public JToken Json;
        public JArray ContentBlocks;
        public JObject Metadata;
        public JArray Warnings;

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
