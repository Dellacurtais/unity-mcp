using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Write
{
    public sealed class SetParticleModuleTool : IMcpTool
    {
        public string Name => "set_particle_module";
        public string Description =>
            "Configures a ParticleSystem module (typed). Pick `module`: main, emission, shape, colorOverLifetime, sizeOverLifetime, velocityOverLifetime, rotationOverLifetime, noise, trails, renderer, collision. Pass per-module `fields` map. " +
            "Numeric fields accept either a scalar (constant), [min,max] (random between), or {constant:N} / {min:N, max:N} / {multiplier:N}. " +
            "Color fields accept hex string, [r,g,b,a], {min,max}, or {gradient:[{time,color}]}. " +
            "Examples: " +
            "main → {duration, loop, startLifetime, startSpeed, startSize, startColor, startRotation, gravityModifier, maxParticles, simulationSpeed, simulationSpace}; " +
            "emission → {enabled, rateOverTime, rateOverDistance, bursts:[{time,count}]}; " +
            "shape → {shapeType (Cone/Sphere/Box/SingleSidedEdge/Hemisphere/etc), angle, radius, scale, position, rotation}; " +
            "trails → {enabled, lifetime, ratio, dieWithParticles, widthOverTrail, colorOverLifetime}; " +
            "renderer → {renderMode, sortingOrder, sortingLayerName, material (GUID or path)}.";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["instanceId"] = new JObject { ["type"] = "integer" },
                ["module"] = new JObject { ["type"] = "string" },
                ["fields"] = new JObject { ["type"] = "object" },
            },
            ["required"] = new JArray { "instanceId", "module", "fields" },
            ["additionalProperties"] = false,
        };

        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var r = ParticleEditor.SetModule(args);
            if (!r.Ok) return ToolResult.Error("OP_FAILED", r.Error);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return ToolResult.Ok(r.Summary);
        }
    }
}
