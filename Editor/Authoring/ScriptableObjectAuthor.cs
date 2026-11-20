using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Maicon.SceneAnalyzer.Editor.AssetTools;
using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Maicon.SceneAnalyzer.Editor.Authoring
{
    /// ScriptableObject discovery + authoring (create, read, set, duplicate)
    /// + a generic `modify_array_property` helper that works for both SO assets
    /// AND Component instances on scene GameObjects.
    public static class ScriptableObjectAuthor
    {
        // ============== Discovery ==============

        public static OpResult ListTypes(JObject args)
        {
            var nameContains = (string)args["nameContains"];
            var category = ((string)args["category"] ?? "all").ToLowerInvariant();
            if (category != "all" && category != "builtin" && category != "user")
                return OpResult.Fail("category must be all, builtin, or user");
            var withMenuOnly = (bool?)args["withMenuOnly"] ?? false;
            var offset = Math.Max(0, (int?)args["offset"] ?? 0);
            var limit = Math.Max(1, Math.Min(1000, (int?)args["limit"] ?? 200));
            var editorWindow = typeof(EditorWindow);

            var matches = TypeCache.GetTypesDerivedFrom<ScriptableObject>()
                .Where(type => type != null && !type.IsAbstract && !type.IsGenericType && !editorWindow.IsAssignableFrom(type))
                .Select(type => new
                {
                    Type = type,
                    Assembly = type.Assembly.GetName().Name ?? string.Empty,
                    Menu = type.GetCustomAttribute<CreateAssetMenuAttribute>(),
                })
                .Where(entry => category == "all"
                    || (category == "builtin") == ComponentTypeInspector.IsBuiltinAssembly(entry.Assembly))
                .Where(entry => string.IsNullOrEmpty(nameContains)
                    || entry.Type.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0
                    || (entry.Type.FullName ?? string.Empty).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(entry => !withMenuOnly || entry.Menu != null)
                .OrderBy(entry => entry.Type.FullName, StringComparer.Ordinal)
                .ThenBy(entry => entry.Assembly, StringComparer.Ordinal)
                .ToArray();

            var page = new JArray(matches.Skip(offset).Take(limit).Select(entry => new JObject
            {
                ["name"] = entry.Type.Name,
                ["fullName"] = entry.Type.FullName,
                ["assembly"] = entry.Assembly,
                ["assemblyQualifiedName"] = entry.Type.AssemblyQualifiedName,
                ["builtin"] = ComponentTypeInspector.IsBuiltinAssembly(entry.Assembly),
                ["menuName"] = entry.Menu != null ? entry.Menu.menuName : null,
                ["defaultFileName"] = entry.Menu != null ? entry.Menu.fileName : null,
            }));
            return new OpResult
            {
                Ok = true,
                Summary = $"returned {page.Count} of {matches.Length} ScriptableObject type(s)",
                Data = PageData("types", page, matches.Length, offset),
            };
        }

        // ============== Find instances by subclass ==============

        public static OpResult FindInstances(JObject args)
        {
            var typeName = (string)args["className"];
            if (!TryResolveScriptableObjectType(typeName, false, out var type, out var typeError)) return OpResult.Fail(typeError);
            var searchPath = (string)args["pathPrefix"] ?? "Assets";
            if (!TryAssetPath(searchPath, false, true, out searchPath, out var pathError)) return OpResult.Fail(pathError);
            if (!AssetDatabase.IsValidFolder(searchPath)) return OpResult.Fail($"search folder not found: {searchPath}");
            var offset = Math.Max(0, (int?)args["offset"] ?? 0);
            var limit = Math.Max(1, Math.Min(1000, (int?)args["limit"] ?? 200));

            var searchableTypes = TypeCache.GetTypesDerivedFrom<ScriptableObject>()
                .Where(candidate => candidate != null && !candidate.IsAbstract && !candidate.IsGenericType && type.IsAssignableFrom(candidate))
                .Concat(!type.IsAbstract && !type.IsGenericType ? new[] { type } : Array.Empty<Type>())
                .Distinct()
                .ToArray();
            var matches = searchableTypes
                .SelectMany(candidate => AssetDatabase.FindAssets($"t:{candidate.Name}", new[] { searchPath }))
                .Distinct(StringComparer.Ordinal)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .Select(path => new { Path = path, Type = AssetDatabase.GetMainAssetTypeAtPath(path) })
                .Where(entry => entry.Type != null && type.IsAssignableFrom(entry.Type))
                .OrderBy(entry => entry.Path, StringComparer.Ordinal)
                .ToArray();
            var page = new JArray(matches.Skip(offset).Take(limit).Select(entry => AssetData(entry.Path)));
            var data = PageData("assets", page, matches.Length, offset);
            data["type"] = type.FullName;
            data["searchPath"] = searchPath;
            return new OpResult
            {
                Ok = true,
                Summary = $"returned {page.Count} of {matches.Length} instance(s) of {type.FullName}",
                Data = data,
            };
        }

        // ============== Create ==============

        public static OpResult Create(JObject args)
        {
            var typeName = (string)args["className"];
            var requestedPath = (string)args["path"];
            if (string.IsNullOrEmpty(requestedPath)) return OpResult.Fail("path is required (e.g. Assets/Data/MyAsset.asset)");
            if (!TryAssetPath(requestedPath, false, false, out var path, out var pathError, requireAssetExtension: true)) return OpResult.Fail(pathError);
            if (path.StartsWith("Assets/StreamingAssets/", StringComparison.Ordinal) || path == "Assets/StreamingAssets")
                return OpResult.Fail("ScriptableObject assets cannot be created under Assets/StreamingAssets");
            if (AssetDatabase.AssetPathExists(path)) return OpResult.Fail($"asset already exists: {path}");
            if (!TryResolveScriptableObjectType(typeName, true, out var type, out var typeError))
                return OpResult.Fail(typeError + " If the script was just created, call refresh_assets first.");

            ScriptableObject scriptableObject = null;
            var createdFolders = new List<string>();
            try
            {
                scriptableObject = ScriptableObject.CreateInstance(type);
                if (scriptableObject == null) return OpResult.Fail($"CreateInstance returned null for {type.FullName}");
                scriptableObject.name = Path.GetFileNameWithoutExtension(path);
                var applied = ApplyInitialFields(scriptableObject, args["initialFields"] as JObject, out var fieldError);
                if (fieldError != null) return OpResult.Fail(fieldError);
                EnsureFolder(Path.GetDirectoryName(path)?.Replace('\\', '/'), createdFolders);
                AssetDatabase.CreateAsset(scriptableObject, path);
                if (!AssetDatabase.AssetPathExists(path)) throw new InvalidOperationException($"CreateAsset did not create '{path}'");
                var result = OpResult.Success(scriptableObject.GetMcpInstanceId(),
                    $"created {type.Name} at {path}" + (applied > 0 ? $" (set {applied} initial field(s))" : string.Empty));
                result.Data = new JObject { ["asset"] = AssetData(path), ["savedAssets"] = true };
                result.CreatedAssetPath = path;
                scriptableObject = null;
                return result;
            }
            catch (Exception exception)
            {
                if (AssetDatabase.AssetPathExists(path)) AssetDatabase.DeleteAsset(path);
                CleanupEmptyFolders(createdFolders);
                return OpResult.Fail($"CREATE_FAILED: {exception.Message}");
            }
            finally
            {
                if (scriptableObject != null) Object.DestroyImmediate(scriptableObject);
            }
        }

        // ============== Read ==============

        public static OpResult Read(JObject args)
        {
            if (!TryResolveScriptableObject((string)args["guidOrPath"], out var path, out var scriptableObject, out var error)) return OpResult.Fail(error);
            var offset = Math.Max(0, (int?)args["offset"] ?? 0);
            var limit = Math.Max(1, Math.Min(1000, (int?)args["limit"] ?? 80));
            var readOptions = new SerializedValueCodec.ReadOptions
            {
                MaxDepth = Math.Max(1, Math.Min(16, (int?)args["maxDepth"] ?? 4)),
                MaxArrayItems = Math.Max(1, Math.Min(1000, (int?)args["maxArrayItems"] ?? 32)),
            };
            var fields = new JArray();
            var serialized = new SerializedObject(scriptableObject);
            var iterator = serialized.GetIterator();
            bool enterChildren = true;
            int total = 0;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.name == "m_Script") continue;
                if (total >= offset && fields.Count < limit)
                {
                    fields.Add(new JObject
                    {
                        ["name"] = iterator.name,
                        ["propertyPath"] = iterator.propertyPath,
                        ["propertyType"] = iterator.propertyType.ToString(),
                        ["unityType"] = iterator.type,
                        ["isArray"] = iterator.isArray,
                        ["arraySize"] = iterator.isArray ? iterator.arraySize : (int?)null,
                        ["value"] = SerializedValueCodec.Read(iterator, readOptions),
                    });
                }
                total++;
            }
            var data = PageData("fields", fields, total, offset);
            data["asset"] = AssetData(path);
            return new OpResult
            {
                Ok = true,
                InstanceID = scriptableObject.GetMcpInstanceId(),
                Summary = $"read {fields.Count} of {total} field(s) from {scriptableObject.name}",
                Data = data,
            };
        }

        // ============== Set property ==============

        public static OpResult SetProperty(JObject args)
        {
            if (!TryResolveScriptableObject((string)args["assetGuidOrPath"], out var path, out var asset, out var error)) return OpResult.Fail(error);
            if (!AssetDatabase.IsOpenForEdit(path, out var editMessage, StatusQueryOptions.ForceUpdate))
                return OpResult.Fail($"ASSET_NOT_EDITABLE: {editMessage}");
            var propertyPath = (string)args["propertyPath"];
            if (string.IsNullOrEmpty(propertyPath)) return OpResult.Fail("propertyPath is required");

            var serialized = new SerializedObject(asset);
            var property = serialized.FindProperty(propertyPath);
            if (property == null) return OpResult.Fail($"property '{propertyPath}' not found on {asset.GetType().Name}");
            var before = SerializedValueCodec.Read(property, CompleteReadOptions());
            using (var mutation = new MutationScope($"Set {asset.name}.{propertyPath}"))
            {
                mutation.Record(asset, $"Set {asset.name}.{propertyPath}");
                try
                {
                    ValidateAssignedValue(property, args["value"], false);
                    ComponentEditor.ApplyValue(property, args["value"]);
                    var after = SerializedValueCodec.Read(property, CompleteReadOptions());
                    serialized.ApplyModifiedProperties();
                    EditorUtility.SetDirty(asset);
                    var saved = ChangesetExecutor.SaveAssetIfDirty(asset);
                    var result = new OpResult
                    {
                        Ok = true,
                        InstanceID = asset.GetMcpInstanceId(),
                        Summary = $"set {asset.GetType().Name}.{propertyPath}",
                        Data = new JObject
                        {
                            ["asset"] = AssetData(path),
                            ["propertyPath"] = propertyPath,
                            ["before"] = before,
                            ["after"] = after,
                            ["savedAssets"] = saved,
                        },
                    };
                    mutation.Complete();
                    return result;
                }
                catch (Exception exception)
                {
                    serialized.Update();
                    return OpResult.Fail($"SET_FAILED: {exception.Message}");
                }
            }
        }

        // ============== Duplicate ==============

        public static OpResult Duplicate(JObject args)
        {
            if (!TryResolveScriptableObject((string)args["sourceGuidOrPath"], out var sourcePath, out var source, out var sourceError)) return OpResult.Fail(sourceError);
            var requestedTarget = (string)args["targetPath"];
            if (string.IsNullOrEmpty(requestedTarget)) return OpResult.Fail("targetPath is required");
            if (!TryAssetPath(requestedTarget, false, false, out var targetPath, out var pathError, requireAssetExtension: true)) return OpResult.Fail(pathError);
            if (targetPath.StartsWith("Assets/StreamingAssets/", StringComparison.Ordinal) || targetPath == "Assets/StreamingAssets")
                return OpResult.Fail("ScriptableObject assets cannot be created under Assets/StreamingAssets");
            if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase)) return OpResult.Fail("source and target paths must differ");
            if (AssetDatabase.AssetPathExists(targetPath)) return OpResult.Fail($"asset already exists: {targetPath}");

            var createdFolders = new List<string>();
            try
            {
                EnsureFolder(Path.GetDirectoryName(targetPath)?.Replace('\\', '/'), createdFolders);
                if (!AssetDatabase.CopyAsset(sourcePath, targetPath)) throw new InvalidOperationException($"CopyAsset returned false: {sourcePath} -> {targetPath}");
                var clone = AssetDatabase.LoadAssetAtPath<ScriptableObject>(targetPath);
                if (clone == null) throw new InvalidOperationException($"copy succeeded but clone is not a ScriptableObject at {targetPath}");
                var result = OpResult.Success(clone.GetMcpInstanceId(), $"duplicated {source.GetType().Name} '{source.name}' -> {targetPath}");
                result.Data = new JObject { ["asset"] = AssetData(targetPath), ["savedAssets"] = true };
                result.CreatedAssetPath = targetPath;
                return result;
            }
            catch (Exception exception)
            {
                if (AssetDatabase.AssetPathExists(targetPath)) AssetDatabase.DeleteAsset(targetPath);
                CleanupEmptyFolders(createdFolders);
                return OpResult.Fail($"DUPLICATE_FAILED: {exception.Message}");
            }
        }

        // ============== Modify array property (works on Component OR SO target) ==============

        public static OpResult ModifyArrayProperty(JObject args)
        {
            var serialized = ResolveSerializedObject(args, out var assetPath, out var error);
            if (serialized == null) return OpResult.Fail(error);
            if (!string.IsNullOrEmpty(assetPath)
                && !AssetDatabase.IsOpenForEdit(assetPath, out var editMessage, StatusQueryOptions.ForceUpdate))
                return OpResult.Fail($"ASSET_NOT_EDITABLE: {editMessage}");

            var propertyPath = (string)args["propertyPath"];
            if (string.IsNullOrEmpty(propertyPath)) return OpResult.Fail("propertyPath is required");
            var property = serialized.FindProperty(propertyPath);
            if (property == null) return OpResult.Fail($"property '{propertyPath}' not found");
            if (!property.isArray) return OpResult.Fail($"'{propertyPath}' is not an array/list (it's {property.propertyType})");

            var action = ((string)args["action"] ?? "append").ToLowerInvariant();
            var index = (int?)args["index"] ?? -1;
            var size = (int?)args["size"] ?? -1;
            var value = args["value"];
            if ((action == "append" || action == "insert" || action == "set") && value == null)
                return OpResult.Fail($"value is required for {action}");
            if (action == "insert" && (index < 0 || index > property.arraySize))
                return OpResult.Fail($"index {index} out of range [0..{property.arraySize}]");
            if ((action == "remove" || action == "set") && (index < 0 || index >= property.arraySize))
                return OpResult.Fail($"index {index} out of range [0..{property.arraySize - 1}]");
            if (action == "resize" && size < 0) return OpResult.Fail("size is required for resize and must be >= 0");
            if (action == "resize" && size > property.arraySize && value == null)
                return OpResult.Fail("value is required when resize expands an array because Unity leaves inserted values undefined");
            if (action != "append" && action != "insert" && action != "remove"
                && action != "resize" && action != "clear" && action != "set")
                return OpResult.Fail($"unknown action '{action}' (use append/insert/remove/resize/clear/set)");

            var before = SerializedValueCodec.Read(property, CompleteReadOptions());
            using (var mutation = new MutationScope($"Array {action}"))
            {
                mutation.Record(serialized.targetObject, $"Array {action}");
                try
                {
                    string summary;
                    switch (action)
                    {
                        case "append":
                            index = property.arraySize;
                            property.InsertArrayElementAtIndex(index);
                            var appended = property.GetArrayElementAtIndex(index);
                            ValidateCompleteValue(appended, value);
                            ComponentEditor.ApplyValue(appended, value);
                            summary = $"appended at [{index}]";
                            break;
                        case "insert":
                            property.InsertArrayElementAtIndex(index);
                            var inserted = property.GetArrayElementAtIndex(index);
                            ValidateCompleteValue(inserted, value);
                            ComponentEditor.ApplyValue(inserted, value);
                            summary = $"inserted at [{index}]";
                            break;
                        case "remove":
                            DeleteArrayElement(property, index);
                            summary = $"removed at [{index}]";
                            break;
                        case "resize":
                            while (property.arraySize > size) DeleteArrayElement(property, property.arraySize - 1);
                            while (property.arraySize < size)
                            {
                                var next = property.arraySize;
                                property.InsertArrayElementAtIndex(next);
                                var expanded = property.GetArrayElementAtIndex(next);
                                ValidateCompleteValue(expanded, value);
                                ComponentEditor.ApplyValue(expanded, value);
                            }
                            summary = $"resized to {size}";
                            break;
                        case "clear":
                            property.ClearArray();
                            summary = "cleared";
                            break;
                        default:
                            var element = property.GetArrayElementAtIndex(index);
                            ValidateAssignedValue(element, value, true);
                            ComponentEditor.ApplyValue(element, value);
                            summary = $"set [{index}]";
                            break;
                    }

                    var after = SerializedValueCodec.Read(property, CompleteReadOptions());
                    serialized.ApplyModifiedProperties();
                    EditorUtility.SetDirty(serialized.targetObject);
                    var saved = !string.IsNullOrEmpty(assetPath) && ChangesetExecutor.SaveAssetIfDirty(serialized.targetObject);
                    var result = new OpResult
                    {
                        Ok = true,
                        InstanceID = serialized.targetObject.GetMcpInstanceId(),
                        Summary = summary + $" (new size {property.arraySize})",
                        Data = new JObject
                        {
                            ["target"] = ToolTargetRef.Capture(serialized.targetObject)?.ToJson(),
                            ["assetPath"] = string.IsNullOrEmpty(assetPath) ? null : assetPath,
                            ["propertyPath"] = propertyPath,
                            ["action"] = action,
                            ["before"] = before,
                            ["after"] = after,
                            ["arraySize"] = property.arraySize,
                            ["savedAssets"] = saved,
                        },
                    };
                    mutation.Complete();
                    return result;
                }
                catch (Exception exception)
                {
                    serialized.Update();
                    return OpResult.Fail($"ARRAY_MUTATION_FAILED: {exception.Message}");
                }
            }
        }

        // ============== Helpers ==============

        private static SerializedObject ResolveSerializedObject(JObject args, out string assetPath, out string error)
        {
            assetPath = null;
            error = null;
            var assetRef = (string)args["assetGuidOrPath"];
            var hasAsset = !string.IsNullOrEmpty(assetRef);
            var hasTarget = args["target"] != null;
            var hasInstanceId = ((int?)args["instanceId"] ?? 0) != 0;
            if (hasTarget && hasInstanceId)
            {
                error = "provide target or instanceId, not both";
                return null;
            }
            var hasComponent = hasTarget || hasInstanceId;
            if (hasAsset == hasComponent)
            {
                error = "provide exactly one target: assetGuidOrPath or target/instanceId+componentType";
                return null;
            }
            if (hasAsset)
            {
                if (!TryResolveScriptableObject(assetRef, out assetPath, out var asset, out error)) return null;
                return new SerializedObject(asset);
            }
            var typeName = (string)args["componentType"];
            if (string.IsNullOrEmpty(typeName)) { error = "componentType is required when targeting a GameObject"; return null; }
            var component = ToolTargetResolver.ResolveComponent(args, typeName, (int?)args["componentOrdinal"] ?? 0, out error);
            return component != null ? new SerializedObject(component) : null;
        }

        public static Type ResolveScriptableObjectType(string name)
            => TryResolveScriptableObjectType(name, false, out var type, out _) ? type : null;

        private static bool TryResolveScriptableObjectType(string name, bool requireCreatable, out Type type, out string error)
        {
            type = null;
            error = null;
            if (string.IsNullOrWhiteSpace(name)) { error = "className is required"; return false; }
            var exact = Type.GetType(name, false);
            if (IsScriptableObjectType(exact) && (!requireCreatable || IsCreatableScriptableObjectType(exact))) { type = exact; return true; }
            var candidates = TypeCache.GetTypesDerivedFrom<ScriptableObject>()
                .Where(candidate => IsScriptableObjectType(candidate) && (!requireCreatable || IsCreatableScriptableObjectType(candidate)))
                .Where(candidate => string.Equals(candidate.FullName, name, StringComparison.Ordinal)
                    || string.Equals(candidate.AssemblyQualifiedName, name, StringComparison.Ordinal)
                    || string.Equals(candidate.Name, name, StringComparison.Ordinal))
                .OrderBy(candidate => candidate.FullName, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Assembly.GetName().Name, StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length == 1) { type = candidates[0]; return true; }
            if (candidates.Length > 1)
            {
                error = $"ambiguous ScriptableObject type '{name}': {string.Join(", ", candidates.Select(candidate => candidate.AssemblyQualifiedName))}. Use fullName or assemblyQualifiedName.";
                return false;
            }
            error = requireCreatable
                ? $"unknown or non-creatable ScriptableObject type '{name}'. Use list_scriptable_object_types nameContains=\"{name}\"."
                : $"unknown ScriptableObject type '{name}'. Use list_scriptable_object_types nameContains=\"{name}\".";
            return false;
        }

        private static bool IsScriptableObjectType(Type type)
            => type != null && typeof(ScriptableObject).IsAssignableFrom(type) && !typeof(EditorWindow).IsAssignableFrom(type);

        private static bool IsCreatableScriptableObjectType(Type type)
            => IsScriptableObjectType(type) && !type.IsAbstract && !type.IsGenericType;

        private static bool TryResolveScriptableObject(string assetRef, out string path, out ScriptableObject asset, out string error)
        {
            path = null;
            asset = null;
            error = null;
            if (string.IsNullOrWhiteSpace(assetRef)) { error = "asset GUID or path is required"; return false; }
            var resolved = AssetToolSupport.ResolveGuidOrPath(assetRef);
            if (!AssetToolSupport.TryAssetPath(resolved, true, out path, out error)) return false;
            asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (asset == null) { error = $"asset is not a ScriptableObject: {path}"; return false; }
            return true;
        }

        private static bool TryAssetPath(string path, bool mustExist, bool allowRoot, out string normalized, out string error, bool requireAssetExtension = false)
        {
            if (!AssetToolSupport.TryAssetPath(path, mustExist, out normalized, out error, allowRoot)) return false;
            if (requireAssetExtension && !normalized.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                error = "ScriptableObject asset paths must end with .asset";
                return false;
            }
            return true;
        }

        private static int ApplyInitialFields(ScriptableObject scriptableObject, JObject fields, out string error)
        {
            error = null;
            if (fields == null || fields.Count == 0) return 0;
            var serialized = new SerializedObject(scriptableObject);
            foreach (var field in fields.Properties())
            {
                var property = serialized.FindProperty(field.Name);
                if (property == null) { error = $"initial field not found: {field.Name}"; serialized.Update(); return 0; }
                try
                {
                    ValidateAssignedValue(property, field.Value, false);
                    ComponentEditor.ApplyValue(property, field.Value);
                }
                catch (Exception exception) { error = $"invalid initialFields.{field.Name}: {exception.Message}"; serialized.Update(); return 0; }
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return fields.Count;
        }

        private static void EnsureFolder(string folder, List<string> createdFolders)
            => AssetToolSupport.EnsureFolder(folder, createdFolders);

        private static void CleanupEmptyFolders(List<string> createdFolders)
            => AssetToolSupport.CleanupEmptyFolders(createdFolders);

        private static JObject AssetData(string path) => AssetToolSupport.AssetJson(path);

        private static JObject PageData(string arrayName, JArray values, int total, int offset) => new JObject
        {
            ["total"] = total,
            ["returned"] = values.Count,
            ["offset"] = offset,
            ["truncated"] = offset + values.Count < total,
            [arrayName] = values,
        };

        private static SerializedValueCodec.ReadOptions CompleteReadOptions() => new SerializedValueCodec.ReadOptions
        {
            MaxDepth = 64,
            MaxArrayItems = int.MaxValue,
        };

        private static void ValidateAssignedValue(SerializedProperty property, JToken value, bool requireCompleteGeneric)
        {
            if (value == null || value.Type == JTokenType.Null)
            {
                if (property.propertyType == SerializedPropertyType.ObjectReference || property.propertyType == SerializedPropertyType.String) return;
                throw new InvalidOperationException($"{property.propertyPath} does not accept null");
            }
            if (requireCompleteGeneric) ValidateCompleteValue(property, value);
        }

        private static void ValidateCompleteValue(SerializedProperty property, JToken value)
        {
            ValidateAssignedValue(property, value, false);
            if (property.propertyType != SerializedPropertyType.Generic || property.isArray) return;
            var fields = value as JObject;
            if (fields?["fields"] is JObject nested) fields = nested;
            if (fields == null) throw new InvalidOperationException($"{property.propertyPath} requires a complete object value");
            var iterator = property.Copy();
            var end = iterator.GetEndProperty();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, end))
            {
                enterChildren = false;
                if (iterator.depth != property.depth + 1) continue;
                var childValue = fields[iterator.name];
                if (childValue == null) throw new InvalidOperationException($"{property.propertyPath} requires field '{iterator.name}' because inserted values are undefined");
                ValidateCompleteValue(iterator, childValue);
            }
        }

        private static void DeleteArrayElement(SerializedProperty property, int index)
        {
            var oldSize = property.arraySize;
            property.DeleteArrayElementAtIndex(index);
            if (property.arraySize == oldSize) property.DeleteArrayElementAtIndex(index);
            if (property.arraySize != oldSize - 1)
                throw new InvalidOperationException($"DeleteArrayElementAtIndex did not remove index {index}");
        }
    }
}
