using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace Maicon.SceneAnalyzer.Editor.NavMesh
{
    public static class NavMeshSurfaceAuthor
    {
        public static OpResult ListSurfaces(JObject args)
        {
            var surfaces = FindLoadedSurfaces();
            var items = new JArray(surfaces.Select(SurfaceJson));
            return new OpResult
            {
                Ok = true,
                Summary = $"{items.Count} NavMeshSurface(s)",
                Data = new JObject { ["count"] = items.Count, ["surfaces"] = items },
            };
        }

        public static OpResult BakeSurface(JObject args)
        {
            List<NavMeshSurface> surfaces;
            var id = (int?)args["surfaceInstanceId"] ?? (int?)args["instanceId"] ?? 0;
            if (id != 0)
            {
                if (!TryResolveSurface(id, out var surface, out var error)) return OpResult.Fail(error);
                surfaces = new List<NavMeshSurface> { surface };
            }
            else
            {
                surfaces = FindLoadedSurfaces().ToList();
            }
            if (surfaces.Count == 0) return OpResult.Fail("no loaded NavMeshSurface components were found");

            foreach (var surface in surfaces)
            {
                if (!TryValidateAgentType(surface.agentTypeID, out var error))
                    return OpResult.Fail($"'{surface.name}': {error}");
                if (PrefabStageUtility.GetPrefabStage(surface.gameObject) != null)
                    return OpResult.Fail("synchronous NavMeshSurface bake does not support Prefab Mode; use the AI Navigation Inspector and save the Prefab so its asset lifecycle is managed by the package");
            }

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Bake NavMeshSurface");
            var snapshots = surfaces.Select(surface => new BakeSnapshot(surface)).ToList();
            foreach (var snapshot in snapshots)
            {
                if (TryBakeAndPersist(snapshot, out var error)) continue;
                RollbackBakes(snapshots);
                Undo.RevertAllDownToGroup(undoGroup);
                return OpResult.Fail(error);
            }

            try
            {
                AssetDatabase.SaveAssets();
            }
            catch (Exception ex)
            {
                RollbackBakes(snapshots);
                Undo.RevertAllDownToGroup(undoGroup);
                return OpResult.Fail($"failed to save baked NavMeshData assets: {ex.Message}");
            }
            Undo.CollapseUndoOperations(undoGroup);

            var baked = new JArray();
            foreach (var snapshot in snapshots)
            {
                var item = new JObject
                {
                    ["instanceId"] = snapshot.Surface.gameObject.GetInstanceID(),
                    ["surfaceInstanceId"] = snapshot.Surface.GetInstanceID(),
                    ["name"] = snapshot.Surface.gameObject.name,
                    ["assetPath"] = snapshot.NewPath,
                };
                if (!string.IsNullOrEmpty(snapshot.PreviousPath) && snapshot.PreviousData != snapshot.NewData)
                    item["retainedPreviousAssetPath"] = snapshot.PreviousPath;
                baked.Add(item);
            }

            return new OpResult
            {
                Ok = true,
                InstanceID = surfaces.Count == 1 ? surfaces[0].GetInstanceID() : 0,
                Summary = $"baked and persisted {surfaces.Count} NavMeshSurface(s)",
                Data = new JObject { ["bakedCount"] = surfaces.Count, ["surfaces"] = baked },
                CreatedAssetPath = surfaces.Count == 1 ? (string)baked[0]["assetPath"] : null,
            };
        }

        public static OpResult AddSurface(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            if (!TryResolveSceneGameObject(id, out var go, out var error)) return OpResult.Fail(error);
            var existing = go.GetComponent<NavMeshSurface>();
            var agentTypeId = (int?)args["agentTypeID"] ?? existing?.agentTypeID ?? 0;
            if (!TryValidateAgentType(agentTypeId, out error)) return OpResult.Fail(error);

            var collectObjects = existing?.collectObjects ?? CollectObjects.All;
            if (args["collectObjects"] != null && (!Enum.TryParse((string)args["collectObjects"], true, out collectObjects) || !Enum.IsDefined(typeof(CollectObjects), collectObjects)))
                return OpResult.Fail($"unknown collectObjects value '{(string)args["collectObjects"]}'");
            var useGeometry = existing?.useGeometry ?? NavMeshCollectGeometry.RenderMeshes;
            if (args["useGeometry"] != null && (!Enum.TryParse((string)args["useGeometry"], true, out useGeometry) || !Enum.IsDefined(typeof(NavMeshCollectGeometry), useGeometry)))
                return OpResult.Fail($"unknown useGeometry value '{(string)args["useGeometry"]}'");

            var layerMask = existing != null ? existing.layerMask.value : ~0;
            if (args["layerMask"] != null && !TryParseLayerMask(args["layerMask"], out layerMask, out error)) return OpResult.Fail($"layerMask: {error}");
            var defaultArea = existing?.defaultArea ?? 0;
            if (args["defaultArea"] != null && !NavMeshAuthor.TryParseAreaIndex(args["defaultArea"], out defaultArea, out error)) return OpResult.Fail($"defaultArea: {error}");

            Vector3? size = null;
            if (args["size"] != null)
            {
                if (!TryParseVector3(args["size"], out var parsed, out error) || parsed.x <= 0f || parsed.y <= 0f || parsed.z <= 0f)
                    return OpResult.Fail("size must contain three positive finite values");
                size = parsed;
            }
            Vector3? center = null;
            if (args["center"] != null)
            {
                if (!TryParseVector3(args["center"], out var parsed, out error)) return OpResult.Fail($"center: {error}");
                center = parsed;
            }
            if (args["voxelSize"] != null && (!TryReadPositive(args["voxelSize"], out _, out error))) return OpResult.Fail($"voxelSize: {error}");
            if (args["tileSize"] != null)
            {
                var tileSize = (int)args["tileSize"];
                if (tileSize < 16 || tileSize > 1024) return OpResult.Fail("tileSize must be between 16 and 1024 voxels");
            }
            if (args["minRegionArea"] != null && (!TryReadNonNegative(args["minRegionArea"], out _, out error))) return OpResult.Fail($"minRegionArea: {error}");

            var surface = existing;
            if (surface == null) surface = Undo.AddComponent<NavMeshSurface>(go);
            else Undo.RecordObject(surface, "Configure NavMeshSurface");
            surface.agentTypeID = agentTypeId;
            surface.collectObjects = collectObjects;
            surface.useGeometry = useGeometry;
            surface.layerMask = layerMask;
            surface.defaultArea = defaultArea;
            if (size.HasValue) surface.size = size.Value;
            if (center.HasValue) surface.center = center.Value;
            if (args["overrideVoxelSize"] != null) surface.overrideVoxelSize = (bool)args["overrideVoxelSize"];
            if (args["voxelSize"] != null) surface.voxelSize = (float)args["voxelSize"];
            if (args["overrideTileSize"] != null) surface.overrideTileSize = (bool)args["overrideTileSize"];
            if (args["tileSize"] != null) surface.tileSize = (int)args["tileSize"];
            if (args["minRegionArea"] != null) surface.minRegionArea = (float)args["minRegionArea"];
            if (args["buildHeightMesh"] != null) surface.buildHeightMesh = (bool)args["buildHeightMesh"];
            if (args["generateLinks"] != null) SetSerializedBool(surface, "m_GenerateLinks", (bool)args["generateLinks"]);

            EditorUtility.SetDirty(surface);
            PrefabUtility.RecordPrefabInstancePropertyModifications(surface);
            EditorSceneManager.MarkSceneDirty(go.scene);

            return new OpResult
            {
                Ok = true,
                InstanceID = surface.GetInstanceID(),
                Summary = $"configured NavMeshSurface on '{go.name}'",
                Data = SurfaceJson(surface),
            };
        }

        public static OpResult AddModifier(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            if (!TryResolveSceneGameObject(id, out var go, out var error)) return OpResult.Fail(error);
            var useVolume = (bool?)args["volume"] ?? false;
            var area = 0;
            if (args["area"] != null && !NavMeshAuthor.TryParseAreaIndex(args["area"], out area, out error)) return OpResult.Fail($"area: {error}");
            if (!TryParseAffectedAgents(args["affectedAgentTypeIDs"], out var affectedAgents, out error)) return OpResult.Fail(error);

            Vector3? center = null;
            Vector3? size = null;
            if (useVolume)
            {
                if (args["center"] != null)
                {
                    if (!TryParseVector3(args["center"], out var parsed, out error)) return OpResult.Fail($"center: {error}");
                    center = parsed;
                }
                if (args["size"] != null)
                {
                    if (!TryParseVector3(args["size"], out var parsed, out error) || parsed.x <= 0f || parsed.y <= 0f || parsed.z <= 0f)
                        return OpResult.Fail("size must contain three positive finite values");
                    size = parsed;
                }
            }

            Component component;
            JObject data;
            if (useVolume)
            {
                var modifier = go.GetComponent<NavMeshModifierVolume>();
                if (modifier == null) modifier = Undo.AddComponent<NavMeshModifierVolume>(go);
                else Undo.RecordObject(modifier, "Configure NavMeshModifierVolume");
                if (args["area"] != null) modifier.area = area;
                if (center.HasValue) modifier.center = center.Value;
                if (size.HasValue) modifier.size = size.Value;
                if (affectedAgents != null) SetAffectedAgents(modifier, affectedAgents);
                component = modifier;
                data = new JObject
                {
                    ["kind"] = "volume",
                    ["instanceId"] = modifier.GetInstanceID(),
                    ["hostInstanceId"] = go.GetInstanceID(),
                    ["area"] = modifier.area,
                    ["areaName"] = AreaName(modifier.area),
                    ["center"] = JsonHelpers.Vec3(modifier.center),
                    ["size"] = JsonHelpers.Vec3(modifier.size),
                    ["affectedAgentTypeIDs"] = ReadAffectedAgents(modifier),
                };
            }
            else
            {
                var modifier = go.GetComponent<NavMeshModifier>();
                if (modifier == null) modifier = Undo.AddComponent<NavMeshModifier>(go);
                else Undo.RecordObject(modifier, "Configure NavMeshModifier");
                if (args["overrideArea"] != null) modifier.overrideArea = (bool)args["overrideArea"];
                if (args["area"] != null) { modifier.area = area; modifier.overrideArea = true; }
                if (args["ignoreFromBuild"] != null) modifier.ignoreFromBuild = (bool)args["ignoreFromBuild"];
                if (args["applyToChildren"] != null) modifier.applyToChildren = (bool)args["applyToChildren"];
                if (args["overrideGenerateLinks"] != null) modifier.overrideGenerateLinks = (bool)args["overrideGenerateLinks"];
                if (args["generateLinks"] != null) modifier.generateLinks = (bool)args["generateLinks"];
                if (affectedAgents != null) SetAffectedAgents(modifier, affectedAgents);
                component = modifier;
                data = new JObject
                {
                    ["kind"] = "hierarchy",
                    ["instanceId"] = modifier.GetInstanceID(),
                    ["hostInstanceId"] = go.GetInstanceID(),
                    ["overrideArea"] = modifier.overrideArea,
                    ["area"] = modifier.area,
                    ["areaName"] = AreaName(modifier.area),
                    ["ignoreFromBuild"] = modifier.ignoreFromBuild,
                    ["applyToChildren"] = modifier.applyToChildren,
                    ["overrideGenerateLinks"] = modifier.overrideGenerateLinks,
                    ["generateLinks"] = modifier.generateLinks,
                    ["affectedAgentTypeIDs"] = ReadAffectedAgents(modifier),
                };
            }

            EditorUtility.SetDirty(component);
            PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            EditorSceneManager.MarkSceneDirty(go.scene);
            return new OpResult { Ok = true, InstanceID = component.GetInstanceID(), Summary = $"configured {component.GetType().Name} on '{go.name}'", Data = data };
        }

        public static OpResult AddLink(JObject args)
        {
            var id = (int?)args["instanceId"] ?? 0;
            if (!TryResolveSceneGameObject(id, out var go, out var error)) return OpResult.Fail(error);
            var existing = go.GetComponent<NavMeshLink>();
            var agentTypeId = (int?)args["agentTypeID"] ?? existing?.agentTypeID ?? 0;
            if (!TryValidateAgentType(agentTypeId, out error)) return OpResult.Fail(error);
            var area = existing?.area ?? 0;
            if (args["area"] != null && !NavMeshAuthor.TryParseAreaIndex(args["area"], out area, out error)) return OpResult.Fail($"area: {error}");

            Vector3? startPoint = null;
            Vector3? endPoint = null;
            if (args["startPoint"] != null)
            {
                if (!TryParseVector3(args["startPoint"], out var parsed, out error)) return OpResult.Fail($"startPoint: {error}");
                startPoint = parsed;
            }
            if (args["endPoint"] != null)
            {
                if (!TryParseVector3(args["endPoint"], out var parsed, out error)) return OpResult.Fail($"endPoint: {error}");
                endPoint = parsed;
            }
            Transform startTransform = null;
            Transform endTransform = null;
            if (args["startInstanceId"] != null)
            {
                if (!TryResolveSceneGameObject((int)args["startInstanceId"], out var start, out error)) return OpResult.Fail($"start: {error}");
                startTransform = start.transform;
            }
            if (args["endInstanceId"] != null)
            {
                if (!TryResolveSceneGameObject((int)args["endInstanceId"], out var end, out error)) return OpResult.Fail($"end: {error}");
                endTransform = end.transform;
            }
            if (args["width"] != null && (!TryReadNonNegative(args["width"], out _, out error))) return OpResult.Fail($"width: {error}");
            if (args["costModifier"] != null && (!TryReadFinite(args["costModifier"], out _, out error))) return OpResult.Fail($"costModifier: {error}");

            var link = existing;
            if (link == null) link = Undo.AddComponent<NavMeshLink>(go);
            else Undo.RecordObject(link, "Configure NavMeshLink");
            link.agentTypeID = agentTypeId;
            if (startPoint.HasValue) link.startPoint = startPoint.Value;
            if (endPoint.HasValue) link.endPoint = endPoint.Value;
            if (args["startInstanceId"] != null) link.startTransform = startTransform;
            if (args["endInstanceId"] != null) link.endTransform = endTransform;
            if (args["width"] != null) link.width = (float)args["width"];
            if (args["area"] != null) link.area = area;
            if (args["bidirectional"] != null) link.bidirectional = (bool)args["bidirectional"];
            if (args["autoUpdate"] != null) link.autoUpdate = (bool)args["autoUpdate"];
            if (args["costModifier"] != null) link.costModifier = (float)args["costModifier"];
            if (args["activated"] != null) link.activated = (bool)args["activated"];
            link.UpdateLink();

            EditorUtility.SetDirty(link);
            PrefabUtility.RecordPrefabInstancePropertyModifications(link);
            EditorSceneManager.MarkSceneDirty(go.scene);
            var data = new JObject
            {
                ["instanceId"] = link.GetInstanceID(),
                ["hostInstanceId"] = go.GetInstanceID(),
                ["startPoint"] = JsonHelpers.Vec3(link.startPoint),
                ["endPoint"] = JsonHelpers.Vec3(link.endPoint),
                ["width"] = link.width,
                ["agentTypeID"] = link.agentTypeID,
                ["area"] = link.area,
                ["areaName"] = AreaName(link.area),
                ["bidirectional"] = link.bidirectional,
                ["autoUpdate"] = link.autoUpdate,
                ["costModifier"] = link.costModifier,
                ["activated"] = link.activated,
            };
            if (link.startTransform != null) data["startInstanceId"] = link.startTransform.gameObject.GetInstanceID();
            if (link.endTransform != null) data["endInstanceId"] = link.endTransform.gameObject.GetInstanceID();
            return new OpResult { Ok = true, InstanceID = link.GetInstanceID(), Summary = $"configured NavMeshLink on '{go.name}'", Data = data };
        }

        private static IEnumerable<NavMeshSurface> FindLoadedSurfaces() =>
            UnityEngine.Object.FindObjectsByType<NavMeshSurface>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(surface => surface != null && !EditorUtility.IsPersistent(surface) && surface.gameObject.scene.IsValid() && surface.gameObject.scene.isLoaded)
                .OrderBy(surface => surface.gameObject.scene.path, StringComparer.Ordinal)
                .ThenBy(surface => surface.gameObject.name, StringComparer.Ordinal)
                .ThenBy(surface => surface.GetInstanceID());

        private sealed class BakeSnapshot
        {
            public readonly NavMeshSurface Surface;
            public readonly NavMeshData PreviousData;
            public readonly string PreviousPath;
            public NavMeshData NewData;
            public string NewPath;
            public bool CreatedAsset;

            public BakeSnapshot(NavMeshSurface surface)
            {
                Surface = surface;
                PreviousData = surface.navMeshData;
                PreviousPath = PreviousData != null ? AssetDatabase.GetAssetPath(PreviousData) : null;
            }
        }

        private static bool TryBakeAndPersist(BakeSnapshot snapshot, out string error)
        {
            error = null;
            try
            {
                Undo.RecordObject(snapshot.Surface, "Bake NavMeshSurface");
                snapshot.Surface.BuildNavMesh();
                snapshot.NewData = snapshot.Surface.navMeshData;
                if (snapshot.NewData == null)
                {
                    error = $"bake produced no NavMeshData for '{snapshot.Surface.gameObject.name}'";
                    return false;
                }

                if (AssetDatabase.Contains(snapshot.NewData))
                {
                    snapshot.NewPath = AssetDatabase.GetAssetPath(snapshot.NewData);
                }
                else
                {
                    var targetPath = GetAndEnsureTargetPath(snapshot.Surface);
                    var basePath = $"{targetPath}/NavMesh-{SanitizeAssetName(snapshot.Surface.gameObject.name)}.asset";
                    snapshot.NewPath = AssetDatabase.GenerateUniqueAssetPath(basePath);
                    AssetDatabase.CreateAsset(snapshot.NewData, snapshot.NewPath);
                    snapshot.CreatedAsset = true;
                }

                EditorUtility.SetDirty(snapshot.Surface);
                PrefabUtility.RecordPrefabInstancePropertyModifications(snapshot.Surface);
                EditorSceneManager.MarkSceneDirty(snapshot.Surface.gameObject.scene);
                return true;
            }
            catch (Exception ex)
            {
                error = $"failed to bake or persist '{snapshot.Surface.gameObject.name}': {ex.Message}";
                return false;
            }
        }

        private static void RollbackBakes(IEnumerable<BakeSnapshot> snapshots)
        {
            foreach (var snapshot in snapshots.Reverse())
            {
                if (snapshot.Surface == null) continue;
                try
                {
                    snapshot.Surface.RemoveData();
                    snapshot.Surface.navMeshData = snapshot.PreviousData;
                    if (snapshot.PreviousData != null && snapshot.Surface.isActiveAndEnabled)
                        snapshot.Surface.AddData();
                    EditorUtility.SetDirty(snapshot.Surface);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(snapshot.Surface);
                    EditorSceneManager.MarkSceneDirty(snapshot.Surface.gameObject.scene);
                }
                catch
                {
                    // Continue best-effort cleanup for the remaining surfaces and assets.
                }

                if (snapshot.CreatedAsset && !string.IsNullOrEmpty(snapshot.NewPath))
                {
                    AssetDatabase.DeleteAsset(snapshot.NewPath);
                }
                else if (snapshot.NewData != null && snapshot.NewData != snapshot.PreviousData && !AssetDatabase.Contains(snapshot.NewData))
                {
                    UnityEngine.Object.DestroyImmediate(snapshot.NewData);
                }
            }
        }


        private static string GetAndEnsureTargetPath(NavMeshSurface surface)
        {
            var scenePath = surface.gameObject.scene.path;
            var targetPath = "Assets";
            if (!string.IsNullOrEmpty(scenePath))
            {
                targetPath = Path.Combine(Path.GetDirectoryName(scenePath) ?? "Assets", Path.GetFileNameWithoutExtension(scenePath)).Replace('\\', '/');
            }
            if (!Directory.Exists(targetPath)) Directory.CreateDirectory(targetPath);
            AssetDatabase.Refresh();
            return targetPath;
        }

        private static string SanitizeAssetName(string name)
        {
            var builder = new StringBuilder(name ?? "Surface");
            var forbidden = Path.GetInvalidFileNameChars();
            for (var i = 0; i < builder.Length; i++)
                if (forbidden.Contains(builder[i])) builder[i] = '_';
            return builder.ToString();
        }

        private static JObject SurfaceJson(NavMeshSurface surface)
        {
            var data = new JObject
            {
                ["instanceId"] = surface.gameObject.GetInstanceID(),
                ["surfaceInstanceId"] = surface.GetInstanceID(),
                ["name"] = surface.gameObject.name,
                ["agentTypeID"] = surface.agentTypeID,
                ["agentTypeName"] = UnityEngine.AI.NavMesh.GetSettingsNameFromID(surface.agentTypeID),
                ["collectObjects"] = surface.collectObjects.ToString(),
                ["useGeometry"] = surface.useGeometry.ToString(),
                ["layerMask"] = surface.layerMask.value,
                ["defaultArea"] = surface.defaultArea,
                ["defaultAreaName"] = AreaName(surface.defaultArea),
                ["size"] = JsonHelpers.Vec3(surface.size),
                ["center"] = JsonHelpers.Vec3(surface.center),
                ["overrideVoxelSize"] = surface.overrideVoxelSize,
                ["voxelSize"] = surface.voxelSize,
                ["overrideTileSize"] = surface.overrideTileSize,
                ["tileSize"] = surface.tileSize,
                ["minRegionArea"] = surface.minRegionArea,
                ["buildHeightMesh"] = surface.buildHeightMesh,
                ["baked"] = surface.navMeshData != null,
                ["active"] = surface.isActiveAndEnabled,
            };
            if (surface.navMeshData != null) data["assetPath"] = AssetDatabase.GetAssetPath(surface.navMeshData);
            return data;
        }

        private static bool TryResolveSurface(int id, out NavMeshSurface surface, out string error)
        {
            var target = EditorUtility.InstanceIDToObject(id);
            surface = target as NavMeshSurface;
            if (surface == null)
            {
                var go = target as GameObject ?? (target as Component)?.gameObject;
                surface = go != null ? go.GetComponent<NavMeshSurface>() : null;
            }
            if (surface == null) { error = $"NavMeshSurface not found: instanceId={id}"; return false; }
            if (!TryResolveSceneGameObject(surface.gameObject.GetInstanceID(), out _, out error)) return false;
            return true;
        }

        private static bool TryResolveSceneGameObject(int id, out GameObject go, out string error)
        {
            var target = EditorUtility.InstanceIDToObject(id);
            go = target as GameObject ?? (target as Component)?.gameObject;
            if (go == null) { error = $"GameObject not found: instanceId={id}"; return false; }
            if (EditorUtility.IsPersistent(go) || PrefabUtility.IsPartOfPrefabAsset(go))
            {
                error = "persistent prefab assets are not scene-edit targets; instantiate the prefab or open Prefab Mode";
                return false;
            }
            if (!go.scene.IsValid() || !go.scene.isLoaded) { error = "target must belong to a loaded scene or Prefab Stage"; return false; }
            error = null;
            return true;
        }

        private static bool TryValidateAgentType(int id, out string error)
        {
            if (UnityEngine.AI.NavMesh.GetSettingsByID(id).agentTypeID == -1)
            {
                error = $"unknown NavMesh agentTypeID {id}";
                return false;
            }
            error = null;
            return true;
        }

        private static bool TryParseLayerMask(JToken token, out int mask, out string error)
        {
            mask = ~0;
            error = null;
            if (token.Type == JTokenType.Integer)
            {
                var raw = token.Value<long>();
                if (raw < int.MinValue || raw > int.MaxValue) { error = "raw mask is outside Int32 range"; return false; }
                mask = (int)raw;
                return true;
            }
            var names = token.Type == JTokenType.String
                ? new[] { (string)token }
                : token is JArray array && array.Count > 0 && array.All(item => item.Type == JTokenType.String)
                    ? array.Values<string>().ToArray()
                    : null;
            if (names == null) { error = "expected an Int32 mask, layer name, or non-empty array of names"; return false; }
            mask = 0;
            foreach (var name in names)
            {
                var layer = LayerMask.NameToLayer(name);
                if (layer < 0) { error = $"unknown Unity layer '{name}'"; return false; }
                mask |= 1 << layer;
            }
            return true;
        }

        private static bool TryParseAffectedAgents(JToken token, out int[] values, out string error)
        {
            values = null;
            error = null;
            if (token == null) return true;
            if (!(token is JArray array)) { error = "affectedAgentTypeIDs must be an array"; return false; }
            values = array.Values<int>().Distinct().ToArray();
            if (values.Contains(-1) && values.Length != 1) { error = "-1 (All) cannot be combined with explicit agent type IDs"; return false; }
            foreach (var id in values)
                if (id != -1 && !TryValidateAgentType(id, out error)) return false;
            return true;
        }

        private static void SetAffectedAgents(Component component, IReadOnlyList<int> values)
        {
            var serialized = new SerializedObject(component);
            var property = serialized.FindProperty("m_AffectedAgents");
            property.arraySize = values.Count;
            for (var i = 0; i < values.Count; i++) property.GetArrayElementAtIndex(i).intValue = values[i];
            serialized.ApplyModifiedProperties();
        }

        private static JArray ReadAffectedAgents(Component component)
        {
            var serialized = new SerializedObject(component);
            var property = serialized.FindProperty("m_AffectedAgents");
            var result = new JArray();
            for (var i = 0; i < property.arraySize; i++) result.Add(property.GetArrayElementAtIndex(i).intValue);
            return result;
        }

        private static void SetSerializedBool(UnityEngine.Object target, string propertyName, bool value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(propertyName).boolValue = value;
            serialized.ApplyModifiedProperties();
        }

        private static bool TryParseVector3(JToken token, out Vector3 value, out string error)
        {
            value = default;
            error = null;
            if (token is JArray array && array.Count == 3 && array.All(IsNumber))
                value = new Vector3((float)array[0], (float)array[1], (float)array[2]);
            else if (token is JObject obj && obj["x"] != null && obj["y"] != null && obj["z"] != null && IsNumber(obj["x"]) && IsNumber(obj["y"]) && IsNumber(obj["z"]))
                value = new Vector3((float)obj["x"], (float)obj["y"], (float)obj["z"]);
            else { error = "expected [x,y,z] or {x,y,z}"; return false; }
            if (!IsFinite(value.x) || !IsFinite(value.y) || !IsFinite(value.z)) { error = "coordinates must be finite"; return false; }
            return true;
        }

        private static bool TryReadPositive(JToken token, out float value, out string error)
        {
            if (!TryReadFinite(token, out value, out error)) return false;
            if (value <= 0f) { error = "must be greater than zero"; return false; }
            return true;
        }

        private static bool TryReadNonNegative(JToken token, out float value, out string error)
        {
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

        private static string AreaName(int index)
        {
            foreach (var name in UnityEngine.AI.NavMesh.GetAreaNames())
                if (UnityEngine.AI.NavMesh.GetAreaFromName(name) == index) return name;
            return $"#{index}";
        }

        private static bool IsNumber(JToken token) => token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float);
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
