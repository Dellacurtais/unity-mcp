using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Maicon.SceneAnalyzer.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Analyzers
{
    /// Reports MonoBehaviour fields decorated with [RequiredField] that are
    /// null (Unity null overload), empty string, or empty collection. Skips
    /// missing-script entries — those are MissingReferenceAnalyzer's job.
    public sealed class RequiredFieldAnalyzer : IAnalyzer
    {
        public string Id => "required-field";
        public string Category => "validation";

        // Domain-lifetime cache; do not clear. Type identity is stable until
        // domain reload, which re-allocates the dict.
        private static readonly Dictionary<Type, FieldInfo[]> _fieldCache = new();

        public IEnumerable<Issue> Run(SceneSnapshot snapshot)
        {
            foreach (var info in snapshot.All())
            {
                if (!info.IsActive) continue;
                var go = EditorUtility.InstanceIDToObject(info.InstanceID) as GameObject;
                if (go == null) continue;
                var path = snapshot.GetPath(info.InstanceID);

                foreach (var mb in go.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    var fields = GetRequiredFields(mb.GetType());
                    if (fields.Length == 0) continue;

                    foreach (var f in fields)
                    {
                        if (!IsEmpty(f.GetValue(mb))) continue;

                        var data = new Dictionary<string, object>
                        {
                            ["field"] = f.Name,
                            ["componentType"] = mb.GetType().FullName,
                        };
                        yield return new Issue(
                            analyzer: Id, severity: IssueSeverity.Error, category: Category,
                            message: $"{mb.GetType().Name}.{f.Name}: required field is empty.",
                            instanceId: info.InstanceID, gameObjectPath: path, data: data);
                    }
                }
            }
        }

        private static FieldInfo[] GetRequiredFields(Type t)
        {
            if (_fieldCache.TryGetValue(t, out var cached)) return cached;

            var list = new List<FieldInfo>();
            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            for (var cur = t; cur != null && cur != typeof(MonoBehaviour); cur = cur.BaseType)
            {
                foreach (var f in cur.GetFields(flags))
                {
                    if (f.IsLiteral || f.IsInitOnly) continue;
                    if (f.IsDefined(typeof(RequiredFieldAttribute), inherit: false))
                        list.Add(f);
                }
            }

            var arr = list.ToArray();
            _fieldCache[t] = arr;
            return arr;
        }

        private static bool IsEmpty(object v)
        {
            if (v is UnityEngine.Object u) return u == null;
            if (v == null) return true;
            if (v is string s) return string.IsNullOrEmpty(s);
            if (v is ICollection col) return col.Count == 0;
            return false;
        }
    }
}
