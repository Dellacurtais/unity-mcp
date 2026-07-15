using System;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.NavMesh.Tools;
using Maicon.SceneAnalyzer.Editor.Optional;
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
                if (Bootstrap.ToolRegistry == null || OptionalModuleRegistry.Contains("navmesh")) return;

                OptionalToolSupport.RegisterModule("navmesh", "com.unity.ai.navigation",
                    new ListNavMeshSurfacesTool(),
                    new BakeNavMeshSurfaceTool(),
                    new AddNavMeshSurfaceTool(),
                    new AddNavMeshModifierTool(),
                    new AddNavMeshLinkTool());

                ChangesetExecutor.RegisterHandler("bake_navmesh_surface", NavMeshSurfaceAuthor.BakeSurface);
                ChangesetExecutor.RegisterHandler("add_navmesh_surface", NavMeshSurfaceAuthor.AddSurface);
                ChangesetExecutor.RegisterHandler("add_navmesh_modifier", NavMeshSurfaceAuthor.AddModifier);
                ChangesetExecutor.RegisterHandler("add_navmesh_link", NavMeshSurfaceAuthor.AddLink);
            }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}
