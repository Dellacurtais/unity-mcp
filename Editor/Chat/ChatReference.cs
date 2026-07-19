using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Chat
{
    [Serializable]
    public sealed class ChatReference
    {
        public string Label;
        public ToolTargetRef Target;
        public bool IsFolder;
        public bool IsAvailable = true;
        public string ValidationError;

        public string Kind => IsFolder ? "folder" : Target?.Kind ?? "object";

        public string CanonicalKey
        {
            get
            {
                if (Target == null) return "missing:" + (Label ?? "");
                if (!string.IsNullOrEmpty(Target.GlobalObjectId)) return "global:" + Target.GlobalObjectId;
                if (!string.IsNullOrEmpty(Target.AssetGuid)) return $"asset:{Target.AssetGuid}:{Target.LocalFileId}";
                if (!string.IsNullOrEmpty(Target.AssetPath)) return "path:" + NormalizePath(Target.AssetPath);
                if (!string.IsNullOrEmpty(Target.SceneGuid) || !string.IsNullOrEmpty(Target.HierarchyPath))
                    return $"scene:{Target.SceneGuid}:{NormalizePath(Target.ScenePath)}:{Target.HierarchyPath}:{Target.ComponentType}:{Target.ComponentOrdinal}";
                return "unstable:" + (Label ?? "");
            }
        }

        public static ChatReference Capture(UnityEngine.Object target)
        {
            if (target == null) return null;
            var assetPath = AssetDatabase.GetAssetPath(target);
            var isFolder = !string.IsNullOrEmpty(assetPath) && AssetDatabase.IsValidFolder(assetPath);
            var reference = new ChatReference
            {
                Label = isFolder ? Path.GetFileName(assetPath.TrimEnd('/', '\\')) : target.name,
                Target = ToolTargetRef.Capture(target),
                IsFolder = isFolder,
            };
            reference.Validate();
            return reference;
        }

        public static ChatReference FromAssetPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath)) return null;
            assetPath = NormalizePath(assetPath.Trim());
            var isFolder = AssetDatabase.IsValidFolder(assetPath);
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            var target = asset != null ? ToolTargetRef.Capture(asset) : new ToolTargetRef
            {
                Kind = "asset",
                AssetPath = assetPath,
                AssetGuid = AssetDatabase.AssetPathToGUID(assetPath),
            };
            if (target != null && string.IsNullOrEmpty(target.AssetPath)) target.AssetPath = assetPath;
            var reference = new ChatReference
            {
                Label = Path.GetFileName(assetPath.TrimEnd('/', '\\')),
                Target = target,
                IsFolder = isFolder,
            };
            reference.Validate();
            return reference;
        }

        public bool Validate()
        {
            ValidationError = null;
            if (Target == null)
            {
                IsAvailable = false;
                ValidationError = "Referência vazia";
                return false;
            }
            if (IsFolder)
            {
                IsAvailable = !string.IsNullOrEmpty(Target.AssetPath) && AssetDatabase.IsValidFolder(Target.AssetPath);
                if (!IsAvailable) ValidationError = "Pasta não encontrada";
                return IsAvailable;
            }
            if (!HasStableIdentity(Target))
            {
                IsAvailable = false;
                ValidationError = "A referência não possui identidade estável";
                return false;
            }
            IsAvailable = ToolTargetResolver.Resolve(Target, out var error) != null;
            ValidationError = IsAvailable ? null : error;
            return IsAvailable;
        }

        public JObject ToJson()
        {
            return new JObject
            {
                ["label"] = Label,
                ["kind"] = Kind,
                ["isFolder"] = IsFolder,
                ["target"] = Target?.ToJson(),
            };
        }

        public static ChatReference FromJson(JToken token, bool validate = true)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            var reference = new ChatReference
            {
                Label = (string)token["label"],
                IsFolder = (bool?)token["isFolder"] ?? false,
                Target = ToolTargetRef.FromJson(token["target"]),
            };
            if (validate) reference.Validate();
            return reference;
        }

        public static string ParseMessageContent(string content, ICollection<ChatReference> references)
        {
            var text = content ?? "";
            var trimmed = text.TrimEnd();
            const string fenceStart = "```unity-context\n";
            const string fenceEnd = "\n```";
            if (!trimmed.EndsWith(fenceEnd, StringComparison.Ordinal)) return StripDefaultPreamble(text);

            var endIndex = trimmed.Length - fenceEnd.Length;
            var startIndex = trimmed.LastIndexOf(fenceStart, endIndex, StringComparison.Ordinal);
            if (startIndex < 0) return StripDefaultPreamble(text);

            try
            {
                var jsonStart = startIndex + fenceStart.Length;
                var context = JObject.Parse(trimmed.Substring(jsonStart, endIndex - jsonStart));
                if (!string.Equals((string)context["type"], "unity-context", StringComparison.Ordinal))
                    return StripDefaultPreamble(text);

                if (references != null && context["references"] is JArray items)
                {
                    var keys = new HashSet<string>(references.Where(item => item != null).Select(item => item.CanonicalKey), StringComparer.Ordinal);
                    foreach (var item in items)
                    {
                        var reference = FromJson(item, false);
                        if (reference != null && keys.Add(reference.CanonicalKey)) references.Add(reference);
                    }
                }
                return StripDefaultPreamble(trimmed.Substring(0, startIndex).TrimEnd());
            }
            catch
            {
                return StripDefaultPreamble(text);
            }
        }

        public static string BuildMessageContent(string text, IEnumerable<ChatReference> references)
        {
            var valid = (references ?? Enumerable.Empty<ChatReference>())
                .Where(r => r != null && r.Validate())
                .GroupBy(r => r.CanonicalKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            if (valid.Count == 0) return text ?? "";
            var context = new JObject
            {
                ["type"] = "unity-context",
                ["version"] = 1,
                ["references"] = new JArray(valid.Select(r => r.ToJson())),
            };
            var prefix = string.IsNullOrWhiteSpace(text) ? "" : text.TrimEnd() + "\n\n";
            return prefix + "```unity-context\n" + context.ToString(Formatting.None) + "\n```";
        }

        private static string StripDefaultPreamble(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            const string marker = "[unity-context — auto-injected by the Unity Scene Analyzer plugin";
            const string separator = "\n\n---\n\n";
            if (!text.StartsWith(marker, StringComparison.Ordinal)) return text;
            var separatorIndex = text.IndexOf(separator, StringComparison.Ordinal);
            return separatorIndex < 0 ? text : text.Substring(separatorIndex + separator.Length);
        }

        private static bool HasStableIdentity(ToolTargetRef target)
        {
            return !string.IsNullOrEmpty(target.GlobalObjectId)
                || !string.IsNullOrEmpty(target.AssetGuid)
                || !string.IsNullOrEmpty(target.AssetPath)
                || (!string.IsNullOrEmpty(target.HierarchyPath)
                    && (!string.IsNullOrEmpty(target.SceneGuid) || !string.IsNullOrEmpty(target.ScenePath)));
        }

        private static string NormalizePath(string value) => (value ?? "").Replace('\\', '/').TrimEnd('/');
    }
}
