using System;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.NavMesh
{
    /// Modern NavMesh authoring (per-surface). Lives in a secondary asmdef
    /// gated by `com.unity.ai.navigation`.
    public static class NavMeshSurfaceAuthor
    {
        public static OpResult ListSurfaces(JObject args)
        {
            var snapshot = Bootstrap.Snapshot;
            snapshot.RefreshIfNeeded();

            var sb = new System.Text.StringBuilder();
            int count = 0;
            foreach (var info in snapshot.All())
            {
                var go = EditorUtility.InstanceIDToObject(info.InstanceID) as GameObject;
                if (go == null) continue;
                var surface = go.GetComponent<NavMeshSurface>();
                if (surface == null) continue;
                count++;
                sb.AppendLine($"  #{go.GetInstanceID()} '{go.name}'  agentTypeID={surface.agentTypeID}  collect={surface.collectObjects}  layerMask=0x{surface.layerMask.value:X8}  useGeometry={surface.useGeometry}  baked={(surface.navMeshData != null ? "yes" : "no")}");
            }
            return new OpResult { Ok = true, Summary = $"{count} NavMeshSurface(s):\n{sb}" };
        }

        public static OpResult BakeSurface(JObject args)
        {
            var id = (int?)args["surfaceInstanceId"] ?? (int?)args["instanceId"] ?? 0;
            if (id != 0)
            {
                var go = EditorUtility.InstanceIDToObject(id) as GameObject;
                if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
                var surface = go.GetComponent<NavMeshSurface>();
                if (surface == null) return OpResult.Fail("no NavMeshSurface on GameObject");

                surface.BuildNavMesh();
                EditorUtility.SetDirty(surface);
                return OpResult.Success(id, $"baked NavMeshSurface on '{go.name}'");
            }

            // No id supplied → bake all surfaces in the active scene.
            int count = 0;
            foreach (var s in UnityEngine.Object.FindObjectsByType<NavMeshSurface>(UnityEngine.FindObjectsSortMode.None))
            {
                s.BuildNavMesh();
                EditorUtility.SetDirty(s);
                count++;
            }
            return OpResult.Success(0, $"baked {count} NavMeshSurface(s)");
        }

        public static OpResult AddSurface(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");

            var surface = go.GetComponent<NavMeshSurface>();
            if (surface == null) surface = Undo.AddComponent<NavMeshSurface>(go);
            else Undo.RecordObject(surface, "Configure NavMeshSurface");

            if (args["agentTypeID"] != null) surface.agentTypeID = (int)args["agentTypeID"];
            if (args["collectObjects"] != null
                && Enum.TryParse<CollectObjects>((string)args["collectObjects"], true, out var co))
                surface.collectObjects = co;
            if (args["useGeometry"] != null
                && Enum.TryParse<NavMeshCollectGeometry>((string)args["useGeometry"], true, out var ug))
                surface.useGeometry = ug;
            if (args["layerMask"] != null)
            {
                int mask = ParseLayerMask(args["layerMask"]);
                surface.layerMask = mask;
            }
            if (args["defaultArea"] != null) surface.defaultArea = (int)args["defaultArea"];
            if (args["overrideVoxelSize"] != null) surface.overrideVoxelSize = (bool)args["overrideVoxelSize"];
            if (args["voxelSize"] != null) surface.voxelSize = (float)args["voxelSize"];
            if (args["overrideTileSize"] != null) surface.overrideTileSize = (bool)args["overrideTileSize"];
            if (args["tileSize"] != null) surface.tileSize = (int)args["tileSize"];

            bool autoBake = (bool?)args["bake"] ?? false;
            if (autoBake) surface.BuildNavMesh();

            EditorUtility.SetDirty(surface);
            return OpResult.Success(surface.GetInstanceID(),
                $"NavMeshSurface ready on '{go.name}'  agentTypeID={surface.agentTypeID}  collect={surface.collectObjects}" +
                (autoBake ? " + baked" : ""));
        }

        public static OpResult AddModifier(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");

            bool useVolume = (bool?)args["volume"] ?? false;
            if (useVolume)
            {
                var mod = go.GetComponent<NavMeshModifierVolume>();
                if (mod == null) mod = Undo.AddComponent<NavMeshModifierVolume>(go);
                else Undo.RecordObject(mod, "Configure NavMeshModifierVolume");

                if (args["area"] != null) mod.area = ParseAreaIndex(args["area"]);
                if (args["center"] != null) mod.center = JsonHelpers.ParseVector3(args["center"]);
                if (args["size"] != null) mod.size = JsonHelpers.ParseVector3(args["size"], Vector3.one * 4f);

                EditorUtility.SetDirty(mod);
                return OpResult.Success(mod.GetInstanceID(),
                    $"NavMeshModifierVolume on '{go.name}'  area=#{mod.area}  size={mod.size}");
            }
            else
            {
                var mod = go.GetComponent<NavMeshModifier>();
                if (mod == null) mod = Undo.AddComponent<NavMeshModifier>(go);
                else Undo.RecordObject(mod, "Configure NavMeshModifier");

                if (args["overrideArea"] != null) mod.overrideArea = (bool)args["overrideArea"];
                if (args["area"] != null) { mod.area = ParseAreaIndex(args["area"]); mod.overrideArea = true; }
                if (args["ignoreFromBuild"] != null) mod.ignoreFromBuild = (bool)args["ignoreFromBuild"];
                if (args["applyToChildren"] != null) mod.applyToChildren = (bool)args["applyToChildren"];

                EditorUtility.SetDirty(mod);
                return OpResult.Success(mod.GetInstanceID(),
                    $"NavMeshModifier on '{go.name}'  overrideArea={mod.overrideArea}  area=#{mod.area}  ignore={mod.ignoreFromBuild}");
            }
        }

        // ---- helpers ----

        private static int ParseAreaIndex(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return 0;
            if (t.Type == JTokenType.Integer) return (int)t;
            if (t.Type == JTokenType.String)
            {
                var idx = UnityEngine.AI.NavMesh.GetAreaFromName((string)t);
                return idx >= 0 ? idx : 0;
            }
            return 0;
        }

        private static int ParseLayerMask(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return ~0;
            if (t.Type == JTokenType.Integer) return (int)t;
            if (t.Type == JTokenType.String) return UnityEngine.LayerMask.GetMask((string)t);
            if (t is JArray a)
            {
                var names = a.Where(x => x.Type == JTokenType.String).Select(x => (string)x).ToArray();
                return names.Length > 0 ? UnityEngine.LayerMask.GetMask(names) : ~0;
            }
            return ~0;
        }
    }
}
