using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp.Jobs;
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
        private static readonly JObject StableTargetReferenceSchema = SchemaHelpers.TargetRefSchema;

        private readonly McpToolRegistry _registry;
        private readonly ConfirmationTokenService _confirmations = new ConfirmationTokenService();
        private readonly McpRequestCoordinator _coordinator;
        private readonly int _toolTimeoutSeconds;
        private static int _callSeq;

        public McpRequestProcessor(McpToolRegistry registry, int maxConcurrentRequests, int toolTimeoutSeconds)
            : this(registry, maxConcurrentRequests, toolTimeoutSeconds, EditorMutationGate.Semaphore) { }

        /// Tests pass their own gate: a run_tests job started over MCP owns the Editor-wide
        /// gate for the whole run, and must not make the processor's own tests time out.
        internal McpRequestProcessor(McpToolRegistry registry, int maxConcurrentRequests, int toolTimeoutSeconds, SemaphoreSlim mutationGate)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _coordinator = new McpRequestCoordinator(maxConcurrentRequests, mutationGate);
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
                        return await MainThreadDispatcher.RunAsync(() => ParseObject(OkResp(id, new JObject
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
                            ["instructions"] = Settings.RequireHighRiskConfirmation
                                ? "High-risk tools return CONFIRMATION_REQUIRED with a single-use token; retry the same arguments with _confirmationToken."
                                : "High-risk confirmation is disabled; authorized tools execute directly subject to the configured capability switches.",
                        })), cancellationToken);
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
            catch (McpRequestCoordinator.DuplicateRequestException duplicate)
            {
                // A retry or a job poll that arrived while the previous one still runs.
                return notification ? null : ParseObject(OkResp(id, ToolErrorBody("REQUEST_IN_PROGRESS", duplicate.Message,
                    new JObject { ["retrySafe"] = true })));
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
            var moduleByTool = OperationalReport.BuildModuleLookup();
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
                        ["openWorldHint"] = (descriptor.Capabilities &
                            (McpCapability.PackageManager | McpCapability.Build | McpCapability.ExternalService)) != 0,
                    },
                    ["_meta"] = new JObject
                    {
                        ["module"] = moduleByTool.TryGetValue(tool.Name, out var knownModule)
                            ? knownModule : OperationalReport.ModuleFromType(tool.GetType()),
                        ["category"] = descriptor.Category,
                        ["capabilities"] = descriptor.Capabilities.ToString(),
                        ["risk"] = descriptor.Risk.ToString(),
                        ["confirmationRequired"] = ToolAuthorization.RequiresConfirmation(descriptor),
                        ["editorMode"] = descriptor.EditorMode.ToString(),
                        ["executionRequirements"] = new JArray(ExecutionRequirementNames(descriptor.ExecutionRequirements)),
                        ["headlessSafe"] = descriptor.HeadlessSafe,
                        ["jobLifecycle"] = (descriptor.ExecutionRequirements & McpExecutionRequirement.LongRunningJob) != 0
                            ? new JObject
                            {
                                ["startsJob"] = true,
                                ["waitTool"] = "wait_tool_job",
                                ["cancelTool"] = "cancel_tool_job",
                                ["autoWait"] = true,
                            }
                            : null,
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
            var sourceArguments = (JObject)executionArguments.DeepClone();

            ToolDescriptor descriptor = null;
            var inputSchema = _registry.GetInputSchema(name) ?? new JObject { ["type"] = "object" };
            var requiresConfirmation = false;
            string preparationCode = null;
            string preparationMessage = null;
            bool PrepareArguments()
            {
                executionArguments = (JObject)sourceArguments.DeepClone();
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
                    // Native target-aware tools must receive the exact component
                    // identity. The legacy adapter is only for instanceId schemas.
                    if (inputSchema["properties"]?["target"] == null)
                    {
                        if (target is Component component && executionArguments["componentType"] != null)
                        {
                            var componentType = ComponentEditor.ResolveComponentType((string)executionArguments["componentType"]);
                            if (componentType == null || !typeof(Component).IsAssignableFrom(componentType)
                                || !componentType.IsInstanceOfType(component))
                            {
                                preparationCode = "TARGET_TYPE_MISMATCH";
                                preparationMessage = "componentType must match the captured component identity.";
                                return false;
                            }
                            var ordinal = Array.IndexOf(component.gameObject.GetComponents(componentType), component);
                            if (inputSchema["properties"]?["nth"] != null) executionArguments["nth"] = ordinal;
                            else if (inputSchema["properties"]?["componentOrdinal"] != null) executionArguments["componentOrdinal"] = ordinal;
                            else if (ordinal != 0)
                            {
                                preparationCode = "TARGET_COMPONENT_UNSUPPORTED";
                                preparationMessage = "This legacy tool cannot address the captured component ordinal; use a target-aware component tool.";
                                return false;
                            }
                        }
                        var gameObject = target as GameObject ?? (target as Component)?.gameObject;
                        executionArguments["instanceId"] = gameObject != null
                            ? gameObject.GetInstanceID()
                            : target.GetInstanceID();
                        executionArguments.Remove("target");
                    }
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
            }
            var prepared = await MainThreadDispatcher.RunAsync(PrepareArguments, cancellationToken);
            if (!prepared) return OkResp(id, ToolErrorBody(preparationCode, preparationMessage));

            string RequireConfirmation()
            {
                var issued = _confirmations.Issue(name, sourceArguments, Bootstrap.UnityProjectRoot ?? string.Empty, TimeSpan.FromMinutes(2));
                return OkResp(id, ToolErrorBody("CONFIRMATION_REQUIRED",
                    "Review the requested operation, then retry the same arguments with _confirmationToken.",
                    new JObject
                    {
                        ["confirmationToken"] = issued,
                        ["expiresInSeconds"] = 120,
                        ["tool"] = name,
                        ["risk"] = descriptor.Risk.ToString(),
                        ["capabilities"] = descriptor.Capabilities.ToString(),
                        ["arguments"] = sourceArguments.DeepClone(),
                    }));
            }
            var confirmationGranted = false;
            if (requiresConfirmation)
            {
                var projectKey = Bootstrap.UnityProjectRoot ?? string.Empty;
                confirmationGranted = _confirmations.TryConsume(confirmationToken, name, sourceArguments, projectKey);
                if (!confirmationGranted) return RequireConfirmation();
            }

            var requestId = id != null ? RequestIdKey(id) : Guid.NewGuid().ToString("N");
            var timeout = TimeSpan.FromSeconds(_toolTimeoutSeconds);
            if (descriptor.IsWrite && !ToolJobManager.IsControlTool(name) && _coordinator.UsesEditorMutationGate
                && EditorMutationGate.TryGetJobOwner(out var ownerJobId, out var ownerKind))
            {
                // Waiting here could take as long as the whole job (a test run, a build).
                return OkResp(id, ToolErrorBody("RESOURCE_BUSY",
                    $"A {ownerKind} job ({ownerJobId}) holds the Editor for writes. Wait for it with wait_tool_job, then retry; nothing was changed.",
                    new JObject { ["jobId"] = ownerJobId, ["kind"] = ownerKind, ["retrySafe"] = true }));
            }
            using (var lease = await _coordinator.BeginAsync(requestId, descriptor.IsWrite && !ToolJobManager.IsControlTool(name),
                cancellationToken, timeout, ToolJobManager.IsControlTool(name)))
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
                // Queueing can span scene changes, target deletion, or capability changes.
                // Resolve the original identity again, rather than the earlier instanceId.
                if (!await MainThreadDispatcher.RunAsync(PrepareArguments, lease.CancellationToken))
                    return OkResp(id, ToolErrorBody(preparationCode, preparationMessage));
                if (requiresConfirmation && !confirmationGranted) return RequireConfirmation();
                var activityLease = await EditorActivityLease.AcquireAsync(descriptor, lease.CancellationToken);
                ToolResult result = null;
                var invocationStarted = false;
                var resultKnown = false;
                var finalizationCompleted = false;
                System.Collections.Generic.HashSet<int> preMutationScenes = null;
                try
                {
                    preMutationScenes = (descriptor.Capabilities & McpCapability.SceneWrite) != 0
                        ? await MainThreadDispatcher.RunAsync(
                            () => MutationScope.CaptureLegacySceneHandles(executionArguments), lease.CancellationToken)
                        : null;
                    lease.CancellationToken.ThrowIfCancellationRequested();
                    invocationStarted = true;
                    result = await tool.ExecuteAsync(executionArguments);
                    resultKnown = true;
                    if (result == null) result = ToolResult.Error("NULL_RESULT", $"Tool '{name}' returned no result.");
                    // Once a tool returned, request cancellation cannot undo its effects.
                    // Finish bookkeeping independently and preserve the known tool result.
                    using var finalization = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    if (!result.IsError && result.Json != null)
                        await MainThreadDispatcher.RunAsync(
                            () => EnrichStableTargets(result.Json, 0), finalization.Token);
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
                                finalization.Token);
                            if (result.Metadata == null) result.Metadata = new JObject();
                            result.Metadata["mutation"] = mutationMeta;
                        }
                        await MainThreadDispatcher.RunAsync(Bootstrap.NotifyWriteCompleted, finalization.Token);
                    }
                    finalizationCompleted = true;
                }
                catch (OperationCanceledException)
                {
                    if (resultKnown) AddFinalizationWarning(result, "Finalization timed out; the tool result is preserved. Re-observe the affected resources before another write.");
                    else result = descriptor.IsWrite && invocationStarted
                        ? ToolResult.Error("OUTCOME_UNKNOWN", $"Tool '{name}' was interrupted after invocation began; inspect its effects before retrying.")
                        : ToolResult.Error("CANCELLED", $"Tool '{name}' was cancelled before a result was returned.");
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
                    if (resultKnown) AddFinalizationWarning(result, $"Finalization failed: {exception.Message}. The tool result is preserved.");
                    else result = ToolResult.Error(descriptor.IsWrite && invocationStarted ? "OUTCOME_UNKNOWN" : "INTERNAL_ERROR", exception.Message,
                        new JObject { ["exceptionType"] = exception.GetType().FullName });
                }
                finally
                {
                    await activityLease.ReleaseAsync();
                }

                if (result.Metadata == null) result.Metadata = new JObject();
                result.Metadata["executionReceipt"] = new JObject
                {
                    ["requestId"] = requestId,
                    ["status"] = resultKnown ? (result.IsError ? "toolError" : "completed") : invocationStarted ? "outcomeUnknown" : "notStarted",
                    ["resultKnown"] = resultKnown,
                    ["finalizationCompleted"] = finalizationCompleted,
                };
                return OkResp(id, BuildToolResult(result, name));
            }
        }

        private static void AddFinalizationWarning(ToolResult result, string message)
        {
            if (result.Warnings == null) result.Warnings = new JArray();
            result.Warnings.Add(message);
        }

        private static IEnumerable<string> ExecutionRequirementNames(McpExecutionRequirement requirements)
        {
            foreach (McpExecutionRequirement value in Enum.GetValues(typeof(McpExecutionRequirement)))
            {
                if (value == McpExecutionRequirement.None || (requirements & value) == 0) continue;
                yield return value.ToString();
            }
        }

        private static void EnrichStableTargets(JToken token, int depth)
        {
            if (token == null || depth > 6) return;
            if (token is JObject obj)
            {
                // A captured reference is already the reusable target payload. Wrapping it
                // again adds an unknown field and can replace its original provenance.
                if (IsSerializedTargetReference(obj)) return;
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

        private static bool IsSerializedTargetReference(JObject obj)
            => obj["kind"]?.Type == JTokenType.String
                && obj["isPrefabStage"]?.Type == JTokenType.Boolean
                && (obj["captureDomainId"]?.Type == JTokenType.String
                    || obj["globalObjectId"]?.Type == JTokenType.String
                    || obj["assetGuid"]?.Type == JTokenType.String)
                && ToolSchemaValidator.ValidateArguments(obj, StableTargetReferenceSchema, out _);

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
