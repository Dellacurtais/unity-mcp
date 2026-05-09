using System;
using UnityEngine;

namespace Maicon.SceneAnalyzer
{
    /// Marks a serialized field as required. The RequiredFieldAnalyzer checks that the field
    /// is non-null / non-empty on every active component instance in the scene.
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class RequiredFieldAttribute : PropertyAttribute
    {
    }
}
