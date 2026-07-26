using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
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
            "Maicon.SceneAnalyzer.Editor.",
            "UnityEditor.AssetDatabase",
            "UnityEditor.FileUtil",
            "UnityEditor.EditorApplication",
            "UnityEditor.EditorPrefs",
            "UnityEditor.SessionState",
            "UnityEditor.EditorUtility",
            "UnityEditor.SettingsService",
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
            "UnityEngine.Application.OpenURL",
            "UnityEditor.Help.BrowseURL",
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
            "Import Package",
            "Export Package",
            "Quit",
            "Exit",
        };

        public static bool ValidateMethod(MethodInfo method, JArray arguments, out string reason)
        {
            reason = null;
            if (method == null) { reason = "method is required"; return false; }
            if (!method.IsStatic || !method.IsPublic)
            {
                reason = "only public static methods are allowed by the reflection sandbox";
                return false;
            }
            if (method.IsSpecialName)
            {
                reason = "property/event accessors and operator methods are blocked; use a dedicated MCP tool";
                return false;
            }
            if (method.ContainsGenericParameters)
            {
                reason = "generic methods are not supported";
                return false;
            }

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

            var parameters = method.GetParameters();
            if (parameters.Any(parameter => parameter.ParameterType.IsByRef))
            {
                reason = "ref/out parameters are not supported";
                return false;
            }
            if (parameters.Any(parameter => parameter.IsDefined(typeof(ParamArrayAttribute), false)))
            {
                reason = "params methods are not supported by generic reflection invocation";
                return false;
            }
            if (parameters.Any(parameter => IsUnsafeTransportType(parameter.ParameterType)))
            {
                reason = "delegate, reflection metadata, pointer and task parameters are blocked";
                return false;
            }

            var returnType = method.ReturnType;
            if (typeof(Delegate).IsAssignableFrom(returnType)
                || typeof(System.Collections.IEnumerator).IsAssignableFrom(returnType)
                || typeof(Task).IsAssignableFrom(returnType)
                || returnType.FullName == "System.Threading.Tasks.ValueTask"
                || (returnType.IsGenericType && returnType.GetGenericTypeDefinition().FullName == "System.Threading.Tasks.ValueTask`1"))
            {
                reason = "methods returning delegates, enumerators or System.Threading.Tasks.Task/ValueTask are not supported";
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
            var normalized = menuPath.Trim();
            if (normalized.Length > 1024) { reason = "menuPath exceeds 1024 characters"; return false; }
            if (normalized.IndexOf('/') <= 0 || normalized.EndsWith("/", StringComparison.Ordinal))
            {
                reason = "menuPath must be an exact slash-delimited command path";
                return false;
            }
            if (normalized.Any(char.IsControl))
            {
                reason = "menuPath contains control characters";
                return false;
            }
            foreach (var fragment in BlockedMenuFragments)
            {
                if (normalized.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    reason = $"menu path contains blocked modal/destructive fragment '{fragment}'";
                    return false;
                }
            }
            return true;
        }

        private static bool IsUnsafeTransportType(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            return underlying.IsPointer
                || typeof(Delegate).IsAssignableFrom(underlying)
                || typeof(MemberInfo).IsAssignableFrom(underlying)
                || underlying == typeof(Type)
                || typeof(Task).IsAssignableFrom(underlying);
        }
    }
}
