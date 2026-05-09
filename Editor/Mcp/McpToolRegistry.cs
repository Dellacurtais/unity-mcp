using System.Collections.Generic;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    public sealed class McpToolRegistry
    {
        private readonly Dictionary<string, IMcpTool> _tools = new();

        public void Register(IMcpTool tool) => _tools[tool.Name] = tool;

        public bool TryGet(string name, out IMcpTool tool) => _tools.TryGetValue(name, out tool);

        public IEnumerable<IMcpTool> All => _tools.Values;
    }
}
