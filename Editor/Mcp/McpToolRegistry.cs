using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    public sealed class McpToolRegistry
    {
        private sealed class ContractSnapshot
        {
            public string InputSchemaJson;
            public string OutputSchemaJson;
            public string Fingerprint;
        }

        private sealed class PreparedTool
        {
            public IMcpTool Tool;
            public ToolDescriptor Descriptor;
            public ContractSnapshot Contract;
        }

        private readonly Dictionary<string, IMcpTool> _tools = new Dictionary<string, IMcpTool>(StringComparer.Ordinal);
        private readonly Dictionary<string, ToolDescriptor> _descriptors = new Dictionary<string, ToolDescriptor>(StringComparer.Ordinal);
        private readonly Dictionary<string, ContractSnapshot> _contracts = new Dictionary<string, ContractSnapshot>(StringComparer.Ordinal);
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
            var prepared = new List<PreparedTool>(candidates.Length);
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
                var outputSchema = tool.OutputSchema;
                if (outputSchema != null && !ToolSchemaValidator.ValidateSchema(outputSchema, out var outputSchemaError))
                    throw new InvalidOperationException($"Invalid output schema for MCP tool '{tool.Name}': {outputSchemaError}");
                var descriptor = tool.Descriptor ?? ToolDescriptor.Infer(tool);
                if (!string.Equals(descriptor.Name, tool.Name, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Descriptor name '{descriptor.Name}' does not match tool '{tool.Name}'.");
                prepared.Add(new PreparedTool
                {
                    Tool = tool,
                    Descriptor = descriptor,
                    Contract = CaptureContract(tool.Name, inputSchema, outputSchema, descriptor),
                });
            }

            lock (_lock)
            {
                foreach (var name in names)
                    if (_tools.ContainsKey(name)) throw new InvalidOperationException($"Duplicate MCP tool name '{name}'.");
                foreach (var item in prepared)
                {
                    _tools.Add(item.Tool.Name, item.Tool);
                    _descriptors[item.Tool.Name] = item.Descriptor;
                    _contracts[item.Tool.Name] = item.Contract;
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
                if (!_contracts.TryGetValue(descriptor.Name, out var existingContract))
                    throw new InvalidOperationException($"Contract snapshot is missing for MCP tool '{descriptor.Name}'.");
                _descriptors[descriptor.Name] = descriptor;
                _contracts[descriptor.Name] = new ContractSnapshot
                {
                    InputSchemaJson = existingContract.InputSchemaJson,
                    OutputSchemaJson = existingContract.OutputSchemaJson,
                    Fingerprint = BuildContractFingerprint(
                        descriptor.Name, existingContract.InputSchemaJson, existingContract.OutputSchemaJson, descriptor),
                };
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

        public Newtonsoft.Json.Linq.JObject GetInputSchema(string name)
        {
            string json;
            lock (_lock)
            {
                if (!_contracts.TryGetValue(name ?? string.Empty, out var contract)) return null;
                json = contract.InputSchemaJson;
            }
            return Newtonsoft.Json.Linq.JObject.Parse(json);
        }

        public Newtonsoft.Json.Linq.JObject GetOutputSchema(string name)
        {
            string json;
            lock (_lock)
            {
                if (!_contracts.TryGetValue(name ?? string.Empty, out var contract)
                    || string.Equals(contract.OutputSchemaJson, "null", StringComparison.Ordinal)) return null;
                json = contract.OutputSchemaJson;
            }
            return Newtonsoft.Json.Linq.JObject.Parse(json);
        }

        public IEnumerable<IMcpTool> All
        {
            get
            {
                lock (_lock) return _tools.Values.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
            }
        }

        public int Count { get { lock (_lock) return _tools.Count; } }

        private static ContractSnapshot CaptureContract(
            string name, Newtonsoft.Json.Linq.JObject inputSchema, Newtonsoft.Json.Linq.JObject outputSchema, ToolDescriptor descriptor)
        {
            var inputSchemaJson = inputSchema?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}";
            var outputSchemaJson = outputSchema?.ToString(Newtonsoft.Json.Formatting.None) ?? "null";
            return new ContractSnapshot
            {
                InputSchemaJson = inputSchemaJson,
                OutputSchemaJson = outputSchemaJson,
                Fingerprint = BuildContractFingerprint(name, inputSchemaJson, outputSchemaJson, descriptor),
            };
        }

        private static string BuildContractFingerprint(
            string name, string inputSchemaJson, string outputSchemaJson, ToolDescriptor descriptor)
            => string.Join("|", new[]
            {
                name,
                inputSchemaJson,
                outputSchemaJson,
                descriptor?.OutputSchema?.ToString(Newtonsoft.Json.Formatting.None) ?? "null",
                descriptor?.Category ?? string.Empty,
                descriptor?.Capabilities.ToString() ?? string.Empty,
                descriptor?.Risk.ToString() ?? string.Empty,
                descriptor?.ConfirmationPolicy.ToString() ?? string.Empty,
                descriptor?.EditorMode.ToString() ?? string.Empty,
                descriptor != null && descriptor.IsIdempotent ? "1" : "0",
                descriptor?.ExecutionRequirements.ToString() ?? string.Empty,
                descriptor != null && descriptor.HeadlessSafe ? "1" : "0",
            });

        public string VersionHash
        {
            get
            {
                lock (_lock)
                {
                    if (_versionHash != null) return _versionHash;
                    var canonical = string.Join("\n", _contracts
                        .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                        .Select(entry => entry.Value.Fingerprint));
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
