using System;
using UnityEngine;

namespace Maicon.SceneAnalyzer
{
    /// Marks a serialized field as required. RequiredFieldAnalyzer checks that the field
    /// is non-null / non-empty on every active MonoBehaviour in the scene snapshot.
    /// Field-only — properties are not serialized by Unity and so are not checked.
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class RequiredFieldAttribute : PropertyAttribute
    {
    }
}
