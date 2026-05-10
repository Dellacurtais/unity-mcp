using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Analyzers
{
    /// Walks every Component's SerializedObject looking for object-reference
    /// fields that are flagged as broken (instanceID != 0 but objectReferenceValue
    /// is null). [RequiredField] empty-field checks live in RequiredFieldAnalyzer.
    public sealed class MissingReferenceAnalyzer : IAnalyzer
    {
        public string Id => "missing-reference";
        public string Category => "validation";

        public IEnumerable<Issue> Run(SceneSnapshot snapshot)
        {
            foreach (var info in snapshot.All())
            {
                var go = EditorUtility.InstanceIDToObject(info.InstanceID) as GameObject;
                if (go == null) continue;

                var path = snapshot.GetPath(info.InstanceID);
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null)
                    {
                        yield return new Issue(
                            analyzer: Id, severity: IssueSeverity.Error, category: Category,
                            message: "Missing script reference (broken MonoBehaviour).",
                            instanceId: info.InstanceID, gameObjectPath: path);
                        continue;
                    }

                    var so = new SerializedObject(c);
                    var sp = so.GetIterator();
                    var enterChildren = true;
                    while (sp.NextVisible(enterChildren))
                    {
                        enterChildren = false;
                        if (sp.propertyType != SerializedPropertyType.ObjectReference) continue;

                        // Broken reference: had an asset but it can no longer be resolved.
                        if (sp.objectReferenceValue == null && sp.objectReferenceInstanceIDValue != 0)
                        {
                            yield return new Issue(
                                analyzer: Id, severity: IssueSeverity.Error, category: Category,
                                message: $"{c.GetType().Name}.{sp.displayName}: broken reference (asset deleted/unresolved).",
                                instanceId: info.InstanceID, gameObjectPath: path);
                        }
                    }
                }
            }
        }
    }
}
