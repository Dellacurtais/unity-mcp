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
            "emission → {enabled, rateOverTime, rateOverDistance, bursts:[{time,count|min/max,cycles,interval,probability}]}; " +
            "shape → {shapeType (Cone/Sphere/Box/SingleSidedEdge/Hemisphere/etc), angle, radius, scale, position, rotation}; " +
            "sizeOverLifetime → {enabled, size, separateAxes, x, y, z}; velocityOverLifetime → {enabled, x, y, z, orbitalX/Y/Z, orbitalOffsetX/Y/Z, radial, speedModifier, space}; rotationOverLifetime → {enabled, separateAxes, x, y, z}; " +
            "trails → {enabled, lifetime, ratio, dieWithParticles, widthOverTrail, colorOverLifetime}; " +
            "renderer → {renderMode, alignment, sortingOrder, sortingLayerName, minParticleSize, maxParticleSize, material, trailMaterial, mesh}; asset fields accept GUID or Assets/ path.";

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
            return ToolResult.Ok(r.Summary);
        }
    }
}
