using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    internal static class ToolOutputContracts
    {
        private static readonly IReadOnlyDictionary<string, string[]> PropertiesByTool =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["get_input_system_context"] = new[] { "package", "devices", "actionAssets", "playerInputs" },
                ["read_input_action_asset"] = new[] { "asset", "maps", "controlSchemes" },
                ["create_input_action_asset"] = new[] { "asset", "maps", "controlSchemes" },
                ["add_input_action_map"] = new[] { "mapId", "asset", "sourceChanged" },
                ["add_input_action"] = new[] { "actionId", "asset", "sourceChanged" },
                ["add_input_binding"] = new[] { "bindingCount", "asset", "sourceChanged" },
                ["add_input_control_scheme"] = new[] { "controlSchemeCount", "asset", "sourceChanged" },
                ["configure_player_input"] = new[] { "target", "actions", "defaultActionMap", "defaultControlScheme", "notificationBehavior" },
                ["validate_input_setup"] = new[] { "valid", "issues", "assetCount", "eventSystemCount", "inputSystemUiModuleCount", "legacyInputModuleCount" },
                ["get_localization_context"] = new[] { "package", "locales", "stringTableCollectionCount", "activeSettings" },
                ["list_localization_tables"] = new[] { "collections" },
                ["create_localization_locale"] = new[] { "locale", "savedAssets" },
                ["set_localized_string"] = new[] { "collection", "locale", "key", "value", "savedAssets" },
                ["validate_localization"] = new[] { "valid", "localeCount", "collectionCount", "issues" },
                ["get_addressables_context"] = new[] { "package", "settings" },
                ["list_addressable_entries"] = new[] { "settings", "groups", "entries" },
                ["configure_addressable_entry"] = new[] { "entry", "savedAssets" },
                ["validate_addressables"] = new[] { "valid", "entryCount", "issues" },
                ["preview_addressables_build"] = new[] { "settings", "entryCount", "warning", "requiresConfirmation" },
                ["start_addressables_build"] = new[] { "jobId", "kind", "status", "progress", "message", "canCancel", "result", "error", "input", "createdUtc", "startedUtc", "completedUtc" },
                ["get_addressables_build_job"] = new[] { "jobId", "kind", "status", "progress", "message", "canCancel", "result", "error", "input", "createdUtc", "startedUtc", "completedUtc" },
                ["add_xr_grab_interactable"] = new[] { "target", "rigidbody", "collider", "component" },
                ["validate_xr_interaction_setup"] = new[] { "valid", "managerCount", "grabInteractableCount", "interactorCount", "issues" },
                ["create_probuilder_shape"] = new[] { "target", "gameObject", "vertexCount", "faceCount", "edgeCount", "indexCount", "meshSyncState" },
                ["refresh_probuilder_mesh"] = new[] { "target", "gameObject", "vertexCount", "faceCount", "edgeCount", "indexCount", "meshSyncState" },
                ["validate_probuilder_setup"] = new[] { "valid", "meshCount", "issues" },
                ["get_shader_graph_context"] = new[] { "package", "graphCount", "subGraphCount" },
                ["list_shader_graph_assets"] = new[] { "assets" },
                ["read_shader_graph_source"] = new[] { "assetPath", "guid", "bytes", "sha256", "importedAsset", "shader", "shaderSupported", "importMessages", "importErrorCount", "importWarningCount", "shaderMessages", "shaderErrorCount", "shaderWarningCount", "sourceValid", "objectRecordCount", "typeRecordCount", "objectIdRecordCount", "rootType", "looksLikeMultiJson", "source", "sourceCharacters", "truncated" },
                ["validate_shader_graph_assets"] = new[] { "valid", "assetCount", "issues" },
                ["write_shader_graph_source"] = new[] { "beforeSha256", "afterSha256", "asset", "sourceValid", "objectRecordCount", "importErrorCount", "shaderErrorCount", "savedAssets", "undoRegistered" },
            };

        public static int Count => PropertiesByTool.Count;

        public static JObject For(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)
                || !PropertiesByTool.TryGetValue(toolName, out var properties)) return null;
            var fields = new JObject();
            foreach (var property in properties) fields[property] = new JObject();
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = fields,
                ["required"] = new JArray(),
                ["additionalProperties"] = true,
            };
        }
    }
}
