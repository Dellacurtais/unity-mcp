using Maicon.SceneAnalyzer.Editor.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using LegacyBuilder = UnityEditor.AI.NavMeshBuilder;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
#pragma warning disable CS0618
    public static class NavMeshAuthor
    {
        public static OpResult SamplePosition(JObject args)
        {
            if (!TryParseVector3(args["position"], out var position, out var vectorError))
                return OpResult.Fail($"position: {vectorError}");
            if (!TryReadNonNegative(args["maxDistance"], 1f, out var maxDistance, out var distanceError))
                return OpResult.Fail($"maxDistance: {distanceError}");
            if (!TryBuildQuery(args, out var areaMask, out var agentTypeId, out var filter, out var queryError))
                return OpResult.Fail(queryError);

            var found = agentTypeId.HasValue
                ? NavMesh.SamplePosition(position, out var hit, maxDistance, filter)
                : NavMesh.SamplePosition(position, out hit, maxDistance, areaMask);
            var data = new JObject
            {
                ["found"] = found,
                ["requestedPosition"] = JsonHelpers.Vec3(position),
                ["maxDistance"] = maxDistance,
                ["areaMask"] = areaMask,
            };
            if (agentTypeId.HasValue) data["agentTypeID"] = agentTypeId.Value;
            if (found) data["hit"] = HitJson(hit);

            return new OpResult
            {
                Ok = true,
                Data = data,
                Summary = found
                    ? $"sample_position: HIT projected={Format(hit.position)} distance={hit.distance:F3} area={DescribeArea(hit.mask)}"
                    : $"sample_position: no NavMesh within {maxDistance:F3} units of {Format(position)}",
            };
        }

        public static OpResult CalculatePath(JObject args)
        {
            if (!TryParseVector3(args["start"], out var start, out var startError))
                return OpResult.Fail($"start: {startError}");
            if (!TryParseVector3(args["end"], out var end, out var endError))
                return OpResult.Fail($"end: {endError}");
            if (!TryBuildQuery(args, out var areaMask, out var agentTypeId, out var filter, out var queryError))
                return OpResult.Fail(queryError);

            var path = new NavMeshPath();
            var found = agentTypeId.HasValue
                ? NavMesh.CalculatePath(start, end, filter, path)
                : NavMesh.CalculatePath(start, end, areaMask, path);
            var corners = new JArray(path.corners.Select(JsonHelpers.Vec3));
            var totalDistance = 0f;
            for (var i = 0; i + 1 < path.corners.Length; i++)
                totalDistance += Vector3.Distance(path.corners[i], path.corners[i + 1]);

            var data = new JObject
            {
                ["found"] = found,
                ["status"] = path.status.ToString(),
                ["corners"] = corners,
                ["totalDistance"] = totalDistance,
                ["areaMask"] = areaMask,
            };
            if (agentTypeId.HasValue) data["agentTypeID"] = agentTypeId.Value;
            return new OpResult
            {
                Ok = true,
                Data = data,
                Summary = $"calculate_path: found={found} status={path.status} corners={path.corners.Length} totalDistance={totalDistance:F3}",
            };
        }

        public static OpResult Raycast(JObject args)
        {
            if (!TryParseVector3(args["start"], out var start, out var startError))
                return OpResult.Fail($"start: {startError}");
            if (!TryParseVector3(args["end"], out var end, out var endError))
                return OpResult.Fail($"end: {endError}");
            if (!TryReadNonNegative(args["sampleDistance"], 0.1f, out var sampleDistance, out var sampleError))
                return OpResult.Fail($"sampleDistance: {sampleError}");
            if (!TryBuildQuery(args, out var areaMask, out var agentTypeId, out var filter, out var queryError))
                return OpResult.Fail(queryError);

            var startFound = agentTypeId.HasValue
                ? NavMesh.SamplePosition(start, out var mappedStart, sampleDistance, filter)
                : NavMesh.SamplePosition(start, out mappedStart, sampleDistance, areaMask);
            var data = new JObject
            {
                ["found"] = startFound,
                ["blocked"] = false,
                ["clear"] = false,
                ["start"] = JsonHelpers.Vec3(start),
                ["end"] = JsonHelpers.Vec3(end),
                ["areaMask"] = areaMask,
                ["sampleDistance"] = sampleDistance,
            };
            if (agentTypeId.HasValue) data["agentTypeID"] = agentTypeId.Value;
            if (!startFound)
                return new OpResult { Ok = true, Data = data, Summary = $"navmesh_raycast: start is not on a matching NavMesh within {sampleDistance:F3} units" };

            var blocked = agentTypeId.HasValue
                ? NavMesh.Raycast(mappedStart.position, end, out var hit, filter)
                : NavMesh.Raycast(mappedStart.position, end, out hit, areaMask);
            data["blocked"] = blocked;
            data["clear"] = !blocked;
            data["mappedStart"] = JsonHelpers.Vec3(mappedStart.position);
            if (blocked) data["hit"] = HitJson(hit);
            return new OpResult
            {
                Ok = true,
                Data = data,
                Summary = blocked
                    ? $"navmesh_raycast: BLOCKED at {Format(hit.position)} distance={hit.distance:F3}"
                    : "navmesh_raycast: CLEAR",
            };
        }

        public static OpResult FindClosestEdge(JObject args)
        {
            if (!TryParseVector3(args["position"], out var position, out var vectorError))
                return OpResult.Fail($"position: {vectorError}");
            if (!TryBuildQuery(args, out var areaMask, out var agentTypeId, out var filter, out var queryError))
                return OpResult.Fail(queryError);

            var found = agentTypeId.HasValue
                ? NavMesh.FindClosestEdge(position, out var hit, filter)
                : NavMesh.FindClosestEdge(position, out hit, areaMask);
            var data = new JObject
            {
                ["found"] = found,
                ["requestedPosition"] = JsonHelpers.Vec3(position),
                ["areaMask"] = areaMask,
            };
            if (agentTypeId.HasValue) data["agentTypeID"] = agentTypeId.Value;
            if (found) data["hit"] = HitJson(hit);
            return new OpResult
            {
                Ok = true,
                Data = data,
                Summary = found
                    ? $"find_closest_edge: edge={Format(hit.position)} distance={hit.distance:F3} area={DescribeArea(hit.mask)}"
                    : $"find_closest_edge: no matching edge near {Format(position)}",
            };
        }

        public static OpResult BakeLegacy(JObject args)
        {
            if (LegacyBuilder.isRunning)
                return OpResult.Fail("a legacy NavMesh bake is already running");
            LegacyBuilder.BuildNavMesh();
            return new OpResult
            {
                Ok = true,
                Summary = "legacy NavMesh bake completed synchronously",
                Data = new JObject { ["completed"] = true, ["mode"] = "synchronous" },
            };
        }

        public static OpResult SetAgent(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            if (!TryResolveSceneGameObject(id, out var go, out var targetError)) return OpResult.Fail(targetError);
            var agent = go.GetComponent<NavMeshAgent>();
            if (agent == null) return OpResult.Fail("no NavMeshAgent on GameObject");

            if (!TryReadOptionalNonNegative(args, "speed", out var speed, out var error) ||
                !TryReadOptionalNonNegative(args, "acceleration", out var acceleration, out error) ||
                !TryReadOptionalNonNegative(args, "angularSpeed", out var angularSpeed, out error) ||
                !TryReadOptionalNonNegative(args, "stoppingDistance", out var stoppingDistance, out error) ||
                !TryReadOptionalNonNegative(args, "radius", out var radius, out error) ||
                !TryReadOptionalNonNegative(args, "height", out var height, out error))
                return OpResult.Fail(error);
            if (!TryReadOptionalFinite(args, "baseOffset", out var baseOffset, out error)) return OpResult.Fail(error);

            var areaMask = agent.areaMask;
            if (args["areaMask"] != null && !TryParseAreaMask(args["areaMask"], out areaMask, out error))
                return OpResult.Fail($"areaMask: {error}");

            var hasDestination = args["destination"] != null;
            var hasRuntimeCommand = hasDestination || args["isStopped"] != null || ((bool?)args["resetPath"] ?? false);
            var onNavMesh = agent.isActiveAndEnabled && agent.isOnNavMesh;
            if (hasRuntimeCommand && !onNavMesh)
                return OpResult.Fail("destination, isStopped, and resetPath require an active NavMeshAgent placed on a NavMesh");

            var destination = default(Vector3);
            if (hasDestination)
            {
                if (!TryParseVector3(args["destination"], out destination, out error))
                    return OpResult.Fail($"destination: {error}");
                if (!TryValidateAgentType(agent.agentTypeID, out error)) return OpResult.Fail(error);
                var path = new NavMeshPath();
                var filter = new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = areaMask };
                if (!NavMesh.CalculatePath(agent.nextPosition, destination, filter, path))
                    return OpResult.Fail("destination does not produce a complete or partial path for the agent's type and area mask");
            }

            Undo.RecordObject(agent, "Set NavMeshAgent");
            if (speed.HasValue) agent.speed = speed.Value;
            if (acceleration.HasValue) agent.acceleration = acceleration.Value;
            if (angularSpeed.HasValue) agent.angularSpeed = angularSpeed.Value;
            if (stoppingDistance.HasValue) agent.stoppingDistance = stoppingDistance.Value;
            if (radius.HasValue) agent.radius = radius.Value;
            if (height.HasValue) agent.height = height.Value;
            if (baseOffset.HasValue) agent.baseOffset = baseOffset.Value;
            if (args["areaMask"] != null) agent.areaMask = areaMask;
            if (args["isStopped"] != null) agent.isStopped = (bool)args["isStopped"];
            if ((bool?)args["resetPath"] ?? false) agent.ResetPath();
            if (hasDestination && !agent.SetDestination(destination))
                return OpResult.Fail("SetDestination failed after path preflight");

            EditorUtility.SetDirty(agent);
            PrefabUtility.RecordPrefabInstancePropertyModifications(agent);
            EditorSceneManager.MarkSceneDirty(go.scene);
            var data = AgentJson(go, agent);
            return new OpResult { Ok = true, Summary = $"configured NavMeshAgent on '{go.name}'", InstanceID = id, Data = data };
        }

        public static OpResult ListAreas(JObject args)
        {
            var areas = new JArray();
            foreach (var pair in DefinedAreas())
            {
                areas.Add(new JObject
                {
                    ["index"] = pair.Key,
                    ["name"] = pair.Value,
                    ["cost"] = NavMesh.GetAreaCost(pair.Key),
                });
            }
            return new OpResult
            {
                Ok = true,
                Summary = $"{areas.Count} NavMesh area(s)",
                Data = new JObject { ["count"] = areas.Count, ["areas"] = areas },
            };
        }

        public static OpResult ListAgentTypes(JObject args)
        {
            var agents = new JArray();
            for (var i = 0; i < NavMesh.GetSettingsCount(); i++)
            {
                var settings = NavMesh.GetSettingsByIndex(i);
                if (settings.agentTypeID < 0) continue;
                agents.Add(new JObject
                {
                    ["agentTypeID"] = settings.agentTypeID,
                    ["name"] = NavMesh.GetSettingsNameFromID(settings.agentTypeID),
                    ["radius"] = settings.agentRadius,
                    ["height"] = settings.agentHeight,
                    ["slope"] = settings.agentSlope,
                    ["climb"] = settings.agentClimb,
                });
            }
            return new OpResult
            {
                Ok = true,
                Summary = $"{agents.Count} NavMesh agent type(s)",
                Data = new JObject { ["count"] = agents.Count, ["agentTypes"] = agents },
            };
        }

        public static OpResult AddOffMeshLink(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            if (!TryResolveSceneGameObject(id, out var host, out var targetError)) return OpResult.Fail(targetError);

            var startTransform = host.transform;
            if (args["startInstanceId"] != null)
            {
                if (!TryResolveSceneGameObject((int)args["startInstanceId"], out var start, out targetError)) return OpResult.Fail($"start: {targetError}");
                startTransform = start.transform;
            }
            if (args["endInstanceId"] == null) return OpResult.Fail("endInstanceId is required for a usable OffMeshLink");
            if (!TryResolveSceneGameObject((int)args["endInstanceId"], out var end, out targetError)) return OpResult.Fail($"end: {targetError}");

            var area = 0;
            if (args["area"] != null && !TryParseAreaIndex(args["area"], out area, out var error)) return OpResult.Fail($"area: {error}");
            float? costOverride = null;
            if (args["costOverride"] != null)
            {
                if (!TryReadFinite(args["costOverride"], out var parsedCost, out error)) return OpResult.Fail($"costOverride: {error}");
                costOverride = parsedCost;
            }

            var link = host.GetComponent<OffMeshLink>();
            if (link == null) link = Undo.AddComponent<OffMeshLink>(host);
            else Undo.RecordObject(link, "Configure OffMeshLink");
            link.startTransform = startTransform;
            link.endTransform = end.transform;
            if (costOverride.HasValue) link.costOverride = costOverride.Value;
            if (args["biDirectional"] != null) link.biDirectional = (bool)args["biDirectional"];
            if (args["activated"] != null) link.activated = (bool)args["activated"];
            if (args["autoUpdatePositions"] != null) link.autoUpdatePositions = (bool)args["autoUpdatePositions"];
            if (args["area"] != null) link.area = area;
            link.UpdatePositions();

            EditorUtility.SetDirty(link);
            PrefabUtility.RecordPrefabInstancePropertyModifications(link);
            EditorSceneManager.MarkSceneDirty(host.scene);
            var data = new JObject
            {
                ["instanceId"] = link.GetMcpInstanceId(),
                ["hostInstanceId"] = host.GetMcpInstanceId(),
                ["startInstanceId"] = startTransform.gameObject.GetMcpInstanceId(),
                ["endInstanceId"] = end.GetMcpInstanceId(),
                ["area"] = link.area,
                ["areaName"] = AreaName(link.area),
                ["costOverride"] = link.costOverride,
                ["biDirectional"] = link.biDirectional,
                ["activated"] = link.activated,
                ["autoUpdatePositions"] = link.autoUpdatePositions,
                ["deprecated"] = true,
            };
            return new OpResult { Ok = true, InstanceID = link.GetMcpInstanceId(), Data = data, Summary = $"configured deprecated OffMeshLink on '{host.name}'" };
        }

        public static OpResult AddObstacle(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            if (!TryResolveSceneGameObject(id, out var go, out var targetError)) return OpResult.Fail(targetError);

            NavMeshObstacleShape? shape = null;
            if (args["shape"] != null)
            {
                if (!Enum.TryParse((string)args["shape"], true, out NavMeshObstacleShape parsed) || !Enum.IsDefined(typeof(NavMeshObstacleShape), parsed))
                    return OpResult.Fail($"unknown NavMeshObstacle shape '{(string)args["shape"]}'");
                shape = parsed;
            }
            Vector3? center = null;
            if (args["center"] != null)
            {
                if (!TryParseVector3(args["center"], out var parsed, out var error)) return OpResult.Fail($"center: {error}");
                center = parsed;
            }
            Vector3? size = null;
            if (args["size"] != null)
            {
                if (!TryParseVector3(args["size"], out var parsed, out var error) || parsed.x < 0f || parsed.y < 0f || parsed.z < 0f)
                    return OpResult.Fail("size must contain three non-negative finite values");
                size = parsed;
            }
            if (!TryReadOptionalNonNegative(args, "radius", out var radius, out var numericError) ||
                !TryReadOptionalNonNegative(args, "height", out var height, out numericError) ||
                !TryReadOptionalNonNegative(args, "carvingMoveThreshold", out var moveThreshold, out numericError) ||
                !TryReadOptionalNonNegative(args, "carvingTimeToStationary", out var stationaryTime, out numericError))
                return OpResult.Fail(numericError);

            var obstacle = go.GetComponent<NavMeshObstacle>();
            if (obstacle == null) obstacle = Undo.AddComponent<NavMeshObstacle>(go);
            else Undo.RecordObject(obstacle, "Configure NavMeshObstacle");
            if (shape.HasValue) obstacle.shape = shape.Value;
            if (center.HasValue) obstacle.center = center.Value;
            if (size.HasValue) obstacle.size = size.Value;
            if (radius.HasValue) obstacle.radius = radius.Value;
            if (height.HasValue) obstacle.height = height.Value;
            if (args["carving"] != null) obstacle.carving = (bool)args["carving"];
            if (args["carveOnlyStationary"] != null) obstacle.carveOnlyStationary = (bool)args["carveOnlyStationary"];
            if (moveThreshold.HasValue) obstacle.carvingMoveThreshold = moveThreshold.Value;
            if (stationaryTime.HasValue) obstacle.carvingTimeToStationary = stationaryTime.Value;

            EditorUtility.SetDirty(obstacle);
            PrefabUtility.RecordPrefabInstancePropertyModifications(obstacle);
            EditorSceneManager.MarkSceneDirty(go.scene);
            var data = new JObject
            {
                ["instanceId"] = obstacle.GetMcpInstanceId(),
                ["hostInstanceId"] = go.GetMcpInstanceId(),
                ["shape"] = obstacle.shape.ToString(),
                ["center"] = JsonHelpers.Vec3(obstacle.center),
                ["size"] = JsonHelpers.Vec3(obstacle.size),
                ["radius"] = obstacle.radius,
                ["height"] = obstacle.height,
                ["carving"] = obstacle.carving,
                ["carveOnlyStationary"] = obstacle.carveOnlyStationary,
                ["carvingMoveThreshold"] = obstacle.carvingMoveThreshold,
                ["carvingTimeToStationary"] = obstacle.carvingTimeToStationary,
            };
            return new OpResult { Ok = true, InstanceID = obstacle.GetMcpInstanceId(), Data = data, Summary = $"configured NavMeshObstacle on '{go.name}'" };
        }

        public static int ParseAreaMask(JToken token)
        {
            if (TryParseAreaMask(token, out var mask, out var error)) return mask;
            throw new ArgumentException(error, nameof(token));
        }

        public static bool TryParseAreaMask(JToken token, out int mask, out string error)
        {
            mask = NavMesh.AllAreas;
            error = null;
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token.Type == JTokenType.Integer)
            {
                var raw = token.Value<long>();
                if (raw < int.MinValue || raw > int.MaxValue) { error = "raw mask is outside Int32 range"; return false; }
                mask = (int)raw;
                return true;
            }
            if (token.Type == JTokenType.String)
            {
                if (!TryParseAreaIndex(token, out var index, out error)) return false;
                mask = 1 << index;
                return true;
            }
            if (token is JArray array)
            {
                if (array.Count == 0) { error = "area array cannot be empty"; return false; }
                mask = 0;
                foreach (var item in array)
                {
                    if (!TryParseAreaIndex(item, out var index, out error)) return false;
                    mask |= 1 << index;
                }
                return true;
            }
            error = "expected an Int32 mask, area name, or non-empty array of names/indices";
            return false;
        }

        public static bool TryParseAreaIndex(JToken token, out int index, out string error)
        {
            index = -1;
            error = null;
            if (token == null || token.Type == JTokenType.Null) { error = "area is required"; return false; }
            if (token.Type == JTokenType.Integer)
            {
                var raw = token.Value<long>();
                if (raw < 0 || raw > 31) { error = "area index must be between 0 and 31"; return false; }
                index = (int)raw;
            }
            else if (token.Type == JTokenType.String)
            {
                var name = ((string)token)?.Trim();
                if (string.IsNullOrEmpty(name)) { error = "area name cannot be empty"; return false; }
                index = NavMesh.GetAreaFromName(name);
                if (index < 0) { error = $"unknown NavMesh area '{name}'"; return false; }
            }
            else
            {
                error = "expected an area name or numeric index";
                return false;
            }
            if (!DefinedAreas().ContainsKey(index)) { error = $"NavMesh area index {index} is not defined"; return false; }
            return true;
        }

        private static bool TryBuildQuery(JObject args, out int areaMask, out int? agentTypeId, out NavMeshQueryFilter filter, out string error)
        {
            filter = default;
            agentTypeId = null;
            if (!TryParseAreaMask(args["areaMask"], out areaMask, out error)) { error = $"areaMask: {error}"; return false; }
            if (args["agentTypeID"] == null) return true;
            var id = (int)args["agentTypeID"];
            if (!TryValidateAgentType(id, out error)) return false;
            agentTypeId = id;
            filter = new NavMeshQueryFilter { agentTypeID = id, areaMask = areaMask };
            return true;
        }

        private static bool TryValidateAgentType(int agentTypeId, out string error)
        {
            var settings = NavMesh.GetSettingsByID(agentTypeId);
            if (settings.agentTypeID == -1)
            {
                error = $"unknown NavMesh agentTypeID {agentTypeId}";
                return false;
            }
            error = null;
            return true;
        }

        private static bool TryResolveSceneGameObject(int id, out GameObject go, out string error)
        {
            var target = UnityObjectIds.Resolve(id);
            go = target as GameObject ?? (target as Component)?.gameObject;
            if (go == null) { error = $"GameObject not found: instanceId={id}"; return false; }
            if (EditorUtility.IsPersistent(go) || PrefabUtility.IsPartOfPrefabAsset(go))
            {
                error = "persistent prefab assets are not scene-edit targets; instantiate the prefab or open Prefab Mode";
                return false;
            }
            if (!go.scene.IsValid() || !go.scene.isLoaded)
            {
                error = "target must belong to a loaded scene or Prefab Stage";
                return false;
            }
            error = null;
            return true;
        }

        private static bool TryParseVector3(JToken token, out Vector3 value, out string error)
        {
            value = default;
            error = null;
            if (token is JArray array && array.Count == 3 && array.All(IsNumber))
            {
                value = new Vector3((float)array[0], (float)array[1], (float)array[2]);
            }
            else if (token is JObject obj && obj["x"] != null && obj["y"] != null && obj["z"] != null && IsNumber(obj["x"]) && IsNumber(obj["y"]) && IsNumber(obj["z"]))
            {
                value = new Vector3((float)obj["x"], (float)obj["y"], (float)obj["z"]);
            }
            else
            {
                error = "expected [x,y,z] or {x,y,z}";
                return false;
            }
            if (!IsFinite(value.x) || !IsFinite(value.y) || !IsFinite(value.z))
            {
                error = "coordinates must be finite";
                return false;
            }
            return true;
        }

        private static bool TryReadOptionalNonNegative(JObject args, string name, out float? value, out string error)
        {
            value = null;
            error = null;
            if (args[name] == null) return true;
            if (!TryReadNonNegative(args[name], 0f, out var parsed, out error)) { error = $"{name}: {error}"; return false; }
            value = parsed;
            return true;
        }

        private static bool TryReadOptionalFinite(JObject args, string name, out float? value, out string error)
        {
            value = null;
            error = null;
            if (args[name] == null) return true;
            if (!TryReadFinite(args[name], out var parsed, out error)) { error = $"{name}: {error}"; return false; }
            value = parsed;
            return true;
        }

        private static bool TryReadNonNegative(JToken token, float fallback, out float value, out string error)
        {
            if (token == null) { value = fallback; error = null; return true; }
            if (!TryReadFinite(token, out value, out error)) return false;
            if (value < 0f) { error = "must be non-negative"; return false; }
            return true;
        }

        private static bool TryReadFinite(JToken token, out float value, out string error)
        {
            value = 0f;
            if (!IsNumber(token)) { error = "must be a number"; return false; }
            value = (float)token;
            if (!IsFinite(value)) { error = "must be finite"; return false; }
            error = null;
            return true;
        }

        private static JObject AgentJson(GameObject go, NavMeshAgent agent)
        {
            var onNavMesh = agent.isActiveAndEnabled && agent.isOnNavMesh;
            var data = new JObject
            {
                ["instanceId"] = go.GetMcpInstanceId(),
                ["agentInstanceId"] = agent.GetMcpInstanceId(),
                ["name"] = go.name,
                ["isOnNavMesh"] = onNavMesh,
                ["speed"] = agent.speed,
                ["acceleration"] = agent.acceleration,
                ["angularSpeed"] = agent.angularSpeed,
                ["stoppingDistance"] = agent.stoppingDistance,
                ["radius"] = agent.radius,
                ["height"] = agent.height,
                ["baseOffset"] = agent.baseOffset,
                ["areaMask"] = agent.areaMask,
            };
            if (onNavMesh)
            {
                data["hasPath"] = agent.hasPath;
                data["pathPending"] = agent.pathPending;
                data["isStopped"] = agent.isStopped;
                data["destination"] = JsonHelpers.Vec3(agent.destination);
                data["remainingDistance"] = agent.remainingDistance;
                data["velocity"] = JsonHelpers.Vec3(agent.velocity);
                data["pathStatus"] = agent.path.status.ToString();
                data["cornerCount"] = agent.path.corners.Length;
            }
            return data;
        }

        private static JObject HitJson(NavMeshHit hit) => new JObject
        {
            ["position"] = JsonHelpers.Vec3(hit.position),
            ["normal"] = JsonHelpers.Vec3(hit.normal),
            ["distance"] = hit.distance,
            ["areaMask"] = hit.mask,
            ["areas"] = new JArray(AreaNames(hit.mask)),
        };

        private static SortedDictionary<int, string> DefinedAreas()
        {
            var result = new SortedDictionary<int, string>();
            foreach (var name in GameObjectUtility.GetNavMeshAreaNames())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                var index = NavMesh.GetAreaFromName(name);
                if (index >= 0 && index <= 31) result[index] = name;
            }
            return result;
        }

        private static IEnumerable<string> AreaNames(int mask)
        {
            foreach (var pair in DefinedAreas())
                if ((mask & (1 << pair.Key)) != 0) yield return pair.Value;
        }

        private static string AreaName(int index) => DefinedAreas().TryGetValue(index, out var name) ? name : $"#{index}";
        private static string DescribeArea(int mask) => string.Join(",", AreaNames(mask));
        private static string Format(Vector3 value) => $"[{value.x:F2},{value.y:F2},{value.z:F2}]";
        private static bool IsNumber(JToken token) => token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float);
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
#pragma warning restore CS0618
}
