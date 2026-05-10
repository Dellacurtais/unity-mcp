using System;
using System.Collections.Generic;
using System.Linq;
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
            var origin = JsonHelpers.ParseVector3(args["origin"]);
            var direction = JsonHelpers.ParseVector3(args["direction"], Vector3.forward);
            if (direction == Vector3.zero) return OpResult.Fail("direction must be non-zero");
            var maxDistance = (float?)args["maxDistance"] ?? Mathf.Infinity;
            var layerMask = ParseLayerMask(args["layerMask"]);
            var hitTriggers = ParseTriggerInteraction(args["hitTriggers"]);
            var multiple = (bool?)args["multiple"] ?? false;

            var sb = new System.Text.StringBuilder();
            if (multiple)
            {
                var hits = Physics.RaycastAll(origin, direction.normalized, maxDistance, layerMask, hitTriggers);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                sb.AppendLine($"raycast hit {hits.Length} collider(s):");
                foreach (var h in hits) sb.AppendLine("  " + DescribeHit(h));
            }
            else
            {
                if (Physics.Raycast(origin, direction.normalized, out var hit, maxDistance, layerMask, hitTriggers))
                    sb.AppendLine("raycast hit:\n  " + DescribeHit(hit));
                else
                    sb.AppendLine("raycast: no hit");
            }
            return new OpResult { Ok = true, Summary = sb.ToString(), InstanceID = 0 };
        }

        public static OpResult OverlapQuery(JObject args)
        {
            var shape = ((string)args["shape"] ?? "box").ToLowerInvariant();
            var center = JsonHelpers.ParseVector3(args["center"]);
            var layerMask = ParseLayerMask(args["layerMask"]);
            var hitTriggers = ParseTriggerInteraction(args["hitTriggers"]);

            Collider[] hits;
            switch (shape)
            {
                case "box":
                    var halfExtents = JsonHelpers.ParseVector3(args["halfExtents"], Vector3.one * 0.5f);
                    var rot = args["rotation"] != null ? JsonHelpers.ParseRotation(args["rotation"]) : Quaternion.identity;
                    hits = Physics.OverlapBox(center, halfExtents, rot, layerMask, hitTriggers);
                    break;
                case "sphere":
                    var radius = (float?)args["radius"] ?? 1f;
                    hits = Physics.OverlapSphere(center, radius, layerMask, hitTriggers);
                    break;
                case "capsule":
                    var p0 = JsonHelpers.ParseVector3(args["pointA"]);
                    var p1 = JsonHelpers.ParseVector3(args["pointB"]);
                    var capRadius = (float?)args["radius"] ?? 0.5f;
                    hits = Physics.OverlapCapsule(p0, p1, capRadius, layerMask, hitTriggers);
                    break;
                default: return OpResult.Fail($"unknown shape '{shape}' (use box/sphere/capsule)");
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"overlap_{shape}: {hits.Length} collider(s) inside:");
            foreach (var c in hits.Take(50)) sb.AppendLine("  " + DescribeCollider(c));
            if (hits.Length > 50) sb.AppendLine($"  [+{hits.Length - 50} more]");
            return new OpResult { Ok = true, Summary = sb.ToString() };
        }

        public static OpResult SweepQuery(JObject args)
        {
            var shape = ((string)args["shape"] ?? "box").ToLowerInvariant();
            var direction = JsonHelpers.ParseVector3(args["direction"], Vector3.forward);
            if (direction == Vector3.zero) return OpResult.Fail("direction must be non-zero");
            var maxDistance = (float?)args["maxDistance"] ?? Mathf.Infinity;
            var layerMask = ParseLayerMask(args["layerMask"]);
            var hitTriggers = ParseTriggerInteraction(args["hitTriggers"]);

            RaycastHit[] hits;
            switch (shape)
            {
                case "box":
                    var center = JsonHelpers.ParseVector3(args["center"]);
                    var halfExtents = JsonHelpers.ParseVector3(args["halfExtents"], Vector3.one * 0.5f);
                    var rot = args["rotation"] != null ? JsonHelpers.ParseRotation(args["rotation"]) : Quaternion.identity;
                    hits = Physics.BoxCastAll(center, halfExtents, direction.normalized, rot, maxDistance, layerMask, hitTriggers);
                    break;
                case "sphere":
                    var origin = JsonHelpers.ParseVector3(args["origin"]);
                    var radius = (float?)args["radius"] ?? 1f;
                    hits = Physics.SphereCastAll(origin, radius, direction.normalized, maxDistance, layerMask, hitTriggers);
                    break;
                case "capsule":
                    var p0 = JsonHelpers.ParseVector3(args["pointA"]);
                    var p1 = JsonHelpers.ParseVector3(args["pointB"]);
                    var capRadius = (float?)args["radius"] ?? 0.5f;
                    hits = Physics.CapsuleCastAll(p0, p1, capRadius, direction.normalized, maxDistance, layerMask, hitTriggers);
                    break;
                default: return OpResult.Fail($"unknown shape '{shape}'");
            }

            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"sweep_{shape}: {hits.Length} hit(s):");
            foreach (var h in hits.Take(50)) sb.AppendLine("  " + DescribeHit(h));
            if (hits.Length > 50) sb.AppendLine($"  [+{hits.Length - 50} more]");
            return new OpResult { Ok = true, Summary = sb.ToString() };
        }

        public static OpResult CheckGround(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");

            var origin = go.transform.position + JsonHelpers.ParseVector3(args["originOffset"]);
            var down = args["down"] != null ? JsonHelpers.ParseVector3(args["down"]) : Vector3.down;
            var maxDistance = (float?)args["maxDistance"] ?? 100f;
            var layerMask = ParseLayerMask(args["layerMask"]);
            var hitTriggers = ParseTriggerInteraction(args["hitTriggers"]);

            var sb = new System.Text.StringBuilder();
            if (Physics.Raycast(origin, down.normalized, out var hit, maxDistance, layerMask, hitTriggers))
            {
                var hgo = hit.collider.gameObject;
                sb.AppendLine($"grounded: yes  distance={hit.distance:F3}  on '{hgo.name}'");
                sb.AppendLine($"  tag='{hgo.tag}'  layer={hgo.layer} ('{LayerMask.LayerToName(hgo.layer)}')");
                sb.AppendLine($"  point=[{hit.point.x:F2}, {hit.point.y:F2}, {hit.point.z:F2}]");
                sb.AppendLine($"  normal=[{hit.normal.x:F2}, {hit.normal.y:F2}, {hit.normal.z:F2}]");
                sb.AppendLine($"  collider={hit.collider.GetType().Name}  isTrigger={hit.collider.isTrigger}");
            }
            else
            {
                sb.AppendLine($"grounded: NO  (no collider within {maxDistance} along {down})");
            }
            return new OpResult { Ok = true, Summary = sb.ToString(), InstanceID = id };
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
            return new OpResult { Ok = true, Summary = sb.ToString(), InstanceID = id };
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

            var snapshot = Bootstrap.Snapshot;
            snapshot.RefreshIfNeeded();

            var matches = new List<(GameObject go, float dist, int depth)>();
            foreach (var info in snapshot.All())
            {
                var go = EditorUtility.InstanceIDToObject(info.InstanceID) as GameObject;
                if (go == null) continue;

                if (!string.IsNullOrEmpty(tag) && go.tag != tag) continue;
                if (layerFilter.HasValue && go.layer != layerFilter.Value) continue;
                if (!string.IsNullOrEmpty(nameContains)
                    && go.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;

                if (componentTypes != null && componentTypes.Length > 0)
                {
                    bool hasAll = true;
                    foreach (var ct in componentTypes)
                    {
                        var t = ComponentTypeInspector.ResolveType(ct);
                        if (t == null || go.GetComponent(t) == null) { hasAll = false; break; }
                    }
                    if (!hasAll) continue;
                }

                float distance = 0;
                if (center.HasValue)
                {
                    distance = Vector3.Distance(center.Value, go.transform.position);
                    if (radius > 0 && distance > radius) continue;
                }

                if (!string.IsNullOrEmpty(pathContains))
                {
                    var path = snapshot.GetPath(info.InstanceID);
                    if (path == null || path.IndexOf(pathContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }

                int depth = 0;
                var p = go.transform.parent;
                while (p != null) { depth++; p = p.parent; }

                matches.Add((go, distance, depth));
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
            var only = ParseLayerInt(args["layer"]);
            var sb = new System.Text.StringBuilder();
            if (only.HasValue)
            {
                int i = only.Value;
                sb.AppendLine($"layer {i} ('{LayerMask.LayerToName(i)}') collides with:");
                for (int j = 0; j < 32; j++)
                {
                    var name = LayerMask.LayerToName(j);
                    if (string.IsNullOrEmpty(name)) continue;
                    bool ignored = Physics.GetIgnoreLayerCollision(i, j);
                    if (!ignored) sb.AppendLine($"  {j} '{name}'");
                }
            }
            else
            {
                sb.AppendLine("collision matrix (X = collides, . = ignored):");
                sb.Append("       ");
                for (int j = 0; j < 32; j++)
                {
                    if (string.IsNullOrEmpty(LayerMask.LayerToName(j))) continue;
                    sb.Append($"{j,3}");
                }
                sb.AppendLine();
                for (int i = 0; i < 32; i++)
                {
                    var rowName = LayerMask.LayerToName(i);
                    if (string.IsNullOrEmpty(rowName)) continue;
                    sb.Append($"{i,2} {rowName,-12}");
                    for (int j = 0; j < 32; j++)
                    {
                        if (string.IsNullOrEmpty(LayerMask.LayerToName(j))) continue;
                        sb.Append(Physics.GetIgnoreLayerCollision(i, j) ? "  ." : "  X");
                    }
                    sb.AppendLine();
                }
            }
            return new OpResult { Ok = true, Summary = sb.ToString() };
        }

        // ============== Collider authoring ==============

        public static OpResult AutoFitCollider(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            var kind = ((string)args["kind"] ?? "box").ToLowerInvariant();
            var useChildren = (bool?)args["useChildren"] ?? true;
            var replaceExisting = (bool?)args["replaceExisting"] ?? true;
            var isTrigger = (bool?)args["isTrigger"] ?? false;

            var bounds = ComputeWorldBounds(go, useChildren);
            if (!bounds.HasValue) return OpResult.Fail("no Renderer found — cannot auto-fit");
            var b = bounds.Value;

            if (replaceExisting)
            {
                foreach (var existing in go.GetComponents<Collider>())
                {
                    if (existing == null) continue;
                    if (existing is CharacterController) continue; // never auto-remove
                    Undo.DestroyObjectImmediate(existing);
                }
            }

            // Convert world-space bounds into the GO's local space.
            var localCenter = go.transform.InverseTransformPoint(b.center);
            var inverseLossy = new Vector3(
                go.transform.lossyScale.x != 0 ? 1f / go.transform.lossyScale.x : 1f,
                go.transform.lossyScale.y != 0 ? 1f / go.transform.lossyScale.y : 1f,
                go.transform.lossyScale.z != 0 ? 1f / go.transform.lossyScale.z : 1f);
            var localSize = Vector3.Scale(b.size, inverseLossy);

            Collider added = null;
            switch (kind)
            {
                case "box":
                    var box = Undo.AddComponent<BoxCollider>(go);
                    box.center = localCenter;
                    box.size = localSize;
                    box.isTrigger = isTrigger;
                    added = box;
                    break;
                case "sphere":
                    var sphere = Undo.AddComponent<SphereCollider>(go);
                    sphere.center = localCenter;
                    sphere.radius = Mathf.Max(localSize.x, localSize.y, localSize.z) * 0.5f;
                    sphere.isTrigger = isTrigger;
                    added = sphere;
                    break;
                case "capsule":
                    var cap = Undo.AddComponent<CapsuleCollider>(go);
                    cap.center = localCenter;
                    cap.radius = Mathf.Max(localSize.x, localSize.z) * 0.5f;
                    cap.height = localSize.y;
                    cap.direction = 1; // Y
                    cap.isTrigger = isTrigger;
                    added = cap;
                    break;
                case "mesh":
                    var mc = Undo.AddComponent<MeshCollider>(go);
                    var mf = go.GetComponent<MeshFilter>();
                    if (mf != null) mc.sharedMesh = mf.sharedMesh;
                    mc.convex = (bool?)args["convex"] ?? false;
                    mc.isTrigger = isTrigger;
                    added = mc;
                    break;
                default: return OpResult.Fail($"unknown kind '{kind}' (box/sphere/capsule/mesh)");
            }
            return OpResult.Success(added.GetInstanceID(),
                $"added {kind} collider on '{go.name}'  size~{b.size.x:F2}×{b.size.y:F2}×{b.size.z:F2}");
        }

        public static OpResult AddCompoundColliders(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var rootGo = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (rootGo == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            var kind = ((string)args["kind"] ?? "box").ToLowerInvariant();
            var isTrigger = (bool?)args["isTrigger"] ?? false;
            var replaceExisting = (bool?)args["replaceExisting"] ?? false;

            var renderers = rootGo.GetComponentsInChildren<Renderer>(true);
            int added = 0, replaced = 0;
            foreach (var r in renderers)
            {
                var go = r.gameObject;
                if (replaceExisting)
                {
                    foreach (var ex in go.GetComponents<Collider>())
                    {
                        if (ex is CharacterController) continue;
                        Undo.DestroyObjectImmediate(ex);
                        replaced++;
                    }
                }

                var b = r.bounds;
                var localCenter = go.transform.InverseTransformPoint(b.center);
                var inverseLossy = new Vector3(
                    go.transform.lossyScale.x != 0 ? 1f / go.transform.lossyScale.x : 1f,
                    go.transform.lossyScale.y != 0 ? 1f / go.transform.lossyScale.y : 1f,
                    go.transform.lossyScale.z != 0 ? 1f / go.transform.lossyScale.z : 1f);
                var localSize = Vector3.Scale(b.size, inverseLossy);

                switch (kind)
                {
                    case "box":
                        var box = Undo.AddComponent<BoxCollider>(go);
                        box.center = localCenter; box.size = localSize; box.isTrigger = isTrigger;
                        break;
                    case "sphere":
                        var sp = Undo.AddComponent<SphereCollider>(go);
                        sp.center = localCenter; sp.radius = Mathf.Max(localSize.x, localSize.y, localSize.z) * 0.5f; sp.isTrigger = isTrigger;
                        break;
                    case "capsule":
                        var cap = Undo.AddComponent<CapsuleCollider>(go);
                        cap.center = localCenter; cap.radius = Mathf.Max(localSize.x, localSize.z) * 0.5f; cap.height = localSize.y; cap.direction = 1; cap.isTrigger = isTrigger;
                        break;
                    case "mesh":
                        var mc = Undo.AddComponent<MeshCollider>(go);
                        var mf = go.GetComponent<MeshFilter>(); if (mf != null) mc.sharedMesh = mf.sharedMesh;
                        mc.convex = true; mc.isTrigger = isTrigger; // convex required for trigger or rigidbody
                        break;
                    default: return OpResult.Fail($"unknown kind '{kind}'");
                }
                added++;
            }
            return OpResult.Success(id, $"added {added} {kind} collider(s) across child renderers" + (replaced > 0 ? $" (replaced {replaced} pre-existing)" : ""));
        }

        public static OpResult SetPhysicsMaterial(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return OpResult.Fail($"GameObject not found: instanceId={id}");
            var collider = go.GetComponent<Collider>();
            if (collider == null) return OpResult.Fail("no Collider on GameObject");

            var matRef = (string)args["materialGuidOrPath"];
            if (string.IsNullOrEmpty(matRef)) return OpResult.Fail("materialGuidOrPath is required");
            var path = matRef.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? matRef : AssetDatabase.GUIDToAssetPath(matRef);

#if UNITY_2023_3_OR_NEWER
            var mat = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
#else
            var mat = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(path);
#endif
            if (mat == null) return OpResult.Fail($"physics material not found: {matRef}");

            Undo.RecordObject(collider, "Set Physics Material");
            collider.sharedMaterial = mat;
            return OpResult.Success(id, $"set physics material '{mat.name}' on {collider.GetType().Name}");
        }

        // ============== Helpers ==============

        private static int ParseLayerMask(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return Physics.DefaultRaycastLayers;
            if (t.Type == JTokenType.Integer) return (int)t;
            if (t.Type == JTokenType.String)
            {
                var m = LayerMask.GetMask((string)t);
                return m != 0 ? m : Physics.DefaultRaycastLayers;
            }
            if (t is JArray a)
            {
                var names = a.Select(x => (string)x).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                if (names.Length > 0) return LayerMask.GetMask(names);
            }
            return Physics.DefaultRaycastLayers;
        }

        private static QueryTriggerInteraction ParseTriggerInteraction(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return QueryTriggerInteraction.UseGlobal;
            if (t.Type == JTokenType.Boolean)
                return (bool)t ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore;
            if (t.Type == JTokenType.String
                && Enum.TryParse<QueryTriggerInteraction>((string)t, true, out var v)) return v;
            return QueryTriggerInteraction.UseGlobal;
        }

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
