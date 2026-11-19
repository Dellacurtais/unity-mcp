using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Core
{
    internal static class RendererMeshUtility
    {
        internal static Mesh GetSharedMesh(Renderer renderer)
        {
            // Unity's missing/destroyed component wrappers require its overloaded
            // null check; C# null-conditional access does not perform that check.
            if (renderer == null) return null;
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            if (!(renderer is MeshRenderer)) return null;
            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }
    }
}
