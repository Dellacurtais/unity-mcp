using System;
using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Mcp;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Optional.ProBuilder
{
    [InitializeOnLoad]
    internal static class ProBuilderBootstrap
    {
        private const string PackageId = "com.unity.probuilder";
        internal static readonly string[] MeshTypes = { "UnityEngine.ProBuilder.ProBuilderMesh" };
        private static readonly IReadOnlyDictionary<string, string[]> Types =
            new Dictionary<string, string[]> { ["mesh"] = MeshTypes };

        static ProBuilderBootstrap() { EditorApplication.delayCall += Initialize; }

        private static void Initialize()
        {
            try
            {
                OptionalToolSupport.RegisterModule("probuilder", PackageId,
                    new OptionalContextTool("get_probuilder_context", "Reports ProBuilder package and mesh context.", PackageId, Types),
                    new OptionalListComponentsTool("list_probuilder_meshes", "Lists ProBuilder meshes with topology summaries.", PackageId, MeshTypes),
                    new CreateProBuilderShapeTool(),
                    new RefreshProBuilderMeshTool(),
                    new ValidateProBuilderSetupTool());
            }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }
}
