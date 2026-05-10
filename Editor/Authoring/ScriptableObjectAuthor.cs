using System;
using System.IO;
using System.Linq;
using System.Reflection;
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
            var withMenuOnly = (bool?)args["withMenuOnly"] ?? false;
            var limit = (int?)args["limit"] ?? 200;

            var sb = new System.Text.StringBuilder();
            int count = 0;
            var soBase = typeof(ScriptableObject);
            var editorWindow = typeof(UnityEditor.EditorWindow);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var asmName = asm.GetName().Name ?? "";
                bool isBuiltin = ComponentTypeInspector.IsBuiltinAssembly(asmName);
                if (category == "user" && isBuiltin) continue;
                if (category == "builtin" && !isBuiltin) continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException tle) { types = tle.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null) continue;
                    if (!soBase.IsAssignableFrom(t)) continue;
                    if (t == soBase) continue;
                    if (t.IsAbstract || t.IsGenericType) continue;
                    // Skip EditorWindow and most editor-only SOs (still ScriptableObject-derived,
                    // but not what the agent typically wants to instantiate as data assets).
                    if (editorWindow.IsAssignableFrom(t)) continue;

                    if (!string.IsNullOrEmpty(nameContains)
                        && t.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0
                        && (t.FullName ?? "").IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var menu = t.GetCustomAttribute<CreateAssetMenuAttribute>();
                    if (withMenuOnly && menu == null) continue;

                    sb.Append($"  {t.FullName}");
                    sb.Append($"  (asm={asmName}, builtin={isBuiltin})");
                    if (menu != null)
                    {
                        sb.Append($"  [menu='{menu.menuName}'");
                        if (!string.IsNullOrEmpty(menu.fileName)) sb.Append($", default='{menu.fileName}'");
                        sb.Append(']');
                    }
                    sb.AppendLine();
                    count++;
                    if (count >= limit) goto done;
                }
            }
            done:
            return new OpResult { Ok = true, Summary = $"{count} ScriptableObject type(s):\n" + sb, InstanceID = 0 };
        }

        // ============== Create ==============

        public static OpResult Create(JObject args)
        {
            var typeName = (string)args["className"];
            var path = (string)args["path"];
            if (string.IsNullOrEmpty(typeName)) return OpResult.Fail("className is required");
            if (string.IsNullOrEmpty(path)) return OpResult.Fail("path is required (e.g. Assets/Data/MyAsset.asset)");
            if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) path += ".asset";

            var type = ResolveScriptableObjectType(typeName);
            if (type == null) return OpResult.Fail(
                $"unknown ScriptableObject type '{typeName}'. " +
                $"If you JUST created the script, call refresh_assets first. " +
                $"Otherwise use list_scriptable_object_types nameContains=\"{typeName}\" to find the real name.");

            EnsureFolder(Path.GetDirectoryName(path));
            var so = ScriptableObject.CreateInstance(type);
            so.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(so, path);

            // Apply initialFields if provided. Each key = SerializedProperty name; value goes through ApplyValue.
            int applied = 0;
            if (args["initialFields"] is JObject fields)
            {
                var serObj = new SerializedObject(so);
                foreach (var kv in fields)
                {
                    var p = serObj.FindProperty(kv.Key);
                    if (p == null) continue;
                    try { ComponentEditor.ApplyValue(p, kv.Value); applied++; }
                    catch (Exception e) { Debug.LogWarning($"[scene-analyzer] initialFields[{kv.Key}]: {e.Message}"); }
                }
                serObj.ApplyModifiedProperties();
            }

            EditorUtility.SetDirty(so);
            AssetDatabase.SaveAssets();
            return OpResult.Success(so.GetInstanceID(),
                $"created {type.Name} at {path}" + (applied > 0 ? $" (set {applied} initial field(s))" : ""));
        }

        // ============== Read ==============

        public static OpResult Read(JObject args)
        {
            var assetRef = (string)args["guidOrPath"];
            if (string.IsNullOrEmpty(assetRef)) return OpResult.Fail("guidOrPath is required");
            var path = ResolvePath(assetRef);
            var so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (so == null) return OpResult.Fail($"ScriptableObject not found: {assetRef}");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{so.name} ({so.GetType().FullName})");
            sb.AppendLine($"  path: {path}");

            var serObj = new SerializedObject(so);
            var iter = serObj.GetIterator();
            bool enterChildren = true;
            int shown = 0;
            while (iter.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iter.name == "m_Script") continue;
                if (shown++ >= 80) { sb.AppendLine($"  [+more fields truncated]"); break; }
                sb.AppendLine($"  {iter.name} ({iter.propertyType}{(iter.isArray ? $" array[{iter.arraySize}]" : "")}) = {DescribeValue(iter)}");
            }
            return new OpResult { Ok = true, Summary = sb.ToString(), InstanceID = so.GetInstanceID() };
        }

        // ============== Set property ==============

        public static OpResult SetProperty(JObject args)
        {
            var assetRef = (string)args["assetGuidOrPath"];
            if (string.IsNullOrEmpty(assetRef)) return OpResult.Fail("assetGuidOrPath is required");
            var path = ResolvePath(assetRef);
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset == null) return OpResult.Fail($"asset not found: {assetRef}");

            var propPath = (string)args["propertyPath"];
            if (string.IsNullOrEmpty(propPath)) return OpResult.Fail("propertyPath is required");

            var serObj = new SerializedObject(asset);
            var prop = serObj.FindProperty(propPath);
            if (prop == null) return OpResult.Fail($"property '{propPath}' not found on {asset.GetType().Name}");

            Undo.RecordObject(asset, $"Set {asset.name}.{propPath}");
            try { ComponentEditor.ApplyValue(prop, args["value"]); }
            catch (Exception e) { return OpResult.Fail($"INVALID_ARG: {e.Message}"); }
            serObj.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
            return OpResult.Success(asset.GetInstanceID(), $"set {asset.GetType().Name}.{propPath}");
        }

        // ============== Duplicate ==============

        public static OpResult Duplicate(JObject args)
        {
            var srcRef = (string)args["sourceGuidOrPath"];
            var newPath = (string)args["targetPath"];
            if (string.IsNullOrEmpty(srcRef) || string.IsNullOrEmpty(newPath))
                return OpResult.Fail("sourceGuidOrPath + targetPath required");
            if (!newPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) newPath += ".asset";

            var srcPath = ResolvePath(srcRef);
            var src = AssetDatabase.LoadAssetAtPath<ScriptableObject>(srcPath);
            if (src == null) return OpResult.Fail($"source not found: {srcRef}");

            var clone = Object.Instantiate(src);
            clone.name = Path.GetFileNameWithoutExtension(newPath);
            EnsureFolder(Path.GetDirectoryName(newPath));
            AssetDatabase.CreateAsset(clone, newPath);
            AssetDatabase.SaveAssets();
            return OpResult.Success(clone.GetInstanceID(),
                $"duplicated {src.GetType().Name} '{src.name}' → {newPath}");
        }

        // ============== Modify array property (works on Component OR SO target) ==============

        public static OpResult ModifyArrayProperty(JObject args)
        {
            var serObj = ResolveSerializedObject(args, out var err);
            if (serObj == null) return OpResult.Fail(err);

            var propPath = (string)args["propertyPath"];
            if (string.IsNullOrEmpty(propPath)) return OpResult.Fail("propertyPath is required");
            var prop = serObj.FindProperty(propPath);
            if (prop == null) return OpResult.Fail($"property '{propPath}' not found");
            if (!prop.isArray) return OpResult.Fail($"'{propPath}' is not an array/list (it's {prop.propertyType})");

            var action = ((string)args["action"] ?? "append").ToLowerInvariant();
            Undo.RecordObject(serObj.targetObject, $"Array {action}");

            string summary;
            switch (action)
            {
                case "append":
                {
                    var idx = prop.arraySize;
                    prop.arraySize = idx + 1;
                    var elem = prop.GetArrayElementAtIndex(idx);
                    if (args["value"] != null)
                        try { ComponentEditor.ApplyValue(elem, args["value"]); }
                        catch (Exception e) { return OpResult.Fail($"INVALID_ARG: {e.Message}"); }
                    summary = $"appended at [{idx}] (new size {prop.arraySize})";
                    break;
                }
                case "insert":
                {
                    var idx = (int?)args["index"] ?? 0;
                    if (idx < 0 || idx > prop.arraySize) return OpResult.Fail($"index {idx} out of range [0..{prop.arraySize}]");
                    prop.InsertArrayElementAtIndex(idx);
                    if (args["value"] != null)
                    {
                        var elem = prop.GetArrayElementAtIndex(idx);
                        try { ComponentEditor.ApplyValue(elem, args["value"]); }
                        catch (Exception e) { return OpResult.Fail($"INVALID_ARG: {e.Message}"); }
                    }
                    summary = $"inserted at [{idx}] (new size {prop.arraySize})";
                    break;
                }
                case "remove":
                {
                    var idx = (int?)args["index"] ?? -1;
                    if (idx < 0 || idx >= prop.arraySize) return OpResult.Fail($"index {idx} out of range [0..{prop.arraySize - 1}]");
                    // Object-ref array quirk: first DeleteArrayElementAtIndex nulls it,
                    // second actually removes. Apply twice when needed.
                    var elem = prop.GetArrayElementAtIndex(idx);
                    if (elem.propertyType == SerializedPropertyType.ObjectReference
                        && elem.objectReferenceValue != null)
                        prop.DeleteArrayElementAtIndex(idx);
                    prop.DeleteArrayElementAtIndex(idx);
                    summary = $"removed at [{idx}] (new size {prop.arraySize})";
                    break;
                }
                case "resize":
                {
                    var size = (int?)args["size"] ?? -1;
                    if (size < 0) return OpResult.Fail("size required for resize (>= 0)");
                    prop.arraySize = size;
                    summary = $"resized to {size}";
                    break;
                }
                case "clear":
                    prop.ClearArray();
                    summary = "cleared";
                    break;
                case "set":
                {
                    var idx = (int?)args["index"] ?? -1;
                    if (idx < 0 || idx >= prop.arraySize) return OpResult.Fail($"index {idx} out of range");
                    var elem = prop.GetArrayElementAtIndex(idx);
                    if (args["value"] == null) return OpResult.Fail("value required for set");
                    try { ComponentEditor.ApplyValue(elem, args["value"]); }
                    catch (Exception e) { return OpResult.Fail($"INVALID_ARG: {e.Message}"); }
                    summary = $"set [{idx}]";
                    break;
                }
                default:
                    return OpResult.Fail($"unknown action '{action}' (use append/insert/remove/resize/clear/set)");
            }

            serObj.ApplyModifiedProperties();
            EditorUtility.SetDirty(serObj.targetObject);
            return OpResult.Success(serObj.targetObject.GetInstanceID(), summary);
        }

        // ============== Helpers ==============

        private static SerializedObject ResolveSerializedObject(JObject args, out string err)
        {
            err = null;
            var assetRef = (string)args["assetGuidOrPath"];
            if (!string.IsNullOrEmpty(assetRef))
            {
                var path = ResolvePath(assetRef);
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (asset == null) { err = $"asset not found: {assetRef}"; return null; }
                return new SerializedObject(asset);
            }
            var id = (int?)args["instanceId"] ?? 0;
            var typeName = (string)args["componentType"];
            if (id == 0) { err = "either assetGuidOrPath or instanceId+componentType required"; return null; }
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) { err = $"GameObject not found: instanceId={id}"; return null; }
            if (string.IsNullOrEmpty(typeName)) { err = "componentType required when targeting a GameObject"; return null; }
            var t = ComponentEditor.ResolveComponentType(typeName);
            if (t == null) { err = $"unknown component type '{typeName}'"; return null; }
            var c = go.GetComponent(t);
            if (c == null) { err = $"component '{typeName}' not found on '{go.name}'"; return null; }
            return new SerializedObject(c);
        }

        public static Type ResolveScriptableObjectType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var direct = Type.GetType(name);
            if (direct != null && typeof(ScriptableObject).IsAssignableFrom(direct)) return direct;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(name);
                if (t != null && typeof(ScriptableObject).IsAssignableFrom(t)) return t;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException tle) { types = tle.Types.Where(x => x != null).ToArray(); }
                catch { continue; }
                foreach (var c in types)
                {
                    if (c == null) continue;
                    if (string.Equals(c.Name, name, StringComparison.Ordinal)
                        && typeof(ScriptableObject).IsAssignableFrom(c)
                        && !c.IsAbstract) return c;
                }
            }
            return null;
        }

        private static string ResolvePath(string assetRef)
        {
            return assetRef.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? assetRef
                : AssetDatabase.GUIDToAssetPath(assetRef);
        }

        private static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
            var parent = Path.GetDirectoryName(folder);
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            var name = Path.GetFileName(folder);
            if (!string.IsNullOrEmpty(parent)) AssetDatabase.CreateFolder(parent, name);
        }

        private static string DescribeValue(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.intValue.ToString();
                case SerializedPropertyType.Boolean: return p.boolValue.ToString();
                case SerializedPropertyType.Float: return p.floatValue.ToString("F3");
                case SerializedPropertyType.String: return $"\"{p.stringValue}\"";
                case SerializedPropertyType.Color: return p.colorValue.ToString();
                case SerializedPropertyType.Vector2: return p.vector2Value.ToString();
                case SerializedPropertyType.Vector3: return p.vector3Value.ToString();
                case SerializedPropertyType.Vector4: return p.vector4Value.ToString();
                case SerializedPropertyType.Quaternion: return p.quaternionValue.eulerAngles.ToString();
                case SerializedPropertyType.LayerMask: return $"mask=0x{p.intValue:X8}";
                case SerializedPropertyType.Enum:
                    return p.enumValueIndex >= 0 && p.enumValueIndex < p.enumNames.Length
                        ? p.enumNames[p.enumValueIndex]
                        : p.enumValueIndex.ToString();
                case SerializedPropertyType.ObjectReference:
                    if (p.objectReferenceValue == null) return "null";
                    return $"{p.objectReferenceValue.name} ({p.objectReferenceValue.GetType().Name})";
                default:
                    return $"[{p.propertyType}]";
            }
        }
    }
}
