using System;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.NavMesh.Tools;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.NavMesh
{
    /// Side-loaded init for `com.unity.ai.navigation`-dependent tools. Lives in
    /// a secondary asmdef gated by SCENE_ANALYZER_HAS_NAVMESH so the main
    /// plugin builds even on projects without the package. When present, this
    /// registers the 4 modern NavMesh tools and their changeset op kinds.
    [InitializeOnLoad]
    internal static class NavMeshBootstrap
    {
        static NavMeshBootstrap()
        {
            EditorApplication.delayCall += Init;
        }

        private static void Init()
        {
            try
            {
                if (Bootstrap.ToolRegistry == null) return;

                ChangesetExecutor.RegisterHandler("bake_navmesh_surface", NavMeshSurfaceAuthor.BakeSurface);
                ChangesetExecutor.RegisterHandler("add_navmesh_surface", NavMeshSurfaceAuthor.AddSurface);
                ChangesetExecutor.RegisterHandler("add_navmesh_modifier", NavMeshSurfaceAuthor.AddModifier);

                Bootstrap.ToolRegistry.Register(new ListNavMeshSurfacesTool());
                Bootstrap.ToolRegistry.Register(new BakeNavMeshSurfaceTool());
                Bootstrap.ToolRegistry.Register(new AddNavMeshSurfaceTool());
                Bootstrap.ToolRegistry.Register(new AddNavMeshModifierTool());

                Bootstrap.RecordEntry("[navmesh] registered 4 modern NavMesh tools (com.unity.ai.navigation present)");
            }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}
