using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    public sealed class McpProcessOutcome
    {
        public int StatusCode { get; set; } = 200;
        public string Body { get; set; }
    }

    public sealed class McpRequestProcessor : IDisposable
    {
        private const string ServerName = "unity-mcp";
        private const string ConfirmationArgument = "_confirmationToken";

        private readonly McpToolRegistry _registry;
        private readonly ConfirmationTokenService _confirmations = new ConfirmationTokenService();
        private readonly McpRequestCoordinator _coordinator;
        private readonly int _toolTimeoutSeconds;
        private static int _callSeq;

        public McpRequestProcessor(McpToolRegistry registry, int maxConcurrentRequests, int toolTimeoutSeconds)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _coordinator = new McpRequestCoordinator(maxConcurrentRequests);
            _toolTimeoutSeconds = Math.Max(1, toolTimeoutSeconds);
        }

        public int InFlightCount => _coordinator.InFlightCount;

        public async Task<McpProcessOutcome> ProcessAsync(
            string body, string protocolVersion, string origin, CancellationToken cancellationToken)
        {
            JToken request;
            try { request = JToken.Parse(body); }
            catch { return JsonOutcome(ErrorResp(null, -32700, "Parse error")); }

            if (request is JArray batch)
            {
                if (batch.Count == 0) return JsonOutcome(ErrorResp(null, -32600, "Invalid Request: empty batch"));
                var results = new JArray();
                foreach (var item in batch)
                {
                    var response = await ProcessOne(item, protocolVersion, origin, cancellationToken);
                    if (response != null) results.Add(response);
                }
                return results.Count == 0
                    ? new McpProcessOutcome { StatusCode = 202 }
                    : JsonOutcome(results.ToString(Formatting.None));
            }

            var single = await ProcessOne(request, protocolVersion, origin, cancellationToken);
            return single == null
                ? new McpProcessOutcome { StatusCode = 202 }
                : JsonOutcome(single.ToString(Formatting.None));
        }

        public bool Cancel(string requestId) => _coordinator.Cancel(requestId);
        public void CancelAll() => _coordinator.CancelAll();
        public void Dispose() => _coordinator.Dispose();

        private async Task<JObject> ProcessOne(
            JToken token, string protocolVersion, string origin, CancellationToken cancellationToken)
        {
            if (!(token is JObject request) || (string)request["jsonrpc"] != "2.0")
                return ParseObject(ErrorResp(null, -32600, "Invalid Request"));

            var id = request["id"];
            var method = (string)request["method"];
            var parameters = request["params"] as JObject ?? new JObject();
            var notification = id == null;
            if (string.IsNullOrEmpty(method)) return ParseObject(ErrorResp(id, -32600, "Invalid Request: method is required"));

            try
            {
                switch (method)
                {
                    case "initialize":
                    {
                        var selected = McpProtocolNegotiator.Negotiate((string)parameters["protocolVersion"]);
                        return ParseObject(OkResp(id, new JObject
                        {
                            ["protocolVersion"] = selected,
                            ["capabilities"] = new JObject
                            {
                                ["tools"] = new JObject { ["listChanged"] = false },
                            },
                            ["serverInfo"] = new JObject
                            {
                                ["name"] = ServerName,
                                ["version"] = SceneAnalyzerVersion.Current,
                            },
                            ["instructions"] = "High-risk tools return CONFIRMATION_REQUIRED with a single-use token; retry the same arguments with _confirmationToken.",
                        }));
                    }
                    case "notifications/initialized":
                        return notification ? null : ParseObject(OkResp(id, new JObject()));
                    case "notifications/cancelled":
                    {
                        var requestId = RequestIdKey(parameters["requestId"]);
                        _coordinator.Cancel(requestId);
                        return notification ? null : ParseObject(OkResp(id, new JObject { ["cancelled"] = requestId }));
                    }
                    case "ping":
                        return notification ? null : ParseObject(OkResp(id, new JObject()));
                    case "tools/list":
                        return notification
                            ? null
                            : ParseObject(OkResp(id, await MainThreadDispatcher.RunAsync(ListTools, cancellationToken)));
                    case "tools/call":
                        if (notification)
                        {
                            _ = CallToolAsync(id, parameters, protocolVersion, origin, cancellationToken);
                            return null;
                        }
                        return ParseObject(await CallToolAsync(id, parameters, protocolVersion, origin, cancellationToken));
                    default:
                        return notification ? null : ParseObject(ErrorResp(id, -32601, $"Method not found: {method}"));
                }
            }
            catch (OperationCanceledException)
            {
                return notification ? null : ParseObject(OkResp(id, ToolErrorBody("CANCELLED", "The MCP request was cancelled.")));
            }
            catch (Exception exception)
            {
                try
                {
                    await MainThreadDispatcher.RunAsync(
                        () => Debug.LogException(exception),
                        CancellationToken.None);
                }
                catch { }
                return notification ? null : ParseObject(ErrorResp(id, -32603, exception.Message));
            }
        }

        internal JObject ListTools()
        {
            var tools = new JArray();
            foreach (var tool in _registry.All)
            {
                var descriptor = _registry.GetDescriptor(tool.Name) ?? ToolDescriptor.Infer(tool);
                var inputSchema = BuildAdvertisedInputSchema(_registry.GetInputSchema(tool.Name));
                if (ToolAuthorization.RequiresConfirmation(descriptor))
                {
                    var properties = inputSchema["properties"] as JObject;
                    if (properties == null) inputSchema["properties"] = properties = new JObject();
                    properties[ConfirmationArgument] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Single-use token returned by a prior CONFIRMATION_REQUIRED response for these exact arguments.",
                    };
                }

                var item = new JObject
                {
                    ["name"] = tool.Name,
                    ["title"] = tool.Name.Replace('_', ' '),
                    ["description"] = tool.Description,
                    ["inputSchema"] = inputSchema,
                    ["annotations"] = new JObject
                    {
                        ["readOnlyHint"] = !descriptor.IsWrite,
                        ["destructiveHint"] = (descriptor.Capabilities & McpCapability.Destructive) != 0,
                        ["idempotentHint"] = descriptor.IsIdempotent,
                        ["openWorldHint"] = (descriptor.Capabilities & (McpCapability.PackageManager | McpCapability.Build)) != 0,
                    },
                    ["_meta"] = new JObject
                    {
                        ["category"] = descriptor.Category,
                        ["capabilities"] = descriptor.Capabilities.ToString(),
                        ["risk"] = descriptor.Risk.ToString(),
                        ["confirmationRequired"] = ToolAuthorization.RequiresConfirmation(descriptor),
                        ["editorMode"] = descriptor.EditorMode.ToString(),
                    },
                };
                var outputSchema = _registry.GetOutputSchema(tool.Name);
                if (outputSchema != null) item["outputSchema"] = BuildAdvertisedOutputSchema(outputSchema);
                tools.Add(item);
            }
            return new JObject
            {
                ["tools"] = tools,
                ["_meta"] = new JObject
                {
                    ["registryCount"] = _registry.Count,
                    ["registryHash"] = _registry.VersionHash,
                },
            };
        }

        private static JObject BuildAdvertisedInputSchema(JObject registeredSchema)
        {
            var schema = (JObject)(registeredSchema ?? new JObject { ["type"] = "object" }).DeepClone();
            var properties = schema["properties"] as JObject;
            if (properties == null || properties["instanceId"] == null) return schema;

            properties["target"] = SchemaHelpers.TargetRefSchema;
            if (schema["required"] is JArray required
                && required.Any(token => string.Equals((string)token, "instanceId", StringComparison.Ordinal)))
            {
                for (var i = required.Count - 1; i >= 0; i--)
                    if (string.Equals((string)required[i], "instanceId", StringComparison.Ordinal)) required.RemoveAt(i);
                if (required.Count == 0) schema.Remove("required");
                schema["anyOf"] = new JArray
                {
                    new JObject { ["required"] = new JArray { "instanceId" } },
                    new JObject { ["required"] = new JArray { "target" } },
                };
            }
            return schema;
        }

        internal static JObject BuildAdvertisedOutputSchema(JObject successSchema)
        {
            if (successSchema == null) return null;
            var errorSchema = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["code"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                    ["message"] = new JObject { ["type"] = "string" },
                    ["data"] = new JObject(),
                },
                ["required"] = new JArray { "code", "message" },
                ["additionalProperties"] = false,
            };
            return new JObject
            {
                ["type"] = "object",
                ["anyOf"] = new JArray { successSchema.DeepClone(), errorSchema },
            };
        }

        internal static bool RequiresLegacyMutationFinalization(ToolDescriptor descriptor) =>
            descriptor != null && (descriptor.Capabilities & McpCapability.SceneWrite) != 0;

        private async Task<string> CallToolAsync(
            JToken id, JObject parameters, string protocolVersion, string origin, CancellationToken cancellationToken)
        {
            var name = (string)parameters["name"];
            if (string.IsNullOrEmpty(name)) return ErrorResp(id, -32602, "Tool name is required.");
            if (!_registry.TryGet(name, out var tool)) return ErrorResp(id, -32602, $"Tool not found: {name}");

            var arguments = parameters["arguments"] as JObject ?? new JObject();
            var executionArguments = (JObject)arguments.DeepClone();
            var confirmationToken = (string)executionArguments[ConfirmationArgument];
            executionArguments.Remove(ConfirmationArgument);

            ToolDescriptor descriptor = null;
            var inputSchema = _registry.GetInputSchema(name) ?? new JObject { ["type"] = "object" };
            var requiresConfirmation = false;
            string preparationCode = null;
            string preparationMessage = null;
            var prepared = await MainThreadDispatcher.RunAsync(() =>
            {
                if (executionArguments["target"] != null && inputSchema["properties"]?["instanceId"] != null)
                {
                    var target = ToolTargetResolver.Resolve(
                        ToolTargetRef.FromJson(executionArguments["target"]), out var targetError);
                    if (target == null)
                    {
                        preparationCode = "TARGET_NOT_FOUND";
                        preparationMessage = targetError;
                        return false;
                    }
                    var gameObject = target as GameObject ?? (target as Component)?.gameObject;
                    executionArguments["instanceId"] = gameObject != null
                        ? gameObject.GetInstanceID()
                        : target.GetInstanceID();
                    executionArguments.Remove("target");
                }
                if (!ToolSchemaValidator.ValidateArguments(executionArguments, inputSchema, out var argumentError))
                {
                    preparationCode = "BAD_ARGS";
                    preparationMessage = argumentError;
                    return false;
                }

                descriptor = _registry.GetDescriptor(name) ?? ToolDescriptor.Infer(tool);
                if (!ToolAuthorization.TryAuthorize(
                    tool, descriptor, out preparationCode, out preparationMessage)) return false;
                requiresConfirmation = ToolAuthorization.RequiresConfirmation(descriptor);
                return true;
            }, cancellationToken);
            if (!prepared) return OkResp(id, ToolErrorBody(preparationCode, preparationMessage));

            if (requiresConfirmation)
            {
                var projectKey = Bootstrap.UnityProjectRoot ?? string.Empty;
                if (!_confirmations.TryConsume(confirmationToken, name, executionArguments, projectKey))
                {
                    var issued = _confirmations.Issue(name, executionArguments, projectKey, TimeSpan.FromMinutes(2));
                    return OkResp(id, ToolErrorBody("CONFIRMATION_REQUIRED",
                        "Review the requested operation, then retry the same arguments with _confirmationToken.",
                        new JObject
                        {
                            ["confirmationToken"] = issued,
                            ["expiresInSeconds"] = 120,
                            ["tool"] = name,
                            ["risk"] = descriptor.Risk.ToString(),
                            ["capabilities"] = descriptor.Capabilities.ToString(),
                            ["arguments"] = executionArguments.DeepClone(),
                        }));
                }
            }

            var requestId = id != null ? RequestIdKey(id) : Guid.NewGuid().ToString("N");
            var timeout = TimeSpan.FromSeconds(_toolTimeoutSeconds);
            using (var lease = await _coordinator.BeginAsync(requestId, descriptor.IsWrite, cancellationToken, timeout))
            using (ToolExecutionContext.Push(new ToolExecutionContext
            {
                ToolName = name,
                RequestId = requestId,
                ProtocolVersion = protocolVersion,
                Origin = origin,
                RequestPath = "/mcp/",
                CancellationToken = lease.CancellationToken,
                DeadlineUtc = DateTimeOffset.UtcNow.Add(timeout),
                IsNotification = id == null,
                RequestMeta = parameters["_meta"] as JObject,
            }))
            {
                ToolResult result;
                var preMutationScenes = (descriptor.Capabilities & McpCapability.SceneWrite) != 0
                    ? await MainThreadDispatcher.RunAsync(
                        () => MutationScope.CaptureLegacySceneHandles(executionArguments), lease.CancellationToken)
                    : null;
                try
                {
                    result = await tool.ExecuteAsync(executionArguments);
                    if (result == null) result = ToolResult.Error("NULL_RESULT", $"Tool '{name}' returned no result.");
                    if (!result.IsError && result.Json != null)
                        await MainThreadDispatcher.RunAsync(
                            () => EnrichStableTargets(result.Json, 0), lease.CancellationToken);
                    if (descriptor.IsWrite && !result.IsError)
                    {
                        if (RequiresLegacyMutationFinalization(descriptor))
                        {
                            var resultInstanceId = result.Json is JObject resultObject
                                ? (int?)resultObject["instanceId"] ?? 0
                                : 0;
                            var mutationMeta = await MainThreadDispatcher.RunAsync(
                                () => MutationScope.FinalizeLegacyMutation(
                                    executionArguments, resultInstanceId, preMutationScenes),
                                lease.CancellationToken);
                            if (result.Metadata == null) result.Metadata = new JObject();
                            result.Metadata["mutation"] = mutationMeta;
                        }
                        await MainThreadDispatcher.RunAsync(Bootstrap.NotifyWriteCompleted, lease.CancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    result = ToolResult.Error("CANCELLED", $"Tool '{name}' was cancelled or exceeded its deadline.");
                }
                catch (Exception exception)
                {
                    try
                    {
                        await MainThreadDispatcher.RunAsync(
                            () => Debug.LogException(exception),
                            CancellationToken.None);
                    }
                    catch { }
                    result = ToolResult.Error("INTERNAL_ERROR", exception.Message,
                        new JObject { ["exceptionType"] = exception.GetType().FullName });
                }

                return OkResp(id, BuildToolResult(result, name));
            }
        }

        private static void EnrichStableTargets(JToken token, int depth)
        {
            if (token == null || depth > 6) return;
            if (token is JObject obj)
            {
                if (obj["target"] == null && obj["instanceId"]?.Type == JTokenType.Integer)
                {
                    var unityObject = UnityEditor.EditorUtility.InstanceIDToObject((int)obj["instanceId"]);
                    var target = ToolTargetRef.Capture(unityObject);
                    if (target != null) obj["target"] = target.ToJson();
                }
                foreach (var property in obj.Properties())
                    if (property.Name != "target") EnrichStableTargets(property.Value, depth + 1);
            }
            else if (token is JArray array)
            {
                var count = Math.Min(array.Count, 1000);
                for (var i = 0; i < count; i++) EnrichStableTargets(array[i], depth + 1);
            }
        }

        private JObject BuildToolResult(ToolResult result, string toolName)
        {
            JArray content;
            if (result.ContentBlocks != null && result.ContentBlocks.Count > 0)
                content = (JArray)result.ContentBlocks.DeepClone();
            else
            {
                content = new JArray();
                if (!string.IsNullOrEmpty(result.Text))
                    content.Add(new JObject { ["type"] = "text", ["text"] = result.Text });
                if (result.Json != null)
                    content.Add(new JObject { ["type"] = "text", ["text"] = result.Json.ToString(Formatting.None) });
                if (content.Count == 0) content.Add(new JObject { ["type"] = "text", ["text"] = string.Empty });
            }

            var callSequence = Interlocked.Increment(ref _callSeq);
            var meta = result.Metadata != null ? (JObject)result.Metadata.DeepClone() : new JObject();
            meta["unityCallSequence"] = callSequence;
            meta["tool"] = toolName;
            meta["registryHash"] = _registry.VersionHash;
            var payload = new JObject
            {
                ["content"] = content,
                ["isError"] = result.IsError,
                ["_meta"] = meta,
            };
            if (result.Json != null) payload["structuredContent"] = result.Json.DeepClone();
            if (result.Warnings != null && result.Warnings.Count > 0) payload["warnings"] = result.Warnings.DeepClone();
            return payload;
        }

        private static JObject ToolErrorBody(string code, string message, JToken data = null)
        {
            var structured = new JObject { ["code"] = code, ["message"] = message };
            if (data != null) structured["data"] = data;
            return new JObject
            {
                ["content"] = new JArray
                {
                    new JObject { ["type"] = "text", ["text"] = $"[{code}] {message}" },
                },
                ["structuredContent"] = structured,
                ["isError"] = true,
            };
        }

        private static McpProcessOutcome JsonOutcome(string body) =>
            new McpProcessOutcome { StatusCode = 200, Body = body };

        private static string RequestIdKey(JToken id)
        {
            if (id == null || id.Type == JTokenType.Null) return null;
            return id.Type == JTokenType.String ? (string)id : id.ToString(Formatting.None);
        }

        private static JObject ParseObject(string json) => JObject.Parse(json);

        private static string OkResp(JToken id, JToken result)
        {
            var response = new JObject { ["jsonrpc"] = "2.0", ["result"] = result };
            if (id != null) response["id"] = id.DeepClone();
            return response.ToString(Formatting.None);
        }

        private static string ErrorResp(JToken id, int code, string message)
        {
            var response = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["error"] = new JObject { ["code"] = code, ["message"] = message },
            };
            if (id != null) response["id"] = id.DeepClone();
            return response.ToString(Formatting.None);
        }
    }
}
