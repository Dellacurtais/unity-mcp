using System;
using System.Collections.Generic;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp.Jobs;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    public static class SceneAnalyzerVersion
    {
        public const string Fallback = "0.16.16";
        private static string _current;
        private static string _packageName;

        public static string Current => string.IsNullOrEmpty(_current) ? Fallback : _current;
        public static string PackageName => string.IsNullOrEmpty(_packageName) ? "com.maicon.scene-analyzer" : _packageName;

        public static void Initialize()
        {
            try
            {
                var package = PackageInfo.FindForAssembly(typeof(SceneAnalyzerVersion).Assembly);
                _current = string.IsNullOrWhiteSpace(package?.version) ? Fallback : package.version;
                _packageName = string.IsNullOrWhiteSpace(package?.name) ? "com.maicon.scene-analyzer" : package.name;
            }
            catch
            {
                _current = Fallback;
                _packageName = "com.maicon.scene-analyzer";
            }
        }
    }

    public static class OperationalReport
    {
        public static JObject Inventory(McpToolRegistry registry, JObject arguments = null)
        {
            arguments = arguments ?? new JObject();
            var offset = Math.Max(0, (int?)arguments["offset"] ?? 0);
            var limit = Math.Max(1, Math.Min(1000, (int?)arguments["limit"] ?? 250));
            var includeSchemas = (bool?)arguments["includeSchemas"] ?? false;
            var moduleFilter = (string)arguments["module"];
            var categoryFilter = (string)arguments["category"];
            var riskFilter = (string)arguments["risk"];
            var editorModeFilter = (string)arguments["editorMode"];
            var confirmationFilter = (bool?)arguments["confirmationRequired"];
            var writeFilter = (bool?)arguments["writeOnly"];
            var capabilityFilter = ParseCapability((string)arguments["capability"]);

            var moduleByTool = OptionalModuleRegistry.All
                .SelectMany(module => (module.Tools ?? Array.Empty<string>()).Select(tool => new { tool, module.Id }))
                .GroupBy(item => item.tool, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.Ordinal);
            var compatibilityByModule = OptionalModuleRegistry.CompatibilitySnapshot()
                .OfType<JObject>()
                .ToDictionary(item => (string)item["id"], item => item, StringComparer.Ordinal);
            var rows = new List<JObject>();
            foreach (var tool in registry?.All ?? Array.Empty<IMcpTool>())
            {
                var descriptor = registry.GetDescriptor(tool.Name) ?? ToolDescriptor.Infer(tool);
                var module = moduleByTool.TryGetValue(tool.Name, out var knownModule)
                    ? knownModule
                    : ModuleFromType(tool.GetType());
                var confirmationRequired = ToolAuthorization.RequiresConfirmation(descriptor);
                var previewTool = PreviewToolFor(tool.Name, registry);
                if (!Matches(module, moduleFilter) || !Matches(descriptor.Category, categoryFilter)) continue;
                if (!Matches(descriptor.Risk.ToString(), riskFilter) || !Matches(descriptor.EditorMode.ToString(), editorModeFilter)) continue;
                if (confirmationFilter.HasValue && confirmationFilter.Value != confirmationRequired) continue;
                if (writeFilter == true && !descriptor.IsWrite) continue;
                if (capabilityFilter != McpCapability.None && (descriptor.Capabilities & capabilityFilter) != capabilityFilter) continue;

                var row = new JObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["module"] = module,
                    ["assembly"] = tool.GetType().Assembly.GetName().Name,
                    ["category"] = descriptor.Category,
                    ["capabilities"] = CapabilityNames(descriptor.Capabilities),
                    ["risk"] = descriptor.Risk.ToString(),
                    ["editorMode"] = descriptor.EditorMode.ToString(),
                    ["isWrite"] = descriptor.IsWrite,
                    ["isIdempotent"] = descriptor.IsIdempotent,
                    ["confirmationPolicy"] = descriptor.ConfirmationPolicy.ToString(),
                    ["confirmationRequired"] = confirmationRequired,
                    ["previewAvailable"] = previewTool != null || confirmationRequired,
                    ["previewTool"] = previewTool,
                    ["confirmationPreview"] = confirmationRequired,
                    ["supportedUnity"] = "6000.1-6000.3",
                };
                if (compatibilityByModule.TryGetValue(module, out var moduleCompatibility))
                    row["packageCompatibility"] = moduleCompatibility.DeepClone();
                if (includeSchemas)
                {
                    row["inputSchema"] = tool.InputSchema?.DeepClone();
                    if (tool.OutputSchema != null) row["outputSchema"] = tool.OutputSchema.DeepClone();
                }
                rows.Add(row);
            }

            var ordered = rows
                .OrderBy(row => (string)row["module"], StringComparer.Ordinal)
                .ThenBy(row => (string)row["name"], StringComparer.Ordinal)
                .ToArray();
            var page = new JArray(ordered.Skip(offset).Take(limit));
            return new JObject
            {
                ["packageVersion"] = SceneAnalyzerVersion.Current,
                ["registryCount"] = registry?.Count ?? 0,
                ["registryHash"] = registry?.VersionHash,
                ["matched"] = ordered.Length,
                ["offset"] = offset,
                ["returned"] = page.Count,
                ["hasMore"] = offset + page.Count < ordered.Length,
                ["tools"] = page,
                ["loadedModules"] = OptionalModuleRegistry.Snapshot(),
            };
        }

        public static JObject Health()
        {
            var registry = Bootstrap.ToolRegistry;
            var jobs = ToolJobManager.List(null, 200);
            var jobCounts = new JObject();
            foreach (var status in Enum.GetNames(typeof(ToolJobStatus)))
                jobCounts[status] = jobs.Count(job => string.Equals((string)job["status"], status, StringComparison.Ordinal));
            var compileEntries = ConsoleCapture.Snapshot(null, LogType.Warning, null, 1000)
                .Where(entry => entry.IsCompile)
                .ToArray();
            var compatibility = OptionalModuleRegistry.CompatibilitySnapshot();
            var compatibilityIssues = compatibility.Count(item =>
                string.Equals((string)item["status"], "loaded_incompatible", StringComparison.Ordinal)
                || string.Equals((string)item["status"], "available_not_loaded", StringComparison.Ordinal)
                || string.Equals((string)item["status"], "version_unsupported", StringComparison.Ordinal));
            var selfTest = SelfTest();
            var compileErrors = compileEntries.Count(entry => entry.Severity == LogType.Error || entry.Severity == LogType.Exception);
            var loadedUtc = Bootstrap.DomainLoadedUtc;
            var bridge = Bootstrap.BrokerBridgeState;
            return new JObject
            {
                ["status"] = registry != null && compatibilityIssues == 0 && compileErrors == 0 && (bool)selfTest["valid"]
                    ? "ok"
                    : "degraded",
                ["package"] = new JObject
                {
                    ["name"] = SceneAnalyzerVersion.PackageName,
                    ["version"] = SceneAnalyzerVersion.Current,
                    ["unityVersion"] = Application.unityVersion,
                    ["supportedUnity"] = "6000.1-6000.3",
                },
                ["transport"] = new JObject
                {
                    ["mode"] = Settings.TransportMode.ToString(),
                    ["embeddedRunning"] = Bootstrap.Server?.IsRunning ?? false,
                    ["embeddedPort"] = Bootstrap.Server?.Port ?? Settings.Port,
                    ["brokerUrl"] = Settings.BrokerUrl,
                    ["bridgeConnected"] = bridge.IsConnected,
                    ["bridgeState"] = bridge.State,
                    ["bridgeError"] = bridge.Error,
                    ["projectKey"] = bridge.ProjectKey,
                    ["processId"] = bridge.ProcessId,
                    ["generationId"] = bridge.GenerationId,
                    ["registryHash"] = bridge.RegistryHash,
                    ["updatedUtc"] = bridge.UpdatedUtc == DateTimeOffset.MinValue
                        ? null
                        : bridge.UpdatedUtc.ToString("O"),
                },
                ["server"] = new JObject
                {
                    ["running"] = Bootstrap.Server?.IsRunning ?? false,
                    ["version"] = HttpMcpServer.ServerVersion,
                    ["port"] = Bootstrap.Server?.Port ?? Settings.Port,
                    ["protocolPreferred"] = McpProtocolNegotiator.Preferred,
                    ["protocolSupported"] = new JArray(McpProtocolNegotiator.Supported),
                    ["inFlightRequests"] = Bootstrap.Server?.InFlightCount ?? 0,
                    ["dispatcherQueueDepth"] = MainThreadDispatcher.PendingCount,
                    ["maxConcurrentRequests"] = Settings.MaxConcurrentRequests,
                    ["maxRequestBodyBytes"] = Settings.MaxRequestBodyBytes,
                    ["toolTimeoutSeconds"] = Settings.ToolTimeoutSeconds,
                },
                ["registry"] = new JObject
                {
                    ["count"] = registry?.Count ?? 0,
                    ["hash"] = registry?.VersionHash,
                    ["loadedModuleCount"] = OptionalModuleRegistry.All.Length,
                },
                ["compilation"] = new JObject
                {
                    ["isCompiling"] = EditorApplication.isCompiling,
                    ["isUpdating"] = EditorApplication.isUpdating,
                    ["diagnosticCount"] = compileEntries.Length,
                    ["errorCount"] = compileErrors,
                    ["warningCount"] = compileEntries.Count(entry => entry.Severity == LogType.Warning),
                    ["consoleSequence"] = ConsoleCapture.CurrentSeq,
                    ["consoleBufferSize"] = ConsoleCapture.BufferSize,
                },
                ["jobs"] = new JObject
                {
                    ["retained"] = jobs.Count,
                    ["counts"] = jobCounts,
                    ["recent"] = new JArray(jobs.Take(20).Select(job => new JObject
                    {
                        ["jobId"] = job["jobId"],
                        ["kind"] = job["kind"],
                        ["status"] = job["status"],
                        ["progress"] = job["progress"],
                        ["message"] = job["message"],
                        ["createdUtc"] = job["createdUtc"],
                        ["startedUtc"] = job["startedUtc"],
                        ["completedUtc"] = job["completedUtc"],
                        ["canCancel"] = job["canCancel"],
                    })), 
                },
                ["domain"] = new JObject
                {
                    ["loadedUtc"] = loadedUtc.ToString("O"),
                    ["uptimeSeconds"] = Math.Max(0d, (DateTimeOffset.UtcNow - loadedUtc).TotalSeconds),
                },
                ["capabilities"] = CapabilitySettings(),
                ["selfTest"] = new JObject
                {
                    ["valid"] = selfTest["valid"],
                    ["checks"] = selfTest["checks"],
                    ["errors"] = selfTest["errors"],
                    ["warnings"] = selfTest["warnings"],
                },
                ["loadedModules"] = OptionalModuleRegistry.Snapshot(),
                ["packageCompatibility"] = compatibility,
            };
        }

        public static JObject SelfTest()
        {
            var issues = new JArray();
            var checks = 0;
            var registry = Bootstrap.ToolRegistry;
            Check(registry != null, "REGISTRY_UNAVAILABLE", "MCP tool registry is not initialized.", issues, ref checks);
            if (registry != null)
            {
                var tools = registry.All.ToArray();
                Check(tools.Length == registry.Count, "REGISTRY_COUNT_MISMATCH", "Registry enumeration count does not match Count.", issues, ref checks);
                Check(tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() == tools.Length,
                    "DUPLICATE_TOOL_NAME", "Registry contains duplicate tool names.", issues, ref checks);
                foreach (var tool in tools)
                {
                    checks++;
                    if (!ToolSchemaValidator.ValidateSchema(tool.InputSchema ?? new JObject { ["type"] = "object" }, out var schemaError))
                        Issue("error", "INVALID_INPUT_SCHEMA", $"{tool.Name}: {schemaError}", issues);
                    if (tool.OutputSchema != null && !ToolSchemaValidator.ValidateSchema(tool.OutputSchema, out var outputError))
                        Issue("error", "INVALID_OUTPUT_SCHEMA", $"{tool.Name}: {outputError}", issues);
                    var descriptor = registry.GetDescriptor(tool.Name);
                    if (descriptor == null)
                    {
                        Issue("error", "DESCRIPTOR_MISSING", tool.Name, issues);
                        continue;
                    }
                    if (!string.Equals(descriptor.Name, tool.Name, StringComparison.Ordinal))
                        Issue("error", "DESCRIPTOR_NAME_MISMATCH", $"{tool.Name}: {descriptor.Name}", issues);
                    if (tool.IsWrite != descriptor.IsWrite)
                        Issue("error", "MUTABILITY_MISMATCH", $"{tool.Name}: tool.IsWrite={tool.IsWrite}, descriptor.IsWrite={descriptor.IsWrite}", issues);
                    var highRisk = McpCapability.Destructive | McpCapability.Reflection | McpCapability.PackageManager | McpCapability.Build;
                    if ((descriptor.Capabilities & highRisk) != 0 && descriptor.ConfirmationPolicy != McpConfirmationPolicy.Required)
                        Issue("error", "CONFIRMATION_POLICY_MISSING", tool.Name, issues);
                }

                foreach (var module in OptionalModuleRegistry.All)
                {
                    checks++;
                    foreach (var toolName in module.Tools ?? Array.Empty<string>())
                        if (!registry.TryGet(toolName, out _))
                            Issue("error", "MODULE_TOOL_MISSING", $"{module.Id}: {toolName}", issues);
                }
                Check(registry.VersionHash?.Length == 64, "REGISTRY_HASH_INVALID", "Registry hash must be SHA-256 hex.", issues, ref checks);
            }

            foreach (var compatibility in OptionalModuleRegistry.CompatibilitySnapshot().OfType<JObject>())
            {
                checks++;
                var status = (string)compatibility["status"];
                if (status == "loaded_incompatible")
                    Issue("error", "OPTIONAL_MODULE_INCOMPATIBLE", (string)compatibility["id"], issues);
                else if (status == "available_not_loaded")
                    Issue("warning", "OPTIONAL_MODULE_NOT_LOADED", (string)compatibility["id"], issues);
                else if (status == "version_unsupported")
                    Issue("warning", "OPTIONAL_PACKAGE_VERSION_UNSUPPORTED", (string)compatibility["id"], issues);
            }
            Check(string.Equals(SceneAnalyzerVersion.Current, HttpMcpServer.ServerVersion, StringComparison.Ordinal),
                "VERSION_MISMATCH", $"package={SceneAnalyzerVersion.Current}, server={HttpMcpServer.ServerVersion}", issues, ref checks);
            Check(McpProtocolNegotiator.Supported.Contains(McpProtocolNegotiator.Preferred),
                "PROTOCOL_PREFERRED_UNSUPPORTED", McpProtocolNegotiator.Preferred, issues, ref checks);

            var errors = issues.Count(issue => string.Equals((string)issue["severity"], "error", StringComparison.Ordinal));
            var warnings = issues.Count(issue => string.Equals((string)issue["severity"], "warning", StringComparison.Ordinal));
            return new JObject
            {
                ["valid"] = errors == 0,
                ["checks"] = checks,
                ["errors"] = errors,
                ["warnings"] = warnings,
                ["issues"] = issues,
                ["packageVersion"] = SceneAnalyzerVersion.Current,
                ["registryCount"] = registry?.Count ?? 0,
                ["registryHash"] = registry?.VersionHash,
                ["loadedModules"] = OptionalModuleRegistry.Snapshot(),
            };
        }

        private static JObject CapabilitySettings() => new JObject
        {
            ["writes"] = Settings.AllowWrites,
            ["reflection"] = Settings.AllowReflectionTools,
            ["destructive"] = Settings.AllowDestructiveTools,
            ["packageManager"] = Settings.AllowPackageTools,
            ["build"] = Settings.AllowBuildTools,
            ["projectSettings"] = Settings.AllowProjectSettingsTools,
            ["playControl"] = Settings.AllowPlayControlTools,
            ["runtimeCommand"] = Settings.AllowRuntimeCommandTools,
            ["highRiskConfirmation"] = Settings.RequireHighRiskConfirmation,
        };

        private static JArray CapabilityNames(McpCapability capabilities) => new JArray(
            Enum.GetValues(typeof(McpCapability)).Cast<McpCapability>()
                .Where(value => value != McpCapability.None && (capabilities & value) != 0)
                .Select(value => value.ToString()));

        private static McpCapability ParseCapability(string value) =>
            Enum.TryParse(value, true, out McpCapability parsed) ? parsed : McpCapability.None;

        private static bool Matches(string value, string filter) =>
            string.IsNullOrWhiteSpace(filter) || string.Equals(value, filter, StringComparison.OrdinalIgnoreCase);

        private static string PreviewToolFor(string toolName, McpToolRegistry registry)
        {
            if (registry == null || string.IsNullOrEmpty(toolName)) return null;
            string candidate = null;
            if (toolName.StartsWith("confirm_", StringComparison.Ordinal)) candidate = "preview_" + toolName.Substring("confirm_".Length);
            else if (toolName == "start_build") candidate = "preview_build";
            else if (toolName == "start_addressables_build") candidate = "preview_addressables_build";
            return candidate != null && registry.TryGet(candidate, out _) ? candidate : null;
        }

        private static string ModuleFromType(Type type)
        {
            if (type == null) return "unknown";
            const string main = "com.maicon.scene-analyzer.Editor";
            var assemblyName = type.Assembly.GetName().Name;
            if (!string.Equals(assemblyName, main, StringComparison.Ordinal))
            {
                if (assemblyName != null && assemblyName.StartsWith(main + ".", StringComparison.Ordinal))
                {
                    var suffix = assemblyName.Substring(main.Length + 1).ToLowerInvariant();
                    return suffix == "meshanalytics" ? "mesh-analytics" : suffix;
                }
                return assemblyName ?? "unknown";
            }

            var ns = type.Namespace ?? string.Empty;
            if (ns.IndexOf(".ProjectTools", StringComparison.Ordinal) >= 0) return "project";
            if (ns.IndexOf(".SceneTools", StringComparison.Ordinal) >= 0) return "scene";
            if (ns.IndexOf(".PrefabTools", StringComparison.Ordinal) >= 0) return "prefab";
            if (ns.IndexOf(".AssetTools", StringComparison.Ordinal) >= 0) return "assets";
            if (ns.IndexOf(".PackageTools", StringComparison.Ordinal) >= 0) return "packages";
            if (ns.IndexOf(".BuildTools", StringComparison.Ordinal) >= 0) return "build";
            if (ns.IndexOf(".SystemTools", StringComparison.Ordinal) >= 0) return "systems";
            if (ns.IndexOf(".Tools.Read", StringComparison.Ordinal) >= 0) return "read";
            if (ns.IndexOf(".Tools.Analyze", StringComparison.Ordinal) >= 0) return "analyze";
            if (ns.IndexOf(".Tools.Discover", StringComparison.Ordinal) >= 0) return "discover";
            if (ns.IndexOf(".Tools.Write", StringComparison.Ordinal) >= 0) return "author";
            if (ns.IndexOf(".Tools.Reflect", StringComparison.Ordinal) >= 0) return "reflection";
            return "core";
        }

        private static void Check(bool condition, string code, string message, JArray issues, ref int checks)
        {
            checks++;
            if (!condition) Issue("error", code, message, issues);
        }

        private static void Issue(string severity, string code, string message, JArray issues) => issues.Add(new JObject
        {
            ["severity"] = severity,
            ["code"] = code,
            ["message"] = message,
        });
    }

    internal static class OperationalSchemas
    {
        public static JObject Inventory() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["packageVersion"] = new JObject { ["type"] = "string" },
                ["registryCount"] = new JObject { ["type"] = "integer" },
                ["registryHash"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["matched"] = new JObject { ["type"] = "integer" },
                ["offset"] = new JObject { ["type"] = "integer" },
                ["returned"] = new JObject { ["type"] = "integer" },
                ["hasMore"] = new JObject { ["type"] = "boolean" },
                ["tools"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
                ["loadedModules"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            },
            ["required"] = new JArray { "packageVersion", "registryCount", "matched", "offset", "returned", "hasMore", "tools", "loadedModules" },
            ["additionalProperties"] = false,
        };

        public static JObject Health() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["status"] = new JObject { ["type"] = "string" },
                ["package"] = new JObject { ["type"] = "object" },
                ["transport"] = new JObject { ["type"] = "object" },
                ["server"] = new JObject { ["type"] = "object" },
                ["registry"] = new JObject { ["type"] = "object" },
                ["compilation"] = new JObject { ["type"] = "object" },
                ["jobs"] = new JObject { ["type"] = "object" },
                ["domain"] = new JObject { ["type"] = "object" },
                ["capabilities"] = new JObject { ["type"] = "object" },
                ["selfTest"] = new JObject { ["type"] = "object" },
                ["loadedModules"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
                ["packageCompatibility"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            },
            ["required"] = new JArray { "status", "package", "transport", "server", "registry", "compilation", "jobs", "domain", "capabilities", "selfTest", "loadedModules", "packageCompatibility" },
            ["additionalProperties"] = false,
        };

        public static JObject SelfTest() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["valid"] = new JObject { ["type"] = "boolean" },
                ["checks"] = new JObject { ["type"] = "integer" },
                ["errors"] = new JObject { ["type"] = "integer" },
                ["warnings"] = new JObject { ["type"] = "integer" },
                ["issues"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
                ["packageVersion"] = new JObject { ["type"] = "string" },
                ["registryCount"] = new JObject { ["type"] = "integer" },
                ["registryHash"] = new JObject { ["type"] = new JArray { "string", "null" } },
                ["loadedModules"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            },
            ["required"] = new JArray { "valid", "checks", "errors", "warnings", "issues", "packageVersion", "registryCount", "loadedModules" },
            ["additionalProperties"] = false,
        };
    }

    public sealed class GetToolInventoryTool : IMcpTool
    {
        public string Name => "get_tool_inventory";
        public string Description => "Returns the registry-generated tool inventory with module, mode, capability, risk and confirmation metadata.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["module"] = new JObject { ["type"] = "string" },
                ["category"] = new JObject { ["type"] = "string" },
                ["capability"] = new JObject { ["type"] = "string", ["enum"] = new JArray(Enum.GetNames(typeof(McpCapability))) },
                ["risk"] = new JObject { ["type"] = "string", ["enum"] = new JArray(Enum.GetNames(typeof(McpRiskLevel))) },
                ["editorMode"] = new JObject { ["type"] = "string", ["enum"] = new JArray(Enum.GetNames(typeof(McpEditorMode))) },
                ["confirmationRequired"] = new JObject { ["type"] = "boolean" },
                ["writeOnly"] = new JObject { ["type"] = "boolean" },
                ["includeSchemas"] = new JObject { ["type"] = "boolean", ["default"] = false },
                ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 1000, ["default"] = 250 },
            },
            ["additionalProperties"] = false,
        };
        public JObject OutputSchema => OperationalSchemas.Inventory();
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => ReadDescriptor(Name);
        public ToolResult Execute(JObject arguments)
        {
            var inventory = OperationalReport.Inventory(Bootstrap.ToolRegistry, arguments);
            return ToolResult.Ok($"{(int)inventory["returned"]} of {(int)inventory["matched"]} matching tool(s)", inventory);
        }

        internal static ToolDescriptor ReadDescriptor(string name) => new ToolDescriptor
        {
            Name = name,
            Category = "operations",
            Capabilities = McpCapability.Read,
            Risk = McpRiskLevel.Low,
            ConfirmationPolicy = McpConfirmationPolicy.None,
            EditorMode = McpEditorMode.Any,
            IsIdempotent = true,
        };
    }

    public sealed class GetServerHealthTool : IMcpTool
    {
        public string Name => "get_server_health";
        public string Description => "Returns protocol, registry, module, compilation, queue, request, job, reload and package compatibility health.";
        public JObject InputSchema => new JObject { ["type"] = "object", ["properties"] = new JObject(), ["additionalProperties"] = false };
        public JObject OutputSchema => OperationalSchemas.Health();
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => GetToolInventoryTool.ReadDescriptor(Name);
        public ToolResult Execute(JObject arguments)
        {
            var health = OperationalReport.Health();
            return ToolResult.Ok($"server health: {(string)health["status"]}", health);
        }
    }

    public sealed class SelfTestTool : IMcpTool
    {
        public string Name => "self_test";
        public string Description => "Runs read-only registry, schema, descriptor, optional-module, version and protocol invariant checks.";
        public JObject InputSchema => new JObject { ["type"] = "object", ["properties"] = new JObject(), ["additionalProperties"] = false };
        public JObject OutputSchema => OperationalSchemas.SelfTest();
        public bool IsWrite => false;
        public ToolDescriptor Descriptor => GetToolInventoryTool.ReadDescriptor(Name);
        public ToolResult Execute(JObject arguments)
        {
            var report = OperationalReport.SelfTest();
            return ToolResult.Ok((bool)report["valid"]
                ? $"self-test passed ({(int)report["checks"]} checks)"
                : $"self-test failed ({(int)report["errors"]} errors)", report);
        }
    }
}
