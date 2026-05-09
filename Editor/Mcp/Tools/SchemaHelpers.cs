using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools
{
    /// Shared JSON Schema fragments used across tool input schemas. Centralised
    /// so that pose/color shapes stay consistent across the MCP surface.
    internal static class SchemaHelpers
    {
        public static JObject Vec3Schema => new JObject
        {
            ["description"] = "Vector3 as [x,y,z] or {x,y,z}.",
            ["oneOf"] = new JArray
            {
                new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" }, ["minItems"] = 3, ["maxItems"] = 3 },
                new JObject { ["type"] = "object" },
            },
        };

        public static JObject RotSchema => new JObject
        {
            ["description"] = "Euler degrees [x,y,z] or quaternion [x,y,z,w] / {x,y,z,w}.",
            ["oneOf"] = new JArray
            {
                new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" } },
                new JObject { ["type"] = "object" },
            },
        };
    }
}
