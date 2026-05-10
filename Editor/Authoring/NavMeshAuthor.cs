using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AI;
using UnityEngine;
using UnityEngine.AI;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    /// Built-in NavMesh queries + legacy bake + NavMeshAgent runtime control.
    /// Uses `UnityEngine.AI.NavMesh` (always present) and
    /// `UnityEditor.AI.NavMeshBuilder` (legacy bake, also built-in).
    /// The modern per-surface system lives in the secondary NavMesh asmdef
    /// gated by `com.unity.ai.navigation`.
    public static class NavMeshAuthor
    {
        // ============== Queries ==============

        public static OpResult SamplePosition(JObject args)
        {
            var pos = JsonHelpers.ParseVector3(args["position"]);
            var maxDistance = (float?)args["maxDistance"] ?? 1f;
            var areaMask = ParseAreaMask(args["areaMask"]);

            if (!NavMesh.SamplePosition(pos, out var hit, maxDistance, areaMask))
                return new OpResult
                {
                    Ok = true,
                    Summary = $"sample_position: NO navmesh within {maxDistance}u of [{pos.x:F2},{pos.y:F2},{pos.z:F2}]",
                };

            return new OpResult
            {
                Ok = true,
                Summary = $"sample_position: HIT  projected=[{hit.position.x:F2},{hit.position.y:F2},{hit.position.z:F2}]" +
                          $"  distance={hit.distance:F3}  area={DescribeArea(hit.mask)}  normal=[{hit.normal.x:F2},{hit.normal.y:F2},{hit.normal.z:F2}]",
            };
        }

        public static OpResult CalculatePath(JObject args)
        {
            var start = JsonHelpers.ParseVector3(args["start"]);
            var end = JsonHelpers.ParseVector3(args["end"]);
            var areaMask = ParseAreaMask(args["areaMask"]);

            var path = new NavMeshPath();
            bool ok = NavMesh.CalculatePath(start, end, areaMask, path);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"calculate_path: ok={ok}  status={path.status}  corners={path.corners.Length}");
            float total = 0f;
            for (int i = 0; i + 1 < path.corners.Length; i++)
                total += Vector3.Distance(path.corners[i], path.corners[i + 1]);
            sb.AppendLine($"  totalDistance={total:F2}");
            int max = Mathf.Min(path.corners.Length, 30);
            for (int i = 0; i < max; i++)
            {
                var c = path.corners[i];
                sb.AppendLine($"    [{i}] [{c.x:F2}, {c.y:F2}, {c.z:F2}]");
            }
            if (path.corners.Length > 30) sb.AppendLine($"    [+{path.corners.Length - 30} more corners]");
            return new OpResult { Ok = true, Summary = sb.ToString() };
        }

        public static OpResult Raycast(JObject args)
        {
            var start = JsonHelpers.ParseVector3(args["start"]);
            var end = JsonHelpers.ParseVector3(args["end"]);
            var areaMask = ParseAreaMask(args["areaMask"]);

            // NavMesh.Raycast returns true when blocked — the hit data describes
            // where the line was severed. This naming is the opposite of
            // Physics.Raycast, document carefully.
            bool blocked = NavMesh.Raycast(start, end, out var hit, areaMask);
            var sb = new System.Text.StringBuilder();
            if (blocked)
            {
                sb.AppendLine("navmesh_raycast: BLOCKED (line crosses non-walkable / off-navmesh)");
                sb.AppendLine($"  block point=[{hit.position.x:F2}, {hit.position.y:F2}, {hit.position.z:F2}]  distance={hit.distance:F3}");
            }
            else
            {
                sb.AppendLine("navmesh_raycast: clear (entire line is on walkable navmesh)");
            }
            return new OpResult { Ok = true, Summary = sb.ToString() };
        }

        public static OpResult FindClosestEdge(JObject args)
        {
            var pos = JsonHelpers.ParseVector3(args["position"]);
            var areaMask = ParseAreaMask(args["areaMask"]);

            if (!NavMesh.FindClosestEdge(pos, out var hit, areaMask))
                return new OpResult { Ok = true, Summary = $"find_closest_edge: no edge found near [{pos.x:F2},{pos.y:F2},{pos.z:F2}]" };

            return new OpResult
            {
                Ok = true,
                Summary = $"find_closest_edge: edge at [{hit.position.x:F2},{hit.position.y:F2},{hit.position.z:F2}]" +
                          $"  distance={hit.distance:F3}  normal=[{hit.normal.x:F2},{hit.normal.y:F2},{hit.normal.z:F2}]  area={DescribeArea(hit.mask)}",
            };
        }

        // ============== Legacy bake ==============

        public static OpResult BakeLegacy(JObject args)
        {
            // Cancel any running bake first to avoid stacking.
            if (NavMeshBuilder.isRunning) NavMeshBuilder.Cancel();

            var asyncBake = (bool?)args["async"] ?? false;
            if (asyncBake) NavMeshBuilder.BuildNavMeshAsync();
            else NavMeshBuilder.BuildNavMesh();

            return new OpResult
            {
                Ok = true,
                Summary = asyncBake
                    ? "legacy NavMesh bake started (async — query NavMeshBuilder.isRunning to know completion)"
                    : "legacy NavMesh bake completed (sync)",
            };
        }

        // ============== Agent runtime + state read ==============

        public static OpResult SetAgent(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            var agent = go.GetComponent<NavMeshAgent>();
            if (agent == null) return OpResult.Fail("no NavMeshAgent on GameObject — add_component NavMeshAgent first");

            Undo.RecordObject(agent, "Set NavMeshAgent");

            // Optional setters
            if (args["destination"] != null)
            {
                var dest = JsonHelpers.ParseVector3(args["destination"]);
                agent.SetDestination(dest);
            }
            if (args["isStopped"] != null) agent.isStopped = (bool)args["isStopped"];
            if (args["speed"] != null) agent.speed = (float)args["speed"];
            if (args["acceleration"] != null) agent.acceleration = (float)args["acceleration"];
            if (args["angularSpeed"] != null) agent.angularSpeed = (float)args["angularSpeed"];
            if (args["stoppingDistance"] != null) agent.stoppingDistance = (float)args["stoppingDistance"];
            if (args["radius"] != null) agent.radius = (float)args["radius"];
            if (args["height"] != null) agent.height = (float)args["height"];
            if (args["baseOffset"] != null) agent.baseOffset = (float)args["baseOffset"];
            if (args["areaMask"] != null) agent.areaMask = ParseAreaMask(args["areaMask"]);
            if (args["resetPath"] != null && (bool)args["resetPath"]) agent.ResetPath();

            // State read — always returned.
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"NavMeshAgent on '{go.name}'");
            sb.AppendLine($"  isOnNavMesh={agent.isOnNavMesh}  hasPath={agent.hasPath}  pathPending={agent.pathPending}  isStopped={agent.isStopped}");
            sb.AppendLine($"  destination=[{agent.destination.x:F2},{agent.destination.y:F2},{agent.destination.z:F2}]" +
                          $"  remainingDistance={agent.remainingDistance:F3}  velocity={agent.velocity.magnitude:F2}");
            sb.AppendLine($"  speed={agent.speed}  accel={agent.acceleration}  angSpeed={agent.angularSpeed}  stopDist={agent.stoppingDistance}");
            sb.AppendLine($"  radius={agent.radius}  height={agent.height}  baseOffset={agent.baseOffset}  areaMask=0x{agent.areaMask:X8}");
            if (agent.path != null) sb.AppendLine($"  pathStatus={agent.path.status}  corners={agent.path.corners.Length}");
            return new OpResult { Ok = true, Summary = sb.ToString(), InstanceID = id };
        }

        // ============== Helpers ==============

        public static int ParseAreaMask(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return NavMesh.AllAreas;
            if (t.Type == JTokenType.Integer) return (int)t;
            if (t.Type == JTokenType.String)
            {
                var area = NavMesh.GetAreaFromName((string)t);
                return area >= 0 ? (1 << area) : NavMesh.AllAreas;
            }
            if (t is JArray arr)
            {
                int mask = 0;
                foreach (var n in arr)
                {
                    if (n.Type == JTokenType.String)
                    {
                        var area = NavMesh.GetAreaFromName((string)n);
                        if (area >= 0) mask |= (1 << area);
                    }
                    else if (n.Type == JTokenType.Integer)
                    {
                        mask |= (1 << (int)n);
                    }
                }
                return mask != 0 ? mask : NavMesh.AllAreas;
            }
            return NavMesh.AllAreas;
        }

        private static string DescribeArea(int mask)
        {
            if (mask == NavMesh.AllAreas) return "All";
            // First set bit indicates the primary area.
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    // No public reverse-lookup; return index + mask.
                    return $"#{i} (mask=0x{mask:X8})";
                }
            }
            return $"none (0x{mask:X8})";
        }
    }
}
