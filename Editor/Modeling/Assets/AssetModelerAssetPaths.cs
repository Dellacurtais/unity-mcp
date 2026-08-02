using System;
using System.Text;
using UnityEditor;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Assets
{
    public static class AssetModelerAssetPaths
    {
        public const string DefaultRoot = "Assets/AssetModeler";

        public static string ValidateAndEnsureFolder(string folder)
        {
            folder = string.IsNullOrWhiteSpace(folder) ? DefaultRoot : folder.Replace('\\', '/').TrimEnd('/');
            if (!folder.Equals("Assets", StringComparison.Ordinal)
                && !folder.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException("Asset Modeler output must be inside Assets/.", nameof(folder));

            var parts = folder.Split('/');
            var current = "Assets";
            for (int i = 1; i < parts.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(parts[i])) continue;
                if (parts[i] == "." || parts[i] == "..")
                    throw new ArgumentException("Asset Modeler output cannot contain '.' or '..' path segments.", nameof(folder));
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
            return current;
        }

        public static string SafeName(string value, string fallback = "Mesh")
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                if (char.IsLetterOrDigit(character) || character == '-' || character == '_') builder.Append(character);
                else if (char.IsWhiteSpace(character) || character == '.') builder.Append('_');
            }
            var result = builder.ToString().Trim('_');
            return string.IsNullOrEmpty(result) ? fallback : result;
        }

        public static string UniqueAssetPath(string folder, string fileName, string extension)
        {
            folder = ValidateAndEnsureFolder(folder);
            return AssetDatabase.GenerateUniqueAssetPath($"{folder}/{SafeName(fileName)}{extension}");
        }
    }
}
