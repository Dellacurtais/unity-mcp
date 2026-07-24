using System;
using Newtonsoft.Json.Linq;
using Maicon.SceneAnalyzer.Editor.Authoring;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools
{
    public static class AnimationToolContracts
    {
        public static JObject ControllerInputSchema(JObject extraProperties = null, params string[] requiredExtra)
        {
            var properties = new JObject
            {
                ["controllerGuidOrPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["controllerInstanceId"] = new JObject { ["type"] = "integer" },
            };
            if (extraProperties != null)
                foreach (var property in extraProperties.Properties())
                    properties[property.Name] = property.Value.DeepClone();

            var required = new JArray();
            if (requiredExtra != null)
                foreach (var name in requiredExtra)
                    required.Add(name);

            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["oneOf"] = new JArray
                {
                    ReferenceVariant(properties, required, "controllerGuidOrPath"),
                    ReferenceVariant(properties, required, "controllerInstanceId"),
                },
                ["additionalProperties"] = false,
            };
        }

        public static ToolDescriptor AssetWriteDescriptor(string name, bool idempotent = false) => new ToolDescriptor
        {
            Name = name,
            Category = "animation",
            Capabilities = McpCapability.AssetWrite,
            Risk = McpRiskLevel.Medium,
            ConfirmationPolicy = McpConfirmationPolicy.None,
            EditorMode = McpEditorMode.EditModeOnly,
            IsIdempotent = idempotent,
        };

        public static ToolDescriptor SceneWriteDescriptor(string name, bool idempotent = true) => new ToolDescriptor
        {
            Name = name,
            Category = "animation",
            Capabilities = McpCapability.SceneWrite,
            Risk = McpRiskLevel.Medium,
            ConfirmationPolicy = McpConfirmationPolicy.None,
            EditorMode = McpEditorMode.EditModeOnly,
            IsIdempotent = idempotent,
        };

        public static ToolDescriptor RuntimeWriteDescriptor(string name, bool idempotent) => new ToolDescriptor
        {
            Name = name,
            Category = "animation",
            Capabilities = McpCapability.RuntimeCommand,
            Risk = McpRiskLevel.Medium,
            ConfirmationPolicy = McpConfirmationPolicy.None,
            EditorMode = McpEditorMode.PlayModeOnly,
            IsIdempotent = idempotent,
        };

        public static ToolDescriptor RuntimeReadDescriptor(string name) => new ToolDescriptor
        {
            Name = name,
            Category = "animation",
            Capabilities = McpCapability.Read,
            Risk = McpRiskLevel.Low,
            ConfirmationPolicy = McpConfirmationPolicy.None,
            EditorMode = McpEditorMode.PlayModeOnly,
            IsIdempotent = true,
        };

        public static ToolResult Result(OpResult result)
        {
            if (result == null) return ToolResult.Error("NULL_RESULT", "Animation operation returned no result.");
            if (result.Ok) return ToolResult.Ok(result.Summary, result.Data);

            var error = result.Error ?? "Animation operation failed.";
            var separator = error.IndexOf(':');
            if (separator > 0)
            {
                var candidate = error.Substring(0, separator);
                var validCode = true;
                foreach (var character in candidate)
                {
                    if ((character < 'A' || character > 'Z') && character != '_' && (character < '0' || character > '9'))
                    {
                        validCode = false;
                        break;
                    }
                }
                if (validCode)
                    return ToolResult.Error(candidate, error.Substring(separator + 1).Trim());
            }
            return ToolResult.Error("OP_FAILED", error);
        }

        private static JObject ReferenceVariant(JObject allProperties, JArray requiredExtra, string referenceName)
        {
            var properties = new JObject();
            foreach (var property in allProperties.Properties())
                properties[property.Name] = property.Value.DeepClone();
            var required = (JArray)requiredExtra.DeepClone();
            required.Add(referenceName);
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false,
            };
        }
    }
}
