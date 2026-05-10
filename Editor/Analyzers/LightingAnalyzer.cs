using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Analyzers
{
    /// Heuristic lighting cost: counts realtime shadow-casting lights, flags
    /// static meshes missing lightmap UVs, and (for small scenes) reports
    /// overlapping point/spot lights with shadows enabled. Reflection-probe
    /// heuristics are out of scope in v1 — they're shader-pipeline specific
    /// and too noisy without per-pipeline tuning.
    public sealed class LightingAnalyzer : IAnalyzer
    {
        public string Id => "lighting";
        public string Category => "performance";

        private const int RealtimeShadowCasterWarning = 4;
        private const int OverlapAnalysisLimit = 64;

        public IEnumerable<Issue> Run(SceneSnapshot snapshot)
        {
            var pointSpotShadowCasters = new List<Light>();
            int realtimeShadowCasters = 0;
            int totalLights = 0;

            foreach (var info in snapshot.All())
            {
                if (!info.IsActive) continue;
                var go = EditorUtility.InstanceIDToObject(info.InstanceID) as GameObject;
                if (go == null) continue;

                foreach (var light in go.GetComponents<Light>())
                {
                    if (light == null || !light.enabled) continue;
                    totalLights++;

                    var bake = light.lightmapBakeType;
                    var castsShadows = light.shadows != LightShadows.None;
                    if (bake == LightmapBakeType.Realtime && castsShadows)
                        realtimeShadowCasters++;

                    if (castsShadows && (light.type == LightType.Point || light.type == LightType.Spot))
                        pointSpotShadowCasters.Add(light);
                }

                foreach (var mr in go.GetComponents<MeshRenderer>())
                {
                    var flags = GameObjectUtility.GetStaticEditorFlags(go);
                    if ((flags & StaticEditorFlags.ContributeGI) == 0) continue;

                    var mf = mr.GetComponent<MeshFilter>();
                    var mesh = mf != null ? mf.sharedMesh : null;
                    if (mesh == null) continue;

                    if (mesh.uv2 == null || mesh.uv2.Length == 0)
                    {
                        yield return new Issue(
                            analyzer: Id, severity: IssueSeverity.Warning, category: Category,
                            message: $"MeshRenderer on '{info.Name}' contributes to GI but mesh " +
                                     $"'{mesh.name}' has no lightmap UVs (uv2). Re-import with " +
                                     "'Generate Lightmap UVs' or it will receive no baked lighting.",
                            instanceId: info.InstanceID,
                            gameObjectPath: snapshot.GetPath(info.InstanceID));
                    }
                }
            }

            if (realtimeShadowCasters > RealtimeShadowCasterWarning)
            {
                yield return new Issue(
                    analyzer: Id, severity: IssueSeverity.Warning, category: Category,
                    message: $"{realtimeShadowCasters} realtime shadow-casting lights " +
                             $"(> {RealtimeShadowCasterWarning}). Each costs a shadow map render. " +
                             "Switch some to Mixed/Baked or disable shadows.");
            }

            if (pointSpotShadowCasters.Count <= OverlapAnalysisLimit)
            {
                for (int i = 0; i < pointSpotShadowCasters.Count; i++)
                {
                    for (int j = i + 1; j < pointSpotShadowCasters.Count; j++)
                    {
                        var a = pointSpotShadowCasters[i];
                        var b = pointSpotShadowCasters[j];
                        var threshold = (a.range + b.range) * 0.5f;
                        if (Vector3.Distance(a.transform.position, b.transform.position) < threshold)
                        {
                            yield return new Issue(
                                analyzer: Id, severity: IssueSeverity.Info, category: Category,
                                message: $"Point/Spot lights '{a.name}' and '{b.name}' overlap with " +
                                         "shadows enabled — they re-render the same shadow casters.");
                        }
                    }
                }
            }
            else
            {
                yield return new Issue(
                    analyzer: Id, severity: IssueSeverity.Info, category: Category,
                    message: $"{pointSpotShadowCasters.Count} point/spot shadow-casters " +
                             $"(> {OverlapAnalysisLimit}); overlap analysis skipped to keep cost bounded.");
            }

            yield return new Issue(
                analyzer: Id, severity: IssueSeverity.Info, category: Category,
                message: $"Lighting: {totalLights} active lights, {realtimeShadowCasters} realtime shadow casters.");
        }
    }
}
