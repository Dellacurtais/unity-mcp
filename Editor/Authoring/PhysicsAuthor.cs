using System;
using System.Collections.Generic;
using System.Linq;
using ToolTargetRef = Maicon.SceneAnalyzer.Editor.Core.ToolTargetRef;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    /// Physics queries (raycast/overlap/sweep), spatial + semantic scene
    /// classification, and collider authoring helpers (auto-fit, compound,
    /// physics material).
    public static class PhysicsAuthor
    {
        // ============== Physics queries ==============

        public static OpResult Raycast(JObject args)
        {
            if (!TryParseFiniteVector3(args["origin"], "origin", Vector3.zero, true, out var origin, out var error))
                return OpResult.Fail(error);
            if (!TryParseFiniteVector3(args["direction"], "direction", Vector3.forward, true, out var direction, out error)
                || !TryNormalizeDirection(direction, "direction", out direction, out error))
                return OpResult.Fail(error);
            if (!TryParseNonNegativeNumber(args["maxDistance"], "maxDistance", Mathf.Infinity, true, out var maxDistance, out error))
                return OpResult.Fail(error);
            if (!TryParseLayerMask(args["layerMask"], out var layerMask, out error))
                return OpResult.Fail(error);
            if (!TryParseTriggerInteraction(args["hitTriggers"], out var hitTriggers, out error))
                return OpResult.Fail(error);
            var multiple = (bool?)args["multiple"] ?? false;

            Physics.SyncTransforms();
            RaycastHit[] hits;
            if (multiple)
            {
                hits = Physics.RaycastAll(origin, direction, maxDistance, layerMask, hitTriggers);
            }
            else if (Physics.Raycast(origin, direction, out var hit, maxDistance, layerMask, hitTriggers))
            {
                hits = new[] { hit };
            }
            else
            {
                hits = Array.Empty<RaycastHit>();
            }

            Array.Sort(hits, CompareHits);
            var hitData = new JArray();
            foreach (var current in hits) hitData.Add(HitJson(current));

            var sb = new System.Text.StringBuilder();
            if (multiple)
            {
                sb.AppendLine($"raycast hit {hits.Length} collider(s):");
                foreach (var current in hits) sb.AppendLine("  " + DescribeHit(current));
            }
            else if (hits.Length == 1)
            {
                sb.AppendLine("raycast hit:\n  " + DescribeHit(hits[0]));
            }
            else
            {
                sb.AppendLine("raycast: no hit");
            }

            return new OpResult
            {
                Ok = true,
                Summary = sb.ToString(),
                InstanceID = 0,
                Data = new JObject
                {
                    ["origin"] = JsonHelpers.Vec3(origin),
                    ["direction"] = JsonHelpers.Vec3(direction),
                    ["multiple"] = multiple,
                    ["count"] = hits.Length,
                    ["hits"] = hitData,
                },
            };
        }

        public static OpResult OverlapQuery(JObject args)
        {
            var shape = ((string)args["shape"] ?? string.Empty).Trim().ToLowerInvariant();
            if (!TryParseLayerMask(args["layerMask"], out var layerMask, out var error))
                return OpResult.Fail(error);
            if (!TryParseTriggerInteraction(args["hitTriggers"], out var hitTriggers, out error))
                return OpResult.Fail(error);

            Collider[] hits;
            Physics.SyncTransforms();
            switch (shape)
            {
                case "box":
                    if (!TryParseFiniteVector3(args["center"], "center", Vector3.zero, true, out var center, out error))
                        return OpResult.Fail(error);
                    if (!TryParseNonNegativeVector3(args["halfExtents"], "halfExtents", out var halfExtents, out error))
                        return OpResult.Fail(error);
                    if (!TryParseFiniteRotation(args["rotation"], out var rotation, out error))
                        return OpResult.Fail(error);
                    hits = Physics.OverlapBox(center, halfExtents, rotation, layerMask, hitTriggers);
                    break;
                case "sphere":
                    if (!TryParseFiniteVector3(args["center"], "center", Vector3.zero, true, out center, out error))
                        return OpResult.Fail(error);
                    if (!TryParseNonNegativeNumber(args["radius"], "radius", 0f, false, out var radius, out error))
                        return OpResult.Fail(error);
                    hits = Physics.OverlapSphere(center, radius, layerMask, hitTriggers);
                    break;
                case "capsule":
                    if (!TryParseFiniteVector3(args["pointA"], "pointA", Vector3.zero, true, out var pointA, out error))
                        return OpResult.Fail(error);
                    if (!TryParseFiniteVector3(args["pointB"], "pointB", Vector3.zero, true, out var pointB, out error))
                        return OpResult.Fail(error);
                    if (!TryParseNonNegativeNumber(args["radius"], "radius", 0f, false, out radius, out error))
                        return OpResult.Fail(error);
                    hits = Physics.OverlapCapsule(pointA, pointB, radius, layerMask, hitTriggers);
                    break;
                default:
                    return OpResult.Fail($"unknown shape '{shape}' (use box/sphere/capsule)");
            }

            Array.Sort(hits, CompareColliders);
            var colliderData = new JArray();
            foreach (var collider in hits) colliderData.Add(ColliderJson(collider));

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"overlap_{shape}: {hits.Length} collider(s) inside:");
            foreach (var collider in hits.Take(50)) sb.AppendLine("  " + DescribeCollider(collider));
            if (hits.Length > 50) sb.AppendLine($"  [+{hits.Length - 50} more]");
            return new OpResult
            {
                Ok = true,
                Summary = sb.ToString(),
                Data = new JObject
                {
                    ["shape"] = shape,
                    ["count"] = hits.Length,
                    ["colliders"] = colliderData,
                },
            };
        }

        public static OpResult SweepQuery(JObject args)
        {
            var shape = ((string)args["shape"] ?? string.Empty).Trim().ToLowerInvariant();
            if (!TryParseFiniteVector3(args["direction"], "direction", Vector3.forward, true, out var direction, out var error)
                || !TryNormalizeDirection(direction, "direction", out direction, out error))
                return OpResult.Fail(error);
            if (!TryParseNonNegativeNumber(args["maxDistance"], "maxDistance", Mathf.Infinity, true, out var maxDistance, out error))
                return OpResult.Fail(error);
            if (!TryParseLayerMask(args["layerMask"], out var layerMask, out error))
                return OpResult.Fail(error);
            if (!TryParseTriggerInteraction(args["hitTriggers"], out var hitTriggers, out error))
                return OpResult.Fail(error);

            RaycastHit[] hits;
            Physics.SyncTransforms();
            switch (shape)
            {
                case "box":
                    if (!TryParseFiniteVector3(args["center"], "center", Vector3.zero, true, out var center, out error))
                        return OpResult.Fail(error);
                    if (!TryParseNonNegativeVector3(args["halfExtents"], "halfExtents", out var halfExtents, out error))
                        return OpResult.Fail(error);
                    if (!TryParseFiniteRotation(args["rotation"], out var rotation, out error))
                        return OpResult.Fail(error);
                    hits = Physics.BoxCastAll(center, halfExtents, direction, rotation, maxDistance, layerMask, hitTriggers);
                    break;
                case "sphere":
                    if (!TryParseFiniteVector3(args["origin"], "origin", Vector3.zero, true, out var origin, out error))
                        return OpResult.Fail(error);
                    if (!TryParseNonNegativeNumber(args["radius"], "radius", 0f, false, out var radius, out error))
                        return OpResult.Fail(error);
                    hits = Physics.SphereCastAll(origin, radius, direction, maxDistance, layerMask, hitTriggers);
                    break;
                case "capsule":
                    if (!TryParseFiniteVector3(args["pointA"], "pointA", Vector3.zero, true, out var pointA, out error))
                        return OpResult.Fail(error);
                    if (!TryParseFiniteVector3(args["pointB"], "pointB", Vector3.zero, true, out var pointB, out error))
                        return OpResult.Fail(error);
                    if (!TryParseNonNegativeNumber(args["radius"], "radius", 0f, false, out radius, out error))
                        return OpResult.Fail(error);
                    hits = Physics.CapsuleCastAll(pointA, pointB, radius, direction, maxDistance, layerMask, hitTriggers);
                    break;
                default:
                    return OpResult.Fail($"unknown shape '{shape}' (use box/sphere/capsule)");
            }

            Array.Sort(hits, CompareHits);
            var hitData = new JArray();
            foreach (var hit in hits) hitData.Add(HitJson(hit));

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"sweep_{shape}: {hits.Length} hit(s):");
            foreach (var hit in hits.Take(50)) sb.AppendLine("  " + DescribeHit(hit));
            if (hits.Length > 50) sb.AppendLine($"  [+{hits.Length - 50} more]");
            return new OpResult
            {
                Ok = true,
                Summary = sb.ToString(),
                Data = new JObject
                {
                    ["shape"] = shape,
                    ["direction"] = JsonHelpers.Vec3(direction),
                    ["count"] = hits.Length,
                    ["hits"] = hitData,
                },
            };
        }

        public static OpResult CheckGround(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");

            if (!TryParseFiniteVector3(args["originOffset"], "originOffset", Vector3.zero, false, out var originOffset, out var error))
                return OpResult.Fail(error);
            if (!TryParseFiniteVector3(args["down"], "down", Vector3.down, false, out var down, out error)
                || !TryNormalizeDirection(down, "down", out down, out error))
                return OpResult.Fail(error);
            if (!TryParseNonNegativeNumber(args["maxDistance"], "maxDistance", 100f, true, out var maxDistance, out error))
                return OpResult.Fail(error);
            if (!TryParseLayerMask(args["layerMask"], out var layerMask, out error))
                return OpResult.Fail(error);
            if (!TryParseTriggerInteraction(args["hitTriggers"], out var hitTriggers, out error))
                return OpResult.Fail(error);

            var origin = go.transform.position + originOffset;
            Physics.SyncTransforms();
            var grounded = Physics.Raycast(origin, down, out var hit, maxDistance, layerMask, hitTriggers);
            var sb = new System.Text.StringBuilder();
            if (grounded)
            {
                var hitObject = hit.collider.gameObject;
                sb.AppendLine($"grounded: yes  distance={hit.distance:F3}  on '{hitObject.name}'");
                sb.AppendLine($"  tag='{hitObject.tag}'  layer={hitObject.layer} ('{LayerMask.LayerToName(hitObject.layer)}')");
                sb.AppendLine($"  point=[{hit.point.x:F2}, {hit.point.y:F2}, {hit.point.z:F2}]");
                sb.AppendLine($"  normal=[{hit.normal.x:F2}, {hit.normal.y:F2}, {hit.normal.z:F2}]");
                sb.AppendLine($"  collider={hit.collider.GetType().Name}  isTrigger={hit.collider.isTrigger}");
            }
            else
            {
                sb.AppendLine($"grounded: NO  (no collider within {maxDistance} along {down})");
            }

            return new OpResult
            {
                Ok = true,
                Summary = sb.ToString(),
                InstanceID = id,
                Data = new JObject
                {
                    ["target"] = ToolTargetRef.Capture(go)?.ToJson(),
                    ["origin"] = JsonHelpers.Vec3(origin),
                    ["direction"] = JsonHelpers.Vec3(down),
                    ["grounded"] = grounded,
                    ["hit"] = grounded ? HitJson(hit) : null,
                },
            };
        }

        // ============== Bounds ==============

        public static OpResult ComputeBounds(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            var recursive = (bool?)args["recursive"] ?? true;

            var bounds = ComputeWorldBounds(go, recursive);
            if (!bounds.HasValue) return OpResult.Fail("no Renderer found on this GameObject (or descendants)");

            var b = bounds.Value;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"bounds of '{go.name}' (recursive={recursive}, world space):");
            sb.AppendLine($"  center=[{b.center.x:F2}, {b.center.y:F2}, {b.center.z:F2}]");
            sb.AppendLine($"  size=[{b.size.x:F2}, {b.size.y:F2}, {b.size.z:F2}]");
            sb.AppendLine($"  min=[{b.min.x:F2}, {b.min.y:F2}, {b.min.z:F2}]");
            sb.AppendLine($"  max=[{b.max.x:F2}, {b.max.y:F2}, {b.max.z:F2}]");
            return new OpResult
            {
                Ok = true,
                Summary = sb.ToString(),
                InstanceID = id,
                Data = new JObject
                {
                    ["target"] = ToolTargetRef.Capture(go)?.ToJson(),
                    ["recursive"] = recursive,
                    ["bounds"] = BoundsJson(b),
                },
            };
        }

        public static Bounds? ComputeWorldBounds(GameObject go, bool recursive)
        {
            Bounds? combined = null;
            var renderers = recursive
                ? go.GetComponentsInChildren<Renderer>(true)
                : go.GetComponents<Renderer>();
            foreach (var r in renderers)
            {
                if (combined.HasValue) { var b = combined.Value; b.Encapsulate(r.bounds); combined = b; }
                else combined = r.bounds;
            }
            return combined;
        }

        // ============== Scene queries ==============

        public static OpResult QueryScene(JObject args)
        {
            var tag = (string)args["tag"];
            var layerArg = args["layer"];
            int? layerFilter = ParseLayerInt(layerArg);
            var componentTypes = (args["componentTypes"] as JArray)?.Select(t => (string)t).ToArray();
            var nameContains = (string)args["nameContains"];
            var pathContains = (string)args["pathContains"];
            var withinRadius = args["withinRadius"] as JObject;
            Vector3? center = null;
            float radius = 0;
            if (withinRadius != null)
            {
                center = JsonHelpers.ParseVector3(withinRadius["center"]);
                radius = (float?)withinRadius["radius"] ?? 0f;
            }
            var limit = (int?)args["limit"] ?? 100;
            var sortBy = ((string)args["sortBy"] ?? "name").ToLowerInvariant();

            // Pre-resolve componentTypes once (was inside the per-GO loop, dominating cost).
            // Fail fast on bad names so the agent gets a clear error instead of an empty result.
            Type[] resolvedTypes = null;
            if (componentTypes != null && componentTypes.Length > 0)
            {
                resolvedTypes = new Type[componentTypes.Length];
                for (int i = 0; i < componentTypes.Length; i++)
                {
                    var ct = componentTypes[i];
                    var t = ComponentTypeInspector.ResolveType(ct);
                    if (t == null) return OpResult.Fail($"INVALID_ARG: unknown component type '{ct}'");
                    if (!typeof(Component).IsAssignableFrom(t))
                        return OpResult.Fail($"INVALID_ARG: '{ct}' is not a Component subtype (resolved to {t.FullName})");
                    resolvedTypes[i] = t;
                }
            }

            var snapshot = Bootstrap.Snapshot;
            snapshot.RefreshIfNeeded();

            // Aux filters shared between the two iteration strategies.
            bool TryMatchAux(GameObject go, out float distance, out int depth)
            {
                distance = 0; depth = 0;
                if (!string.IsNullOrEmpty(tag) && go.tag != tag) return false;
                if (layerFilter.HasValue && go.layer != layerFilter.Value) return false;
                if (!string.IsNullOrEmpty(nameContains)
                    && go.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) return false;
                if (center.HasValue)
                {
                    distance = Vector3.Distance(center.Value, go.transform.position);
                    if (radius > 0 && distance > radius) return false;
                }
                if (!string.IsNullOrEmpty(pathContains))
                {
                    var path = snapshot.GetPath(go.GetInstanceID());
                    if (path == null || path.IndexOf(pathContains, StringComparison.OrdinalIgnoreCase) < 0) return false;
                }
                var p = go.transform.parent;
                while (p != null) { depth++; p = p.parent; }
                return true;
            }

            var matches = new List<(GameObject go, float dist, int depth)>();

            if (resolvedTypes != null && resolvedTypes.Length > 0)
            {
                // Algorithmic flip: when filtering by componentType, seed from FindObjectsByType
                // (~K results) instead of walking every GO in the snapshot (~N). For custom MBs,
                // K is typically 10-100 vs N=5000+.
                var seeds = UnityEngine.Object.FindObjectsByType(resolvedTypes[0], FindObjectsInactive.Include, FindObjectsSortMode.None);
                var seenIds = new HashSet<int>();
                foreach (var seed in seeds)
                {
                    var seedComp = seed as Component;
                    if (seedComp == null) continue;
                    var go = seedComp.gameObject;
                    if (go == null) continue;
                    // A GO may carry multiple instances of the seed type (e.g. 2 BoxColliders); process once.
                    if (!seenIds.Add(go.GetInstanceID())) continue;

                    bool hasAll = true;
                    for (int i = 1; i < resolvedTypes.Length; i++)
                    {
                        if (go.GetComponent(resolvedTypes[i]) == null) { hasAll = false; break; }
                    }
                    if (!hasAll) continue;

                    if (TryMatchAux(go, out float distance, out int depth))
                        matches.Add((go, distance, depth));
                }
            }
            else
            {
                foreach (var info in snapshot.All())
                {
                    var go = EditorUtility.InstanceIDToObject(info.InstanceID) as GameObject;
                    if (go == null) continue;
                    if (TryMatchAux(go, out float distance, out int depth))
                        matches.Add((go, distance, depth));
                }
            }

            switch (sortBy)
            {
                case "distance": matches.Sort((a, b) => a.dist.CompareTo(b.dist)); break;
                case "depth": matches.Sort((a, b) => a.depth.CompareTo(b.depth)); break;
                default: matches.Sort((a, b) => string.Compare(a.go.name, b.go.name, StringComparison.Ordinal)); break;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"query_scene: {matches.Count} match(es) (showing up to {limit}, sortBy={sortBy})");
            int shown = 0;
            foreach (var m in matches)
            {
                if (shown++ >= limit) { sb.AppendLine($"  [+{matches.Count - limit} more]"); break; }
                var pos = m.go.transform.position;
                sb.AppendLine($"  #{m.go.GetInstanceID()} '{m.go.name}'  tag={m.go.tag} layer={m.go.layer}"
                    + (center.HasValue ? $"  d={m.dist:F2}" : "")
                    + $"  pos=[{pos.x:F1},{pos.y:F1},{pos.z:F1}]");
            }
            return new OpResult { Ok = true, Summary = sb.ToString() };
        }

        public static OpResult ClassifyScene(JObject args)
        {
            var snapshot = Bootstrap.Snapshot;
            snapshot.RefreshIfNeeded();

            var byTag = new Dictionary<string, int>(StringComparer.Ordinal);
            var byLayer = new Dictionary<int, int>();
            var byComponent = new Dictionary<string, int>(StringComparer.Ordinal);
            int rootCount = 0, leafCount = 0, maxDepth = 0;
            var aggBounds = new Bounds(Vector3.zero, Vector3.zero);
            bool hasAnyBounds = false;
            int prefabLinked = 0, prefabBroken = 0, prefabUnlinked = 0;
            int nearOrigin = 0, farFromOrigin = 0;
            const float nearRadius = 50f, farRadius = 200f;

            foreach (var info in snapshot.All())
            {
                var go = EditorUtility.InstanceIDToObject(info.InstanceID) as GameObject;
                if (go == null) continue;

                Bump(byTag, go.tag);
                if (!byLayer.TryGetValue(go.layer, out var lc)) byLayer[go.layer] = 1; else byLayer[go.layer] = lc + 1;

                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) { Bump(byComponent, "[Missing]"); continue; }
                    Bump(byComponent, c.GetType().Name);
                }

                if (info.ParentId == 0) rootCount++;
                if (info.ChildIds == null || info.ChildIds.Count == 0) leafCount++;

                int depth = 0;
                var p = go.transform.parent;
                while (p != null) { depth++; p = p.parent; }
                if (depth > maxDepth) maxDepth = depth;

                var d = go.transform.position.magnitude;
                if (d <= nearRadius) nearOrigin++;
                else if (d > farRadius) farFromOrigin++;

                var renderer = go.GetComponent<Renderer>();
                if (renderer != null)
                {
                    if (!hasAnyBounds) { aggBounds = renderer.bounds; hasAnyBounds = true; }
                    else aggBounds.Encapsulate(renderer.bounds);
                }

                var prefabType = PrefabUtility.GetPrefabAssetType(go);
                var status = PrefabUtility.GetPrefabInstanceStatus(go);
                if (status == PrefabInstanceStatus.Connected) prefabLinked++;
                else if (status == PrefabInstanceStatus.MissingAsset) prefabBroken++;
                else if (prefabType == PrefabAssetType.NotAPrefab) prefabUnlinked++;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"classify_scene — {snapshot.Count} GameObject(s) total");
            sb.AppendLine();
            sb.AppendLine("Tags:");
            foreach (var kv in byTag.OrderByDescending(k => k.Value).Take(20))
                sb.AppendLine($"  {kv.Key}: {kv.Value}");
            sb.AppendLine();
            sb.AppendLine("Layers:");
            foreach (var kv in byLayer.OrderByDescending(k => k.Value).Take(20))
                sb.AppendLine($"  {LayerMask.LayerToName(kv.Key)} (#{kv.Key}): {kv.Value}");
            sb.AppendLine();
            sb.AppendLine("Top components:");
            foreach (var kv in byComponent.OrderByDescending(k => k.Value).Take(30))
                sb.AppendLine($"  {kv.Key}: {kv.Value}");
            sb.AppendLine();
            sb.AppendLine($"Hierarchy: roots={rootCount}  leaves={leafCount}  maxDepth={maxDepth}");
            sb.AppendLine($"Spatial: bounds={(hasAnyBounds ? $"min=[{aggBounds.min.x:F1},{aggBounds.min.y:F1},{aggBounds.min.z:F1}] max=[{aggBounds.max.x:F1},{aggBounds.max.y:F1},{aggBounds.max.z:F1}]" : "(no renderers)")}");
            sb.AppendLine($"  near origin (≤{nearRadius}u): {nearOrigin}  far (>{farRadius}u): {farFromOrigin}");
            sb.AppendLine($"Prefab: linked={prefabLinked}  broken={prefabBroken}  unlinked={prefabUnlinked}");
            return new OpResult { Ok = true, Summary = sb.ToString() };
        }

        public static OpResult QueryCollisionLayers(JObject args)
        {
            int? only = null;
            if (args["layer"] != null && args["layer"].Type != JTokenType.Null)
            {
                if (!TryParseLayerIndex(args["layer"], out var layerIndex, out var error))
                    return OpResult.Fail(error);
                only = layerIndex;
            }

            var rows = new JArray();
            var sb = new System.Text.StringBuilder();
            if (only.HasValue)
            {
                var index = only.Value;
                var collidesWith = BuildCollisionLayerList(index);
                var layerName = LayerMask.LayerToName(index);
                rows.Add(new JObject
                {
                    ["index"] = index,
                    ["name"] = layerName,
                    ["collidesWith"] = collidesWith,
                });
                sb.AppendLine($"layer {index} ('{DisplayLayerName(layerName)}') collides with:");
                foreach (var layer in collidesWith)
                    sb.AppendLine($"  {(int)layer["index"]} '{DisplayLayerName((string)layer["name"])}'");
            }
            else
            {
                sb.AppendLine("collision matrix (X = collides, . = ignored):");
                sb.Append("       ");
                for (var column = 0; column < 32; column++) sb.Append($"{column,3}");
                sb.AppendLine();
                for (var row = 0; row < 32; row++)
                {
                    var rowName = LayerMask.LayerToName(row);
                    rows.Add(new JObject
                    {
                        ["index"] = row,
                        ["name"] = rowName,
                        ["collidesWith"] = BuildCollisionLayerList(row),
                    });
                    sb.Append($"{row,2} {DisplayLayerName(rowName),-12}");
                    for (var column = 0; column < 32; column++)
                        sb.Append(Physics.GetIgnoreLayerCollision(row, column) ? "  ." : "  X");
                    sb.AppendLine();
                }
            }

            return new OpResult
            {
                Ok = true,
                Summary = sb.ToString(),
                Data = new JObject
                {
                    ["matrixState"] = "effectiveRuntime",
                    ["selectedLayer"] = only.HasValue ? rows[0] : null,
                    ["layers"] = rows,
                },
            };
        }

        // ============== Collider authoring ==============

        public static OpResult AutoFitCollider(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");

            var kind = ((string)args["kind"] ?? "box").Trim().ToLowerInvariant();
            if (!IsColliderKind(kind))
                return OpResult.Fail($"unknown kind '{kind}' (box/sphere/capsule/mesh)");

            var useChildren = kind == "mesh"
                ? (bool?)args["useChildren"] ?? false
                : (bool?)args["useChildren"] ?? true;
            var replaceExisting = (bool?)args["replaceExisting"] ?? true;
            var isTrigger = (bool?)args["isTrigger"] ?? false;
            var convex = kind == "mesh" && ((bool?)args["convex"] ?? false);

            if (kind == "mesh" && useChildren)
                return OpResult.Fail("mesh kind requires useChildren=false because one MeshCollider can only use the target GameObject's MeshFilter.sharedMesh");

            var renderers = useChildren
                ? go.GetComponentsInChildren<Renderer>(true)
                : go.GetComponents<Renderer>();
            if (renderers.Length == 0)
                return OpResult.Fail("no Renderer found — cannot auto-fit");
            if (!TryComputeLocalRendererBounds(go.transform, renderers, out var localBounds, out var boundsError))
                return OpResult.Fail(boundsError);

            Mesh mesh = null;
            if (kind == "mesh")
            {
                if (!TryGetMeshSource(go, out mesh, out var meshError))
                    return OpResult.Fail(meshError);
                if (isTrigger && !convex)
                    return OpResult.Fail("mesh collider triggers require convex=true");
                var attachedRigidbody = go.GetComponentInParent<Rigidbody>();
                if (attachedRigidbody != null && !attachedRigidbody.isKinematic && !convex)
                    return OpResult.Fail("a MeshCollider attached to a non-kinematic Rigidbody requires convex=true");
            }

            Collider added;
            int replaced = 0;
            using (var mutation = new MutationScope("Auto Fit Collider"))
            {
                if (replaceExisting)
                {
                    foreach (var existing in go.GetComponents<Collider>())
                    {
                        if (existing == null || existing is CharacterController) continue;
                        mutation.Destroy(existing);
                        replaced++;
                    }
                }

                added = AddConfiguredCollider(mutation, go, kind, localBounds, isTrigger, convex, mesh);
                mutation.Touch(go);
                mutation.Complete();
            }

            var result = OpResult.Success(added.GetInstanceID(),
                $"added {kind} collider on '{go.name}'  local size~{localBounds.size.x:F2}×{localBounds.size.y:F2}×{localBounds.size.z:F2}");
            result.Data = new JObject
            {
                ["target"] = ToolTargetRef.Capture(go)?.ToJson(),
                ["collider"] = ToolTargetRef.Capture(added)?.ToJson(),
                ["kind"] = kind,
                ["useChildren"] = useChildren,
                ["replaceExisting"] = replaceExisting,
                ["replacedCount"] = replaced,
                ["isTrigger"] = isTrigger,
                ["convex"] = convex,
            };
            return result;
        }

        public static OpResult AddCompoundColliders(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var rootGo = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (rootGo == null) return OpResult.Fail($"GameObject not found: instanceId={id}");

            var kind = ((string)args["kind"] ?? "box").Trim().ToLowerInvariant();
            if (!IsColliderKind(kind))
                return OpResult.Fail($"unknown kind '{kind}' (box/sphere/capsule/mesh)");
            var isTrigger = (bool?)args["isTrigger"] ?? false;
            var replaceExisting = (bool?)args["replaceExisting"] ?? false;

            var renderers = rootGo.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return OpResult.Fail("no Renderer found on the target hierarchy");

            var plans = new List<(Renderer renderer, Bounds localBounds, Mesh mesh)>();
            foreach (var renderer in renderers)
            {
                if (!TryComputeLocalRendererBounds(renderer.transform, new[] { renderer }, out var localBounds, out var boundsError))
                    return OpResult.Fail($"cannot fit renderer '{renderer.name}': {boundsError}");
                Mesh mesh = null;
                if (kind == "mesh" && !TryGetMeshSource(renderer.gameObject, out mesh, out var meshError))
                    return OpResult.Fail($"cannot add mesh collider for renderer '{renderer.name}': {meshError}");
                plans.Add((renderer, localBounds, mesh));
            }

            int replaced = 0;
            var colliderRefs = new JArray();
            using (var mutation = new MutationScope("Add Compound Colliders"))
            {
                if (replaceExisting)
                {
                    var visitedObjects = new HashSet<int>();
                    foreach (var plan in plans)
                    {
                        var gameObject = plan.renderer.gameObject;
                        if (!visitedObjects.Add(gameObject.GetInstanceID())) continue;
                        foreach (var existing in gameObject.GetComponents<Collider>())
                        {
                            if (existing == null || existing is CharacterController) continue;
                            mutation.Destroy(existing);
                            replaced++;
                        }
                    }
                }

                foreach (var plan in plans)
                {
                    var added = AddConfiguredCollider(
                        mutation,
                        plan.renderer.gameObject,
                        kind,
                        plan.localBounds,
                        isTrigger,
                        kind == "mesh",
                        plan.mesh);
                    colliderRefs.Add(ToolTargetRef.Capture(added)?.ToJson());
                }
                mutation.Touch(rootGo);
                mutation.Complete();
            }

            var result = OpResult.Success(id,
                $"added {plans.Count} {kind} collider(s) across child renderers" +
                (replaced > 0 ? $" (replaced {replaced} pre-existing)" : ""));
            result.Data = new JObject
            {
                ["target"] = ToolTargetRef.Capture(rootGo)?.ToJson(),
                ["kind"] = kind,
                ["addedCount"] = plans.Count,
                ["replacedCount"] = replaced,
                ["replaceExisting"] = replaceExisting,
                ["isTrigger"] = isTrigger,
                ["colliders"] = colliderRefs,
            };
            return result;
        }

        public static OpResult SetPhysicsMaterial(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");

            var colliderId = (int?)args["colliderInstanceId"] ?? 0;
            Collider collider;
            if (colliderId != 0)
            {
                collider = EditorUtility.InstanceIDToObject(colliderId) as Collider;
                if (collider == null)
                    return OpResult.Fail($"Collider not found: colliderInstanceId={colliderId}");
                if (collider.gameObject != go)
                    return OpResult.Fail("colliderInstanceId must identify a Collider on the target GameObject");
            }
            else
            {
                var colliders = go.GetComponents<Collider>().Where(candidate => candidate != null).ToArray();
                if (colliders.Length == 0) return OpResult.Fail("no Collider on GameObject");
                if (colliders.Length > 1)
                    return OpResult.Fail($"GameObject has {colliders.Length} Colliders; colliderInstanceId is required");
                collider = colliders[0];
            }

            var matRef = ((string)args["materialGuidOrPath"] ?? string.Empty).Trim();
            if (matRef.Length == 0) return OpResult.Fail("materialGuidOrPath is required");
            var normalizedRef = matRef.Replace('\\', '/');
            var path = normalizedRef.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                       normalizedRef.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
                ? normalizedRef
                : AssetDatabase.GUIDToAssetPath(normalizedRef);

#if UNITY_2023_3_OR_NEWER
            var mat = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
#else
            var mat = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(path);
#endif
            if (mat == null) return OpResult.Fail($"physics material not found: {matRef}");

            var changed = collider.sharedMaterial != mat;
            if (changed)
            {
                using (var mutation = new MutationScope("Set Physics Material"))
                {
                    mutation.Record(collider);
                    collider.sharedMaterial = mat;
                    if (PrefabUtility.IsPartOfPrefabInstance(collider))
                        PrefabUtility.RecordPrefabInstancePropertyModifications(collider);
                    mutation.Complete();
                }
            }

            var result = OpResult.Success(id, $"set physics material '{mat.name}' on {collider.GetType().Name}");
            result.Data = new JObject
            {
                ["target"] = ToolTargetRef.Capture(go)?.ToJson(),
                ["collider"] = ToolTargetRef.Capture(collider)?.ToJson(),
                ["material"] = ToolTargetRef.Capture(mat)?.ToJson(),
                ["changed"] = changed,
            };
            return result;
        }

        private static bool IsColliderKind(string kind) =>
            kind == "box" || kind == "sphere" || kind == "capsule" || kind == "mesh";

        private static bool TryGetMeshSource(GameObject gameObject, out Mesh mesh, out string error)
        {
            var meshFilter = gameObject.GetComponent<MeshFilter>();
            mesh = meshFilter != null ? meshFilter.sharedMesh : null;
            if (mesh == null)
            {
                error = "mesh kind requires a MeshFilter with a non-null sharedMesh on the collider GameObject";
                return false;
            }
            if (mesh.vertexCount == 0)
            {
                error = $"mesh '{mesh.name}' has no vertices";
                return false;
            }
            error = null;
            return true;
        }

        private static bool TryComputeLocalRendererBounds(
            Transform target,
            IEnumerable<Renderer> renderers,
            out Bounds bounds,
            out string error)
        {
            bounds = default;
            error = null;
            if (target == null)
            {
                error = "target Transform is missing";
                return false;
            }

            var scale = target.lossyScale;
            if (Mathf.Abs(scale.x) < 0.000001f || Mathf.Abs(scale.y) < 0.000001f || Mathf.Abs(scale.z) < 0.000001f)
            {
                error = "target Transform has a zero world-scale axis";
                return false;
            }

            bool hasPoint = false;
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                var rendererBounds = renderer.localBounds;
                if (!IsFiniteVector(rendererBounds.center) || !IsFiniteVector(rendererBounds.size))
                {
                    error = $"Renderer '{renderer.name}' has non-finite local bounds";
                    return false;
                }

                var matrix = target.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                var min = rendererBounds.min;
                var max = rendererBounds.max;
                for (int cornerIndex = 0; cornerIndex < 8; cornerIndex++)
                {
                    var corner = new Vector3(
                        (cornerIndex & 1) == 0 ? min.x : max.x,
                        (cornerIndex & 2) == 0 ? min.y : max.y,
                        (cornerIndex & 4) == 0 ? min.z : max.z);
                    var point = matrix.MultiplyPoint3x4(corner);
                    if (!IsFiniteVector(point))
                    {
                        error = $"Renderer '{renderer.name}' produced non-finite local bounds";
                        return false;
                    }
                    if (!hasPoint)
                    {
                        bounds = new Bounds(point, Vector3.zero);
                        hasPoint = true;
                    }
                    else
                    {
                        bounds.Encapsulate(point);
                    }
                }
            }

            if (hasPoint) return true;
            error = "no Renderer found — cannot compute local bounds";
            return false;
        }

        private static Collider AddConfiguredCollider(
            MutationScope mutation,
            GameObject gameObject,
            string kind,
            Bounds localBounds,
            bool isTrigger,
            bool convex,
            Mesh mesh)
        {
            switch (kind)
            {
                case "box":
                    var box = mutation.AddComponent<BoxCollider>(gameObject);
                    box.center = localBounds.center;
                    box.size = localBounds.size;
                    box.isTrigger = isTrigger;
                    return box;
                case "sphere":
                    var sphere = mutation.AddComponent<SphereCollider>(gameObject);
                    sphere.center = localBounds.center;
                    sphere.radius = Mathf.Max(localBounds.size.x, localBounds.size.y, localBounds.size.z) * 0.5f;
                    sphere.isTrigger = isTrigger;
                    return sphere;
                case "capsule":
                    var capsule = mutation.AddComponent<CapsuleCollider>(gameObject);
                    ConfigureCapsule(capsule, localBounds);
                    capsule.isTrigger = isTrigger;
                    return capsule;
                case "mesh":
                    var meshCollider = mutation.AddComponent<MeshCollider>(gameObject);
                    meshCollider.convex = convex;
                    meshCollider.sharedMesh = mesh;
                    meshCollider.isTrigger = isTrigger;
                    return meshCollider;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported collider kind");
            }
        }

        private static void ConfigureCapsule(CapsuleCollider capsule, Bounds localBounds)
        {
            var size = localBounds.size;
            int direction;
            float height;
            float radius;
            if (size.x >= size.y && size.x >= size.z)
            {
                direction = 0;
                height = size.x;
                radius = Mathf.Max(size.y, size.z) * 0.5f;
            }
            else if (size.y >= size.z)
            {
                direction = 1;
                height = size.y;
                radius = Mathf.Max(size.x, size.z) * 0.5f;
            }
            else
            {
                direction = 2;
                height = size.z;
                radius = Mathf.Max(size.x, size.y) * 0.5f;
            }

            capsule.center = localBounds.center;
            capsule.direction = direction;
            capsule.height = height;
            capsule.radius = radius;
        }

        private static bool IsFiniteVector(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        // ============== Helpers ==============

        private static bool TryParseLayerMask(JToken token, out int mask, out string error)
        {
            mask = Physics.DefaultRaycastLayers;
            error = null;
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token.Type == JTokenType.Integer)
            {
                if (!long.TryParse(token.ToString(), out var rawMask) || rawMask < int.MinValue || rawMask > int.MaxValue)
                {
                    error = "layerMask integer must be between -2147483648 and 2147483647";
                    return false;
                }
                mask = (int)rawMask;
                return true;
            }

            if (token.Type == JTokenType.String)
            {
                var name = ((string)token ?? string.Empty).Trim();
                var layer = LayerMask.NameToLayer(name);
                if (layer < 0)
                {
                    error = $"unknown layer '{name}'";
                    return false;
                }
                mask = 1 << layer;
                return true;
            }

            if (token is JArray names && names.Count > 0)
            {
                mask = 0;
                foreach (var item in names)
                {
                    if (item.Type != JTokenType.String)
                    {
                        error = "layerMask arrays must contain only layer names";
                        return false;
                    }
                    var name = ((string)item ?? string.Empty).Trim();
                    var layer = LayerMask.NameToLayer(name);
                    if (layer < 0)
                    {
                        error = $"unknown layer '{name}'";
                        return false;
                    }
                    mask |= 1 << layer;
                }
                return true;
            }

            error = "layerMask must be an integer, layer name, or non-empty array of layer names";
            return false;
        }

        private static bool TryParseTriggerInteraction(JToken token, out QueryTriggerInteraction value, out string error)
        {
            value = QueryTriggerInteraction.UseGlobal;
            error = null;
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token.Type == JTokenType.Boolean)
            {
                value = (bool)token ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore;
                return true;
            }
            if (token.Type == JTokenType.String
                && Enum.TryParse((string)token, true, out QueryTriggerInteraction parsed)
                && Enum.IsDefined(typeof(QueryTriggerInteraction), parsed))
            {
                value = parsed;
                return true;
            }
            error = "hitTriggers must be true, false, 'UseGlobal', 'Collide', or 'Ignore'";
            return false;
        }

        private static bool TryParseLayerIndex(JToken token, out int layer, out string error)
        {
            layer = -1;
            error = null;
            if (token.Type == JTokenType.Integer)
            {
                layer = (int)token;
                if (layer >= 0 && layer < 32) return true;
                error = "layer index must be between 0 and 31";
                return false;
            }
            if (token.Type == JTokenType.String)
            {
                var name = ((string)token ?? string.Empty).Trim();
                layer = LayerMask.NameToLayer(name);
                if (layer >= 0) return true;
                error = $"unknown layer '{name}'";
                return false;
            }
            error = "layer must be an index or layer name";
            return false;
        }

        private static bool TryParseFiniteVector3(JToken token, string name, Vector3 fallback, bool required, out Vector3 value, out string error)
        {
            value = fallback;
            error = null;
            if (token == null || token.Type == JTokenType.Null)
            {
                if (!required) return true;
                error = $"{name} is required";
                return false;
            }

            if (token is JArray array && array.Count == 3
                && TryReadFiniteFloat(array[0], out var x)
                && TryReadFiniteFloat(array[1], out var y)
                && TryReadFiniteFloat(array[2], out var z))
            {
                value = new Vector3(x, y, z);
                return true;
            }
            if (token is JObject obj && obj["x"] != null && obj["y"] != null && obj["z"] != null
                && TryReadFiniteFloat(obj["x"], out x)
                && TryReadFiniteFloat(obj["y"], out y)
                && TryReadFiniteFloat(obj["z"], out z))
            {
                value = new Vector3(x, y, z);
                return true;
            }

            error = $"{name} must be a finite Vector3 [x,y,z] or {{x,y,z}}";
            return false;
        }

        private static bool TryParseNonNegativeVector3(JToken token, string name, out Vector3 value, out string error)
        {
            if (!TryParseFiniteVector3(token, name, Vector3.zero, true, out value, out error)) return false;
            if (value.x >= 0f && value.y >= 0f && value.z >= 0f) return true;
            error = $"{name} components must be non-negative";
            return false;
        }

        private static bool TryParseFiniteRotation(JToken token, out Quaternion value, out string error)
        {
            value = Quaternion.identity;
            error = null;
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token is JArray array && (array.Count == 3 || array.Count == 4)
                && TryReadFiniteFloat(array[0], out var x)
                && TryReadFiniteFloat(array[1], out var y)
                && TryReadFiniteFloat(array[2], out var z))
            {
                if (array.Count == 3)
                {
                    value = Quaternion.Euler(x, y, z);
                    return true;
                }
                if (TryReadFiniteFloat(array[3], out var w))
                {
                    value = new Quaternion(x, y, z, w);
                    return true;
                }
            }
            if (token is JObject obj && obj["x"] != null && obj["y"] != null && obj["z"] != null
                && TryReadFiniteFloat(obj["x"], out x)
                && TryReadFiniteFloat(obj["y"], out y)
                && TryReadFiniteFloat(obj["z"], out z))
            {
                if (obj["w"] == null)
                {
                    value = Quaternion.Euler(x, y, z);
                    return true;
                }
                if (TryReadFiniteFloat(obj["w"], out var w))
                {
                    value = new Quaternion(x, y, z, w);
                    return true;
                }
            }
            error = "rotation must contain three finite Euler components or four finite quaternion components";
            return false;
        }

        private static bool TryParseNonNegativeNumber(JToken token, string name, float fallback, bool allowPositiveInfinity, out float value, out string error)
        {
            value = fallback;
            error = null;
            if (token == null || token.Type == JTokenType.Null) return true;
            if ((token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
            {
                error = $"{name} must be a number";
                return false;
            }
            value = (float)token;
            if (float.IsNaN(value) || value < 0f || (!allowPositiveInfinity && float.IsInfinity(value)))
            {
                error = $"{name} must be a non-negative {(allowPositiveInfinity ? "number" : "finite number")}";
                return false;
            }
            return true;
        }

        private static bool TryNormalizeDirection(Vector3 value, string name, out Vector3 normalized, out string error)
        {
            normalized = Vector3.zero;
            error = null;
            if (value.sqrMagnitude <= 1e-12f)
            {
                error = $"{name} must be non-zero";
                return false;
            }
            normalized = value.normalized;
            return true;
        }

        private static bool TryReadFiniteFloat(JToken token, out float value)
        {
            value = 0f;
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) return false;
            value = (float)token;
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static int CompareHits(RaycastHit left, RaycastHit right)
        {
            var distance = left.distance.CompareTo(right.distance);
            if (distance != 0) return distance;
            var leftId = left.collider != null ? left.collider.GetInstanceID() : 0;
            var rightId = right.collider != null ? right.collider.GetInstanceID() : 0;
            return leftId.CompareTo(rightId);
        }

        private static int CompareColliders(Collider left, Collider right)
        {
            var name = string.Compare(left != null ? left.gameObject.name : string.Empty, right != null ? right.gameObject.name : string.Empty, StringComparison.Ordinal);
            if (name != 0) return name;
            var leftId = left != null ? left.GetInstanceID() : 0;
            var rightId = right != null ? right.GetInstanceID() : 0;
            return leftId.CompareTo(rightId);
        }

        private static JObject HitJson(RaycastHit hit)
        {
            var collider = hit.collider;
            var gameObject = collider != null ? collider.gameObject : null;
            return new JObject
            {
                ["name"] = gameObject != null ? gameObject.name : null,
                ["instanceId"] = gameObject != null ? gameObject.GetInstanceID() : 0,
                ["target"] = ToolTargetRef.Capture(gameObject)?.ToJson(),
                ["colliderTarget"] = ToolTargetRef.Capture(collider)?.ToJson(),
                ["tag"] = gameObject != null ? gameObject.tag : null,
                ["layer"] = gameObject != null ? gameObject.layer : -1,
                ["layerName"] = gameObject != null ? LayerMask.LayerToName(gameObject.layer) : null,
                ["point"] = JsonHelpers.Vec3(hit.point),
                ["normal"] = JsonHelpers.Vec3(hit.normal),
                ["distance"] = hit.distance,
                ["colliderType"] = collider != null ? collider.GetType().FullName : null,
                ["isTrigger"] = collider != null && collider.isTrigger,
            };
        }

        private static JObject ColliderJson(Collider collider)
        {
            var gameObject = collider != null ? collider.gameObject : null;
            return new JObject
            {
                ["name"] = gameObject != null ? gameObject.name : null,
                ["instanceId"] = gameObject != null ? gameObject.GetInstanceID() : 0,
                ["target"] = ToolTargetRef.Capture(gameObject)?.ToJson(),
                ["colliderTarget"] = ToolTargetRef.Capture(collider)?.ToJson(),
                ["tag"] = gameObject != null ? gameObject.tag : null,
                ["layer"] = gameObject != null ? gameObject.layer : -1,
                ["layerName"] = gameObject != null ? LayerMask.LayerToName(gameObject.layer) : null,
                ["colliderType"] = collider != null ? collider.GetType().FullName : null,
                ["isTrigger"] = collider != null && collider.isTrigger,
            };
        }

        private static JObject BoundsJson(Bounds bounds) => new JObject
        {
            ["center"] = JsonHelpers.Vec3(bounds.center),
            ["size"] = JsonHelpers.Vec3(bounds.size),
            ["min"] = JsonHelpers.Vec3(bounds.min),
            ["max"] = JsonHelpers.Vec3(bounds.max),
        };

        private static JArray BuildCollisionLayerList(int layer)
        {
            var result = new JArray();
            for (var index = 0; index < 32; index++)
            {
                var name = LayerMask.LayerToName(index);
                if (Physics.GetIgnoreLayerCollision(layer, index)) continue;
                result.Add(new JObject { ["index"] = index, ["name"] = name });
            }
            return result;
        }

        private static string DisplayLayerName(string name) => string.IsNullOrEmpty(name) ? "(unnamed)" : name;

        private static int? ParseLayerInt(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Integer) return (int)t;
            if (t.Type == JTokenType.String)
            {
                var n = LayerMask.NameToLayer((string)t);
                return n >= 0 ? n : (int?)null;
            }
            return null;
        }

        private static string DescribeHit(RaycastHit h)
        {
            if (h.collider == null) return "(null collider)";
            var go = h.collider.gameObject;
            return $"#{go.GetInstanceID()} '{go.name}'  d={h.distance:F2}  tag={go.tag} layer={LayerMask.LayerToName(go.layer)}  "
                + $"point=[{h.point.x:F2},{h.point.y:F2},{h.point.z:F2}]  collider={h.collider.GetType().Name}{(h.collider.isTrigger ? " (trigger)" : "")}";
        }

        private static string DescribeCollider(Collider c)
        {
            if (c == null) return "(null)";
            var go = c.gameObject;
            return $"#{go.GetInstanceID()} '{go.name}'  tag={go.tag} layer={LayerMask.LayerToName(go.layer)}  {c.GetType().Name}{(c.isTrigger ? " (trigger)" : "")}";
        }

        private static void Bump(Dictionary<string, int> d, string key)
        {
            if (string.IsNullOrEmpty(key)) key = "(empty)";
            if (d.TryGetValue(key, out var v)) d[key] = v + 1;
            else d[key] = 1;
        }
    }
}
