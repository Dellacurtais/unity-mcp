using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Read
{
    public sealed class ReadHierarchyTool : IMcpTool
    {
        private static readonly string Generation = Guid.NewGuid().ToString("N");
        private static readonly string[] OptionalFields =
        {
            "sceneName", "scenePath", "sceneGuid", "sceneHandle", "hierarchyPath", "globalObjectId",
            "isPrefabStage", "tag", "layer", "isActive", "isStatic", "staticFlags", "prefabStatus",
            "prefabPath", "childIds", "componentTypes",
        };

        static ReadHierarchyTool()
        {
            // Invalidation is cheap: these callbacks never traverse a scene.
            EditorApplication.hierarchyChanged += Invalidate;
            EditorApplication.playModeStateChanged += _ => Invalidate();
            Undo.undoRedoPerformed += Invalidate;
            Undo.postprocessModifications += changes =>
            {
                if (changes.Any(change => change.currentValue?.target is UnityEngine.GameObject
                    || change.currentValue?.target is UnityEngine.Component)) Invalidate();
                return changes;
            };
        }

        private static void Invalidate() => Bootstrap.Snapshot?.MarkStale();

        public string Name => "read_hierarchy";
        public string Description =>
            "Returns a bounded flat hierarchy page. Defaults: 250 nodes, depth 64, 262144 UTF-8 bytes of structuredContent. " +
            "Filter by scene and root, select optional fields, then repeat the same filters with nextCursor. " +
            "Identity fields are always included. A changed snapshot or domain reload invalidates cursors; restart without cursor. " +
            "count remains the total loaded object count; matched/returned/hasMore describe this query and page.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["root"] = new JObject
                {
                    ["description"] = "Optional subtree root: stable ToolTargetRef or session-local GameObject instance ID. Includes the root itself.",
                    ["oneOf"] = new JArray(SchemaHelpers.TargetRefSchema, new JObject { ["type"] = "integer" }),
                },
                ["scene"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Exact loaded scene path, GUID, or unique name." },
                ["depth"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 256, ["default"] = 64, ["description"] = "Maximum descendant depth; selected root or scene roots are depth zero." },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 1000, ["default"] = 250 },
                ["maxBytes"] = new JObject { ["type"] = "integer", ["minimum"] = 1024, ["maximum"] = 1048576, ["default"] = 262144, ["description"] = "Maximum UTF-8 bytes of the structured result, before the MCP transport envelope and compatibility text copy." },
                ["fields"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string", ["enum"] = new JArray(OptionalFields) }, ["maxItems"] = OptionalFields.Length, ["description"] = "Optional extra fields. Omit for all; [] returns only instanceId, name, parentId, target." },
                ["cursor"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 2048 },
                ["snapshotVersion"] = new JObject { ["type"] = "string", ["description"] = "Optional expected snapshotVersion; mismatch returns SNAPSHOT_STALE." },
            },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => false;
        public JObject OutputSchema => SceneToolSchemas.ReadHierarchy;

        public ToolResult Execute(JObject args)
        {
            args = args ?? new JObject();
            if (!ToolSchemaValidator.ValidateArguments(args, InputSchema, out var argumentError))
                return ToolResult.Error("INVALID_ARGUMENT", argumentError);
            var snapshot = Bootstrap.Snapshot;
            snapshot.RefreshIfNeeded();
            var version = Generation + ":" + snapshot.Revision;
            var cursorText = (string)args["cursor"];
            JObject cursor = null;
            if (cursorText != null)
            {
                try { cursor = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(cursorText))); }
                catch (Exception exception) when (exception is FormatException || exception is JsonException)
                { return ToolResult.Error("CURSOR_INVALID", "Use nextCursor returned by read_hierarchy."); }
                if (cursor["version"]?.Type != JTokenType.String || cursor["query"]?.Type != JTokenType.String
                    || cursor["offset"]?.Type != JTokenType.Integer)
                    return ToolResult.Error("CURSOR_INVALID", "The hierarchy cursor is malformed.");
                if ((string)cursor["version"] != version)
                    return ToolResult.Error("SNAPSHOT_STALE", "The hierarchy changed or Unity reloaded. Restart without cursor.");
            }
            if (args["snapshotVersion"] != null && (string)args["snapshotVersion"] != version)
                return ToolResult.Error("SNAPSHOT_STALE", "The expected snapshot is no longer current. Restart without snapshotVersion/cursor.");

            var sceneSelector = (string)args["scene"];
            var scenes = snapshot.LoadedScenes.Where(scene => sceneSelector == null
                || string.Equals(scene.Path, sceneSelector, StringComparison.Ordinal)
                || string.Equals(scene.Guid, sceneSelector, StringComparison.Ordinal)
                || string.Equals(scene.Name, sceneSelector, StringComparison.Ordinal)).ToArray();
            if (sceneSelector != null && scenes.Length != 1)
                return ToolResult.Error(scenes.Length == 0 ? "SCENE_NOT_FOUND" : "SCENE_AMBIGUOUS", "scene must identify exactly one loaded scene; prefer its path or GUID.");
            var sceneHandles = new HashSet<int>(scenes.Select(scene => scene.Handle));
            GameObjectInfo root = null;
            if (args["root"] != null)
            {
                var rootObject = ToolTargetResolver.ResolveGameObject(new JObject { ["target"] = args["root"].DeepClone() }, out var rootError);
                if (rootObject == null) return ToolResult.Error("ROOT_NOT_FOUND", rootError);
                root = snapshot.Get(rootObject.GetMcpInstanceId());
                if (root == null || !sceneHandles.Contains(root.SceneHandle))
                    return ToolResult.Error("ROOT_OUTSIDE_SCOPE", "root must belong to the selected loaded scene(s).");
            }
            var depth = (int?)args["depth"] ?? 64;
            var limit = (int?)args["limit"] ?? 250;
            var maxBytes = (int?)args["maxBytes"] ?? 262144;
            var fields = new HashSet<string>((args["fields"] as JArray)?.Values<string>() ?? OptionalFields, StringComparer.Ordinal);
            var query = QueryHash(root?.InstanceID ?? 0, sceneSelector, depth, fields);
            if (cursor != null && (string)cursor["query"] != query)
                return ToolResult.Error("CURSOR_QUERY_MISMATCH", "Repeat the same root, scene, depth and fields when using nextCursor. limit/maxBytes may change.");

            var matches = new List<GameObjectInfo>();
            var pending = new Stack<(GameObjectInfo node, int depth)>();
            var roots = root != null ? new[] { root } : snapshot.All()
                .Where(node => node.ParentId == 0 && sceneHandles.Contains(node.SceneHandle))
                .OrderBy(node => node.SceneHandle).ThenBy(node => node.InstanceID).ToArray();
            for (var i = roots.Length - 1; i >= 0; i--) pending.Push((roots[i], 0));
            var scopeCount = 0;
            var depthLimited = false;
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                scopeCount++;
                if (current.depth <= depth) matches.Add(current.node);
                else depthLimited = true;
                for (var i = current.node.ChildIds.Count - 1; i >= 0; i--)
                {
                    var child = snapshot.Get(current.node.ChildIds[i]);
                    if (child != null) pending.Push((child, current.depth + 1));
                }
            }
            long offsetValue;
            try { offsetValue = (long?)cursor?["offset"] ?? 0; }
            catch (Exception exception) when (exception is OverflowException || exception is InvalidCastException)
            { return ToolResult.Error("CURSOR_INVALID", "The hierarchy cursor offset is invalid."); }
            if (offsetValue < 0 || offsetValue > matches.Count)
                return ToolResult.Error("CURSOR_INVALID", "The hierarchy cursor offset is outside this snapshot.");
            var offset = (int)offsetValue;
            var nodes = new JArray();
            var payload = new JObject
            {
                ["sceneName"] = snapshot.SceneName,
                ["scenePath"] = snapshot.ScenePath,
                ["loadedScenes"] = new JArray(scenes.Select(scene => new JObject
                {
                    ["name"] = scene.Name, ["path"] = scene.Path, ["guid"] = scene.Guid,
                    ["handle"] = scene.Handle, ["isActive"] = scene.IsActive, ["isPrefabStage"] = scene.IsPrefabStage,
                })),
                ["count"] = snapshot.Count,
                ["scopeCount"] = scopeCount,
                ["matched"] = matches.Count,
                ["offset"] = offset,
                ["returned"] = limit,
                ["hasMore"] = true,
                ["truncated"] = true,
                ["depthLimited"] = depthLimited,
                ["snapshotVersion"] = version,
                ["nextCursor"] = MakeCursor(version, query, matches.Count),
                ["nodes"] = nodes,
            };
            // Reserve a few bytes for booleans becoming "false". Materialize and
            // encode each selected node once; never serialize the entire page per node.
            var usedBytes = Encoding.UTF8.GetByteCount(payload.ToString(Formatting.None)) + 16;
            if (usedBytes > maxBytes)
                return ToolResult.Error("BYTE_BUDGET_TOO_SMALL", "Scene metadata exceeds maxBytes. Filter scene or increase maxBytes.");
            for (var i = offset; i < matches.Count && nodes.Count < limit; i++)
            {
                var node = BuildNode(matches[i], fields);
                var nodeBytes = Encoding.UTF8.GetByteCount(node.ToString(Formatting.None)) + (nodes.Count > 0 ? 1 : 0);
                if (usedBytes + nodeBytes > maxBytes)
                {
                    if (nodes.Count == 0)
                        return ToolResult.Error("BYTE_BUDGET_TOO_SMALL", "The next node exceeds maxBytes. Select fewer fields (especially childIds) or increase maxBytes.");
                    break;
                }
                nodes.Add(node);
                usedBytes += nodeBytes;
            }
            var hasMore = offset + nodes.Count < matches.Count;
            payload["returned"] = nodes.Count;
            payload["hasMore"] = hasMore;
            payload["truncated"] = hasMore || depthLimited;
            payload["nextCursor"] = hasMore ? MakeCursor(version, query, offset + nodes.Count) : null;
            return ToolResult.Ok($"hierarchy page: {nodes.Count} of {matches.Count} matching GameObjects", payload);
        }

        private static string QueryHash(int root, string scene, int depth, IEnumerable<string> fields)
        {
            var json = new JObject { ["root"] = root, ["scene"] = scene, ["depth"] = depth, ["fields"] = new JArray(fields.OrderBy(field => field, StringComparer.Ordinal)) };
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(json.ToString(Formatting.None))));
        }

        private static string MakeCursor(string version, string query, int offset) => Convert.ToBase64String(
            Encoding.UTF8.GetBytes(new JObject { ["version"] = version, ["query"] = query, ["offset"] = offset }.ToString(Formatting.None)));

        private static JObject BuildNode(GameObjectInfo info, HashSet<string> fields)
        {
            // Return the identity captured in this snapshot; do not resolve each
            // live object again during result enrichment or after a rename.
            var target = info.ToTargetRef();
            var node = new JObject { ["instanceId"] = info.InstanceID, ["name"] = info.Name, ["parentId"] = info.ParentId, ["target"] = target.ToJson() };
            if (fields.Contains("sceneName")) node["sceneName"] = info.SceneName;
            if (fields.Contains("scenePath")) node["scenePath"] = info.ScenePath;
            if (fields.Contains("sceneGuid")) node["sceneGuid"] = info.SceneGuid;
            if (fields.Contains("sceneHandle")) node["sceneHandle"] = info.SceneHandle;
            if (fields.Contains("hierarchyPath")) node["hierarchyPath"] = info.HierarchyPath;
            if (fields.Contains("globalObjectId")) node["globalObjectId"] = info.GlobalObjectId;
            if (fields.Contains("isPrefabStage")) node["isPrefabStage"] = info.IsPrefabStage;
            if (fields.Contains("tag")) node["tag"] = info.Tag;
            if (fields.Contains("layer")) node["layer"] = info.Layer;
            if (fields.Contains("isActive")) node["isActive"] = info.IsActive;
            if (fields.Contains("isStatic")) node["isStatic"] = info.IsStatic;
            if (fields.Contains("staticFlags")) node["staticFlags"] = (int)info.StaticFlags;
            if (fields.Contains("prefabStatus")) node["prefabStatus"] = info.PrefabStatus.ToString();
            if (fields.Contains("prefabPath")) node["prefabPath"] = info.PrefabAssetPath;
            if (fields.Contains("childIds")) node["childIds"] = new JArray(info.ChildIds);
            if (fields.Contains("componentTypes")) node["componentTypes"] = new JArray(info.Components.Select(component => component.TypeName));
            return node;
        }
    }
}
