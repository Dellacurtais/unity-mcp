using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Assets
{
    public static class FbxExportAdapter
    {
        public static bool IsAvailable => ResolveExporterType() != null;

        public static string Export(GameObject target, string assetPath)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrWhiteSpace(assetPath)) throw new ArgumentException("FBX output path is required.", nameof(assetPath));
            assetPath = assetPath.Replace('\\', '/');
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal) || !assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("FBX output must be an Assets/*.fbx path.", nameof(assetPath));
            var exporter = ResolveExporterType();
            if (exporter == null)
                throw new InvalidOperationException(
                    "FBX_EXPORTER_NOT_INSTALLED: install com.unity.formats.fbx; editable .asset meshes and Prefabs remain the authoritative source.");
            var method = exporter.GetMethod(
                "ExportObject",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(UnityEngine.Object) },
                null);
            if (method == null) throw new InvalidOperationException("FBX_EXPORTER_API_UNAVAILABLE: ModelExporter.ExportObject was not found.");
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                ?? throw new InvalidOperationException("UNITY_PROJECT_PATH_MISSING: cannot resolve project root.");
            var absolute = Path.GetFullPath(Path.Combine(projectRoot, assetPath));
            var assetsRoot = Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("FBX_PATH_OUTSIDE_ASSETS: export path escaped the project Assets directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(absolute) ?? projectRoot);
            var result = method.Invoke(null, new object[] { absolute, target }) as string;
            if (string.IsNullOrEmpty(result) && !File.Exists(absolute))
                throw new InvalidOperationException("FBX_EXPORT_FAILED: Unity FBX Exporter did not create the requested file.");
            AssetDatabase.Refresh();
            return assetPath;
        }

        private static Type ResolveExporterType()
        {
            var type = Type.GetType("UnityEditor.Formats.Fbx.Exporter.ModelExporter, Unity.Formats.Fbx.Editor", false);
            if (type != null) return type;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                type = assemblies[i].GetType("UnityEditor.Formats.Fbx.Exporter.ModelExporter", false);
                if (type != null) return type;
            }
            return null;
        }
    }
}
