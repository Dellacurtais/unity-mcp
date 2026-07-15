using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools.Reflect
{
    public static class ReflectionPolicy
    {
        private static readonly string[] AllowedTypePrefixes =
        {
            "UnityEngine.",
            "UnityEditor.",
            "Maicon.SceneAnalyzer.",
        };

        private static readonly string[] BlockedTypePrefixes =
        {
            "UnityEditor.AssetDatabase",
            "UnityEditor.FileUtil",
            "UnityEditor.EditorApplication",
            "UnityEditor.PackageManager.",
            "UnityEditor.Build.",
            "UnityEditor.Compilation.",
            "UnityEditor.VersionControl.",
            "UnityEditorInternal.",
            "UnityEngine.SceneManagement.",
            "UnityEngine.PlayerPrefs",
            "UnityEngine.Resources",
        };

        private static readonly HashSet<string> BlockedMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "UnityEngine.Application.Quit",
            "UnityEditor.EditorApplication.Exit",
            "UnityEditor.EditorApplication.ExitPlaymode",
            "UnityEditor.AssetDatabase.DeleteAsset",
            "UnityEditor.AssetDatabase.MoveAssetToTrash",
            "UnityEditor.FileUtil.DeleteFileOrDirectory",
            "UnityEditor.FileUtil.ReplaceFile",
            "UnityEngine.Object.Destroy",
            "UnityEngine.Object.DestroyImmediate",
            "UnityEngine.Object.DontDestroyOnLoad",
        };

        private static readonly string[] BlockedMenuFragments =
        {
            "Build Settings",
            "Build Profiles",
            "Project Settings",
            "Preferences",
            "Package Manager",
            "Save As",
            "Open Project",
            "Quit",
            "Exit",
        };

        public static bool ValidateMethod(MethodInfo method, JArray arguments, out string reason)
        {
            reason = null;
            if (method == null) { reason = "method is required"; return false; }
            var declaringType = method.DeclaringType;
            var typeName = declaringType?.FullName ?? string.Empty;
            foreach (var prefix in BlockedTypePrefixes)
            {
                if (typeName.StartsWith(prefix, StringComparison.Ordinal))
                {
                    reason = $"type '{typeName}' is blocked by the reflection sandbox";
                    return false;
                }
            }

            if (!AllowedTypePrefixes.Any(prefix => typeName.StartsWith(prefix, StringComparison.Ordinal)))
            {
                reason = $"type '{typeName}' is outside the reflection allowlist";
                return false;
            }

            var methodKey = typeName + "." + method.Name;
            if (BlockedMethods.Contains(methodKey))
            {
                reason = $"method '{methodKey}' is blocked by the reflection sandbox";
                return false;
            }
            if (typeof(Delegate).IsAssignableFrom(method.ReturnType)
                || typeof(System.Collections.IEnumerator).IsAssignableFrom(method.ReturnType))
            {
                reason = "methods returning delegates or enumerators are not supported";
                return false;
            }
            if (method.GetCustomAttribute<ObsoleteAttribute>() != null)
            {
                reason = "obsolete methods are blocked from generic reflection invocation";
                return false;
            }
            return true;
        }

        public static bool ValidateMenuPath(string menuPath, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(menuPath)) { reason = "menuPath is required"; return false; }
            foreach (var fragment in BlockedMenuFragments)
            {
                if (menuPath.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    reason = $"menu path contains blocked modal/destructive fragment '{fragment}'";
                    return false;
                }
            }
            return true;
        }
    }
}
