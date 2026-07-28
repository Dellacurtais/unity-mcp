using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    public sealed class McpToolRegistry
    {
        private readonly Dictionary<string, IMcpTool> _tools = new Dictionary<string, IMcpTool>(StringComparer.Ordinal);
        private readonly Dictionary<string, ToolDescriptor> _descriptors = new Dictionary<string, ToolDescriptor>(StringComparer.Ordinal);
        private readonly object _lock = new object();
        private string _versionHash;

        public event Action Changed;

        public void Register(IMcpTool tool) => RegisterRange(new[] { tool });

        public void RegisterRange(IEnumerable<IMcpTool> tools)
        {
            if (tools == null) throw new ArgumentNullException(nameof(tools));
            var candidates = tools.ToArray();
            if (candidates.Length == 0) return;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in candidates)
            {
                if (tool == null) throw new ArgumentNullException(nameof(tools), "MCP tool collection contains null.");
                if (string.IsNullOrWhiteSpace(tool.Name)) throw new ArgumentException("MCP tool name is required.", nameof(tools));
                if (!names.Add(tool.Name)) throw new InvalidOperationException($"Duplicate MCP tool name '{tool.Name}' in registration batch.");
                var inputSchema = tool.InputSchema ?? new Newtonsoft.Json.Linq.JObject { ["type"] = "object" };
                if (!string.Equals((string)inputSchema["type"], "object", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Invalid input schema for MCP tool '{tool.Name}': root type must be 'object'.");
                if (!ToolSchemaValidator.ValidateSchema(inputSchema, out var schemaError))
                    throw new InvalidOperationException($"Invalid input schema for MCP tool '{tool.Name}': {schemaError}");
                if (tool.OutputSchema != null && !ToolSchemaValidator.ValidateSchema(tool.OutputSchema, out var outputSchemaError))
                    throw new InvalidOperationException($"Invalid output schema for MCP tool '{tool.Name}': {outputSchemaError}");
                var descriptor = tool.Descriptor ?? ToolDescriptor.Infer(tool);
                if (!string.Equals(descriptor.Name, tool.Name, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Descriptor name '{descriptor.Name}' does not match tool '{tool.Name}'.");
            }

            lock (_lock)
            {
                foreach (var name in names)
                    if (_tools.ContainsKey(name)) throw new InvalidOperationException($"Duplicate MCP tool name '{name}'.");
                foreach (var tool in candidates)
                {
                    _tools.Add(tool.Name, tool);
                    _descriptors[tool.Name] = tool.Descriptor ?? ToolDescriptor.Infer(tool);
                }
                _versionHash = null;
            }
            Changed?.Invoke();
        }

        public void RegisterDescriptor(ToolDescriptor descriptor)
        {
            if (descriptor == null || string.IsNullOrEmpty(descriptor.Name)) throw new ArgumentException("Descriptor name is required.", nameof(descriptor));
            lock (_lock)
            {
                if (!_tools.TryGetValue(descriptor.Name, out var tool))
                    throw new InvalidOperationException($"Cannot register a descriptor for unknown MCP tool '{descriptor.Name}'.");
                if (tool.IsWrite != descriptor.IsWrite)
                    throw new InvalidOperationException($"Descriptor mutability does not match MCP tool '{descriptor.Name}'.");
                if (descriptor.OutputSchema != null && !ToolSchemaValidator.ValidateSchema(descriptor.OutputSchema, out var outputSchemaError))
                    throw new InvalidOperationException($"Invalid descriptor output schema for MCP tool '{descriptor.Name}': {outputSchemaError}");
                _descriptors[descriptor.Name] = descriptor;
                _versionHash = null;
            }
            Changed?.Invoke();
        }

        public bool TryGet(string name, out IMcpTool tool)
        {
            lock (_lock) return _tools.TryGetValue(name ?? string.Empty, out tool);
        }

        public ToolDescriptor GetDescriptor(string name)
        {
            lock (_lock)
            {
                _descriptors.TryGetValue(name ?? string.Empty, out var descriptor);
                return descriptor;
            }
        }

        public IEnumerable<IMcpTool> All
        {
            get
            {
                lock (_lock) return _tools.Values.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
            }
        }

        public int Count { get { lock (_lock) return _tools.Count; } }

        public string VersionHash
        {
            get
            {
                lock (_lock)
                {
                    if (_versionHash != null) return _versionHash;
                    var canonical = string.Join("\n", _tools.Values
                        .OrderBy(tool => tool.Name, StringComparer.Ordinal)
                        .Select(tool =>
                        {
                            _descriptors.TryGetValue(tool.Name, out var descriptor);
                            descriptor = descriptor ?? ToolDescriptor.Infer(tool);
                            return string.Join("|", new[]
                            {
                                tool.Name,
                                tool.InputSchema?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}",
                                tool.OutputSchema?.ToString(Newtonsoft.Json.Formatting.None) ?? "null",
                                descriptor.OutputSchema?.ToString(Newtonsoft.Json.Formatting.None) ?? "null",
                                descriptor.Category ?? string.Empty,
                                descriptor.Capabilities.ToString(),
                                descriptor.Risk.ToString(),
                                descriptor.ConfirmationPolicy.ToString(),
                                descriptor.EditorMode.ToString(),
                                descriptor.IsIdempotent ? "1" : "0",
                            });
                        }));
                    using (var sha = SHA256.Create())
                    {
                        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                        _versionHash = BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
                    }
                    return _versionHash;
                }
            }
        }
    }
}
