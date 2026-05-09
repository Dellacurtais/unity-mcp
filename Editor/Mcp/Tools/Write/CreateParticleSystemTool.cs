using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class CreateParticleSystemTool : IMcpTool
    {
        public string Name => "create_particle_system";
        public string Description =>
            "Creates a ParticleSystem from one of the built-in templates (smoke, fire, sparks, dust, trail). Override duration/startColor/startSize/startSpeed/startLifetime/emissionRate inline; use set_property for finer module controls (shape, velocityOverLifetime, etc.). NOTE on URP/HDRP: the default ParticleSystem renderer material is the legacy Standard Particle shader which appears pink under URP/HDRP — assign a pipeline-correct particle material via `set_material` on the ParticleSystemRenderer slot 0 afterwards.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["template"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "smoke", "fire", "sparks", "dust", "trail", "default" } },
                ["name"] = new JObject { ["type"] = "string" },
                ["duration"] = new JObject { ["type"] = "number" },
                ["startColor"] = new JObject(),
                ["startSize"] = new JObject { ["type"] = "number" },
                ["startSpeed"] = new JObject { ["type"] = "number" },
                ["startLifetime"] = new JObject { ["type"] = "number" },
                ["emissionRate"] = new JObject { ["type"] = "number" },
                ["parent"] = new JObject { ["type"] = "integer" },
                ["position"] = SchemaHelpers.Vec3Schema,
                ["rotation"] = SchemaHelpers.RotSchema,
            },
            ["required"] = new JArray { "template" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = ParticleTemplates.Create(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return ToolResult.Ok(r.Summary, new JObject { ["instanceId"] = r.InstanceID });
        }
    }
}
