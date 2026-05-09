using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    /// Creates ParticleSystems with hand-tuned defaults for common effects so the
    /// LLM can spawn good-looking emitters from a single tool call. Per-template
    /// behaviour can be tweaked further by passing color/duration/rate args, or
    /// by following up with set_property calls on specific modules.
    public static class ParticleTemplates
    {
        public static OpResult Create(JObject args)
        {
            var template = ((string)args["template"] ?? "default").ToLowerInvariant();
            var name = (string)args["name"] ?? $"Particles ({template})";
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create Particle System");
            var ps = go.AddComponent<ParticleSystem>();
            ApplyTemplate(ps, template);

            // Optional overrides — only applied when the agent passed them.
            var main = ps.main;
            if (args["duration"] != null) main.duration = (float)args["duration"];
            if (args["startColor"] != null) main.startColor = new ParticleSystem.MinMaxGradient(JsonHelpers.ParseColor(args["startColor"]));
            if (args["startSize"] != null) main.startSize = (float)args["startSize"];
            if (args["startSpeed"] != null) main.startSpeed = (float)args["startSpeed"];
            if (args["startLifetime"] != null) main.startLifetime = (float)args["startLifetime"];

            if (args["emissionRate"] != null)
            {
                var emission = ps.emission;
                emission.rateOverTime = (float)args["emissionRate"];
            }

            var parent = JsonHelpers.ResolveParent(args["parent"]);
            if (parent != null) Undo.SetTransformParent(go.transform, parent.transform, "Parent");
            go.transform.localPosition = JsonHelpers.ParseVector3(args["position"]);
            go.transform.localRotation = JsonHelpers.ParseRotation(args["rotation"]);

            return OpResult.Success(go.GetInstanceID(), $"created particle system '{name}' from template '{template}'");
        }

        private static void ApplyTemplate(ParticleSystem ps, string template)
        {
            // We touch the modules through their struct accessors; assignment
            // back is implicit because Unity returns mutable structs that
            // write through the underlying ParticleSystem when fields are set.
            var main = ps.main;
            var emission = ps.emission;
            var shape = ps.shape;
            var color = ps.colorOverLifetime;
            var size = ps.sizeOverLifetime;

            switch (template)
            {
                case "smoke":
                    main.duration = 5f;
                    main.loop = true;
                    main.startLifetime = 3f;
                    main.startSpeed = 0.6f;
                    main.startSize = 1.2f;
                    main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.5f, 0.5f, 0.5f, 0.5f));
                    main.gravityModifier = -0.05f;
                    emission.rateOverTime = 8f;
                    shape.shapeType = ParticleSystemShapeType.Cone;
                    shape.angle = 15f;
                    shape.radius = 0.3f;
                    color.enabled = true;
                    color.color = new ParticleSystem.MinMaxGradient(FadeOut(new Color(0.5f, 0.5f, 0.5f, 0.5f)));
                    size.enabled = true;
                    size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, 0.5f, 1, 1.8f));
                    break;
                case "fire":
                    main.duration = 5f;
                    main.loop = true;
                    main.startLifetime = 1.2f;
                    main.startSpeed = 1.5f;
                    main.startSize = 0.5f;
                    main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.6f, 0.1f, 1f), new Color(1f, 0.9f, 0.3f, 1f));
                    emission.rateOverTime = 30f;
                    shape.shapeType = ParticleSystemShapeType.Cone;
                    shape.angle = 10f;
                    shape.radius = 0.2f;
                    color.enabled = true;
                    color.color = new ParticleSystem.MinMaxGradient(FireGradient());
                    size.enabled = true;
                    size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, 0.4f, 1, 1.2f));
                    break;
                case "sparks":
                    main.duration = 1f;
                    main.loop = false;
                    main.startLifetime = 0.6f;
                    main.startSpeed = 5f;
                    main.startSize = 0.05f;
                    main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.85f, 0.3f, 1f));
                    main.gravityModifier = 0.5f;
                    emission.rateOverTime = 0f;
                    emission.SetBursts(new[] { new ParticleSystem.Burst(0.0f, 80) });
                    shape.shapeType = ParticleSystemShapeType.Sphere;
                    shape.radius = 0.05f;
                    color.enabled = true;
                    color.color = new ParticleSystem.MinMaxGradient(FadeOut(new Color(1f, 0.85f, 0.3f, 1f)));
                    break;
                case "dust":
                    main.duration = 8f;
                    main.loop = true;
                    main.startLifetime = 4f;
                    main.startSpeed = 0.2f;
                    main.startSize = 0.3f;
                    main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.85f, 0.78f, 0.6f, 0.4f));
                    main.gravityModifier = -0.02f;
                    emission.rateOverTime = 6f;
                    shape.shapeType = ParticleSystemShapeType.Box;
                    shape.scale = new Vector3(2f, 0.1f, 2f);
                    color.enabled = true;
                    color.color = new ParticleSystem.MinMaxGradient(FadeOut(new Color(0.85f, 0.78f, 0.6f, 0.4f)));
                    break;
                case "trail":
                    main.duration = 2f;
                    main.loop = false;
                    main.startLifetime = 1f;
                    main.startSpeed = 0f;
                    main.startSize = 0.2f;
                    main.startColor = new ParticleSystem.MinMaxGradient(Color.white);
                    emission.rateOverTime = 50f;
                    shape.shapeType = ParticleSystemShapeType.SingleSidedEdge;
                    shape.radius = 0.1f;
                    var trails = ps.trails;
                    trails.enabled = true;
                    trails.lifetime = 0.4f;
                    break;
                default:
                    // leave Unity defaults
                    break;
            }
        }

        private static Gradient FadeOut(Color c)
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(c, 0f), new GradientColorKey(c, 1f) },
                new[] { new GradientAlphaKey(c.a, 0f), new GradientAlphaKey(0f, 1f) });
            return g;
        }

        private static Gradient FireGradient()
        {
            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.9f, 0.4f), 0f),
                    new GradientColorKey(new Color(1f, 0.5f, 0.1f), 0.5f),
                    new GradientColorKey(new Color(0.2f, 0.05f, 0.05f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.6f, 0.5f),
                    new GradientAlphaKey(0f, 1f),
                });
            return g;
        }
    }
}
