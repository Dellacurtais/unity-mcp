using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    /// Module-typed setters for ParticleSystem. Unity's ParticleSystem modules
    /// are returned as struct-like facades that internally write back through
    /// the native ParticleSystem on every property assignment, so reading the
    /// module via `ps.main` and assigning fields is the canonical pattern.
    public static class ParticleEditor
    {
        public static OpResult SetModule(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var moduleName = ((string)args["module"] ?? "").ToLowerInvariant();
            var fields = args["fields"] as JObject;
            if (string.IsNullOrEmpty(moduleName) || fields == null)
                return OpResult.Fail("module + fields required");

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            var ps = go.GetComponent<ParticleSystem>();
            if (ps == null) return OpResult.Fail("no ParticleSystem on this GameObject");

            Undo.RecordObject(ps, "Set Particle Module");

            var changes = new List<string>();
            switch (moduleName)
            {
                case "main": SetMain(ps, fields, changes); break;
                case "emission": SetEmission(ps, fields, changes); break;
                case "shape": SetShape(ps, fields, changes); break;
                case "coloroverlifetime": SetColorOverLifetime(ps, fields, changes); break;
                case "sizeoverlifetime": SetSizeOverLifetime(ps, fields, changes); break;
                case "velocityoverlifetime": SetVelocityOverLifetime(ps, fields, changes); break;
                case "rotationoverlifetime": SetRotationOverLifetime(ps, fields, changes); break;
                case "noise": SetNoise(ps, fields, changes); break;
                case "trails": SetTrails(ps, fields, changes); break;
                case "renderer": SetRenderer(ps, fields, changes); break;
                case "collision": SetCollision(ps, fields, changes); break;
                default: return OpResult.Fail($"unknown module '{moduleName}'. Known: main, emission, shape, colorOverLifetime, sizeOverLifetime, velocityOverLifetime, rotationOverLifetime, noise, trails, renderer, collision");
            }
            return OpResult.Success(id, $"set {moduleName}: {string.Join(", ", changes)}");
        }

        public static OpResult PlayPreview(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var action = ((string)args["action"] ?? "play").ToLowerInvariant();
            var time = (float?)args["time"] ?? 0f;
            var withChildren = (bool?)args["withChildren"] ?? true;

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            var ps = go.GetComponent<ParticleSystem>();
            if (ps == null) return OpResult.Fail("no ParticleSystem on this GameObject");

            switch (action)
            {
                case "play": ps.Play(withChildren); break;
                case "stop": ps.Stop(withChildren); break;
                case "pause": ps.Pause(withChildren); break;
                case "clear": ps.Clear(withChildren); break;
                case "restart": ps.Stop(withChildren); ps.Clear(withChildren); ps.Play(withChildren); break;
                case "simulate":
                    ps.Simulate(time > 0 ? time : 1f, withChildren, restart: true);
                    break;
                default:
                    return OpResult.Fail($"unknown action '{action}' (use play/stop/pause/clear/restart/simulate)");
            }
            return OpResult.Success(id, action == "simulate" ? $"simulate {time}s" : action);
        }

        // ---- module setters ----

        private static void SetMain(ParticleSystem ps, JObject f, List<string> log)
        {
            var m = ps.main;
            if (f["duration"] != null) { m.duration = (float)f["duration"]; log.Add("duration"); }
            if (f["loop"] != null) { m.loop = (bool)f["loop"]; log.Add("loop"); }
            if (f["prewarm"] != null) { m.prewarm = (bool)f["prewarm"]; log.Add("prewarm"); }
            if (f["startDelay"] != null) { m.startDelay = ParseMinMax(f["startDelay"]); log.Add("startDelay"); }
            if (f["startLifetime"] != null) { m.startLifetime = ParseMinMax(f["startLifetime"]); log.Add("startLifetime"); }
            if (f["startSpeed"] != null) { m.startSpeed = ParseMinMax(f["startSpeed"]); log.Add("startSpeed"); }
            if (f["startSize"] != null) { m.startSize = ParseMinMax(f["startSize"]); log.Add("startSize"); }
            if (f["startRotation"] != null) { m.startRotation = ParseMinMax(f["startRotation"]); log.Add("startRotation"); }
            if (f["startColor"] != null) { m.startColor = ParseGradient(f["startColor"]); log.Add("startColor"); }
            if (f["gravityModifier"] != null) { m.gravityModifier = ParseMinMax(f["gravityModifier"]); log.Add("gravityModifier"); }
            if (f["maxParticles"] != null) { m.maxParticles = (int)f["maxParticles"]; log.Add("maxParticles"); }
            if (f["simulationSpeed"] != null) { m.simulationSpeed = (float)f["simulationSpeed"]; log.Add("simulationSpeed"); }
            if (f["simulationSpace"] != null && System.Enum.TryParse((string)f["simulationSpace"], true, out ParticleSystemSimulationSpace ss)) { m.simulationSpace = ss; log.Add("simulationSpace"); }
        }

        private static void SetEmission(ParticleSystem ps, JObject f, List<string> log)
        {
            var em = ps.emission;
            if (f["enabled"] != null) { em.enabled = (bool)f["enabled"]; log.Add("enabled"); }
            if (f["rateOverTime"] != null) { em.rateOverTime = ParseMinMax(f["rateOverTime"]); log.Add("rateOverTime"); }
            if (f["rateOverDistance"] != null) { em.rateOverDistance = ParseMinMax(f["rateOverDistance"]); log.Add("rateOverDistance"); }
            if (f["bursts"] is JArray ba)
            {
                var bursts = new List<ParticleSystem.Burst>();
                foreach (var bo in ba)
                {
                    if (bo is JObject b)
                    {
                        bursts.Add(new ParticleSystem.Burst(
                            (float?)b["time"] ?? 0f,
                            (short)((int?)b["count"] ?? 10)));
                    }
                }
                em.SetBursts(bursts.ToArray());
                log.Add($"bursts({bursts.Count})");
            }
        }

        private static void SetShape(ParticleSystem ps, JObject f, List<string> log)
        {
            var sh = ps.shape;
            if (f["enabled"] != null) { sh.enabled = (bool)f["enabled"]; log.Add("enabled"); }
            if (f["shapeType"] != null && System.Enum.TryParse((string)f["shapeType"], true, out ParticleSystemShapeType st)) { sh.shapeType = st; log.Add("shapeType"); }
            if (f["radius"] != null) { sh.radius = (float)f["radius"]; log.Add("radius"); }
            if (f["angle"] != null) { sh.angle = (float)f["angle"]; log.Add("angle"); }
            if (f["scale"] != null) { sh.scale = JsonHelpers.ParseVector3(f["scale"], Vector3.one); log.Add("scale"); }
            if (f["position"] != null) { sh.position = JsonHelpers.ParseVector3(f["position"]); log.Add("position"); }
            if (f["rotation"] != null)
            {
                var q = JsonHelpers.ParseRotation(f["rotation"]);
                sh.rotation = q.eulerAngles;
                log.Add("rotation");
            }
        }

        private static void SetColorOverLifetime(ParticleSystem ps, JObject f, List<string> log)
        {
            var col = ps.colorOverLifetime;
            if (f["enabled"] != null) { col.enabled = (bool)f["enabled"]; log.Add("enabled"); }
            if (f["color"] != null) { col.color = ParseGradient(f["color"]); log.Add("color"); }
        }

        private static void SetSizeOverLifetime(ParticleSystem ps, JObject f, List<string> log)
        {
            var sz = ps.sizeOverLifetime;
            if (f["enabled"] != null) { sz.enabled = (bool)f["enabled"]; log.Add("enabled"); }
            if (f["size"] != null) { sz.size = ParseMinMax(f["size"]); log.Add("size"); }
        }

        private static void SetVelocityOverLifetime(ParticleSystem ps, JObject f, List<string> log)
        {
            var v = ps.velocityOverLifetime;
            if (f["enabled"] != null) { v.enabled = (bool)f["enabled"]; log.Add("enabled"); }
            if (f["x"] != null) { v.x = ParseMinMax(f["x"]); log.Add("x"); }
            if (f["y"] != null) { v.y = ParseMinMax(f["y"]); log.Add("y"); }
            if (f["z"] != null) { v.z = ParseMinMax(f["z"]); log.Add("z"); }
            if (f["space"] != null && System.Enum.TryParse((string)f["space"], true, out ParticleSystemSimulationSpace ss)) { v.space = ss; log.Add("space"); }
        }

        private static void SetRotationOverLifetime(ParticleSystem ps, JObject f, List<string> log)
        {
            var r = ps.rotationOverLifetime;
            if (f["enabled"] != null) { r.enabled = (bool)f["enabled"]; log.Add("enabled"); }
            if (f["z"] != null) { r.z = ParseMinMax(f["z"]); log.Add("z"); }
        }

        private static void SetNoise(ParticleSystem ps, JObject f, List<string> log)
        {
            var n = ps.noise;
            if (f["enabled"] != null) { n.enabled = (bool)f["enabled"]; log.Add("enabled"); }
            if (f["strength"] != null) { n.strength = ParseMinMax(f["strength"]); log.Add("strength"); }
            if (f["frequency"] != null) { n.frequency = (float)f["frequency"]; log.Add("frequency"); }
            if (f["scrollSpeed"] != null) { n.scrollSpeed = ParseMinMax(f["scrollSpeed"]); log.Add("scrollSpeed"); }
            if (f["octaveCount"] != null) { n.octaveCount = (int)f["octaveCount"]; log.Add("octaveCount"); }
            if (f["damping"] != null) { n.damping = (bool)f["damping"]; log.Add("damping"); }
        }

        private static void SetTrails(ParticleSystem ps, JObject f, List<string> log)
        {
            var t = ps.trails;
            if (f["enabled"] != null) { t.enabled = (bool)f["enabled"]; log.Add("enabled"); }
            if (f["lifetime"] != null) { t.lifetime = ParseMinMax(f["lifetime"]); log.Add("lifetime"); }
            if (f["ratio"] != null) { t.ratio = (float)f["ratio"]; log.Add("ratio"); }
            if (f["dieWithParticles"] != null) { t.dieWithParticles = (bool)f["dieWithParticles"]; log.Add("dieWithParticles"); }
            if (f["widthOverTrail"] != null) { t.widthOverTrail = ParseMinMax(f["widthOverTrail"]); log.Add("widthOverTrail"); }
            if (f["colorOverLifetime"] != null) { t.colorOverLifetime = ParseGradient(f["colorOverLifetime"]); log.Add("colorOverLifetime"); }
        }

        private static void SetRenderer(ParticleSystem ps, JObject f, List<string> log)
        {
            var r = ps.GetComponent<ParticleSystemRenderer>();
            if (r == null) return;
            if (f["renderMode"] != null && System.Enum.TryParse((string)f["renderMode"], true, out ParticleSystemRenderMode rm))
            { r.renderMode = rm; log.Add("renderMode"); }
            if (f["sortingOrder"] != null) { r.sortingOrder = (int)f["sortingOrder"]; log.Add("sortingOrder"); }
            if (f["sortingLayerName"] != null) { r.sortingLayerName = (string)f["sortingLayerName"]; log.Add("sortingLayerName"); }
            if (f["material"] != null)
            {
                var matRef = (string)f["material"];
                var path = matRef.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase)
                    ? matRef
                    : AssetDatabase.GUIDToAssetPath(matRef);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat != null) { r.sharedMaterial = mat; log.Add("material"); }
            }
        }

        private static void SetCollision(ParticleSystem ps, JObject f, List<string> log)
        {
            var c = ps.collision;
            if (f["enabled"] != null) { c.enabled = (bool)f["enabled"]; log.Add("enabled"); }
            if (f["type"] != null && System.Enum.TryParse((string)f["type"], true, out ParticleSystemCollisionType ct)) { c.type = ct; log.Add("type"); }
            if (f["bounce"] != null) { c.bounce = ParseMinMax(f["bounce"]); log.Add("bounce"); }
            if (f["dampen"] != null) { c.dampen = ParseMinMax(f["dampen"]); log.Add("dampen"); }
            if (f["lifetimeLoss"] != null) { c.lifetimeLoss = ParseMinMax(f["lifetimeLoss"]); log.Add("lifetimeLoss"); }
        }

        // ---- parsers ----

        public static ParticleSystem.MinMaxCurve ParseMinMax(JToken t)
        {
            if (t == null) return new ParticleSystem.MinMaxCurve(1f);
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return new ParticleSystem.MinMaxCurve((float)t);
            if (t is JArray a && a.Count >= 2) return new ParticleSystem.MinMaxCurve((float)a[0], (float)a[1]);
            if (t is JObject o)
            {
                var min = (float?)o["min"];
                var max = (float?)o["max"];
                if (min.HasValue && max.HasValue) return new ParticleSystem.MinMaxCurve(min.Value, max.Value);
                if (o["constant"] != null) return new ParticleSystem.MinMaxCurve((float)o["constant"]);
                if (o["multiplier"] != null) return new ParticleSystem.MinMaxCurve((float)o["multiplier"]);
            }
            return new ParticleSystem.MinMaxCurve(1f);
        }

        public static ParticleSystem.MinMaxGradient ParseGradient(JToken t)
        {
            if (t == null) return new ParticleSystem.MinMaxGradient(Color.white);
            if (t.Type == JTokenType.String || t is JArray || (t is JObject o1 && (o1["r"] != null || o1["g"] != null || o1["b"] != null)))
                return new ParticleSystem.MinMaxGradient(JsonHelpers.ParseColor(t));
            if (t is JObject o)
            {
                if (o["min"] != null && o["max"] != null)
                    return new ParticleSystem.MinMaxGradient(JsonHelpers.ParseColor(o["min"]), JsonHelpers.ParseColor(o["max"]));
                if (o["constant"] != null)
                    return new ParticleSystem.MinMaxGradient(JsonHelpers.ParseColor(o["constant"]));
                if (o["gradient"] is JArray stops)
                {
                    var g = new Gradient();
                    var ck = new List<GradientColorKey>();
                    var ak = new List<GradientAlphaKey>();
                    foreach (var s in stops)
                    {
                        if (s is JObject so)
                        {
                            var time = (float?)so["time"] ?? 0f;
                            var c = JsonHelpers.ParseColor(so["color"]);
                            ck.Add(new GradientColorKey(c, time));
                            ak.Add(new GradientAlphaKey(c.a, time));
                        }
                    }
                    g.SetKeys(ck.ToArray(), ak.ToArray());
                    return new ParticleSystem.MinMaxGradient(g);
                }
            }
            return new ParticleSystem.MinMaxGradient(Color.white);
        }
    }
}
