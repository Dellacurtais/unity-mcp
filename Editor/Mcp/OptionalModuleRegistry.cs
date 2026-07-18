using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor.PackageManager;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    public sealed class OptionalModuleInfo
    {
        public string Id { get; internal set; }
        public string PackageId { get; internal set; }
        public string PackageVersion { get; internal set; }
        public string AssemblyName { get; internal set; }
        public DateTimeOffset LoadedUtc { get; internal set; }
        public string[] Tools { get; internal set; }

        public JObject ToJson() => new JObject
        {
            ["id"] = Id,
            ["packageId"] = PackageId,
            ["packageVersion"] = PackageVersion,
            ["assemblyName"] = AssemblyName,
            ["loadedUtc"] = LoadedUtc.ToString("O"),
            ["tools"] = new JArray(Tools ?? Array.Empty<string>()),
        };
    }

    public static class OptionalModuleRegistry
    {
        private sealed class Requirement
        {
            public string PackageId;
            public string MinimumVersion;
        }

        private sealed class ModuleSpec
        {
            public string Id;
            public Requirement[] Alternatives;
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, OptionalModuleInfo> Modules =
            new Dictionary<string, OptionalModuleInfo>(StringComparer.Ordinal);
        private static readonly ModuleSpec[] KnownModules =
        {
            Spec("postfx", Req("com.unity.render-pipelines.core", "10.0.0")),
            Spec("timeline", Req("com.unity.timeline", "1.0.0")),
            Spec("navmesh", Req("com.unity.ai.navigation", "1.0.0")),
            Spec("input-system", Req("com.unity.inputsystem", "1.7.0")),
            Spec("ugui", Req("com.unity.ugui", "1.0.0")),
            Spec("test-framework", Req("com.unity.test-framework", "1.1.0")),
            Spec("cinemachine", Req("com.unity.cinemachine", "3.0.0")),
            Spec("probuilder", Req("com.unity.probuilder", "5.0.0")),
            Spec("addressables", Req("com.unity.addressables", "1.21.0")),
            Spec("localization", Req("com.unity.localization", "1.5.0")),
            Spec("shadergraph", Req("com.unity.shadergraph", "17.0.0")),
            Spec("urp", Req("com.unity.render-pipelines.universal", "17.0.0")),
            Spec("hdrp", Req("com.unity.render-pipelines.high-definition", "17.0.0")),
            Spec("xr", Req("com.unity.xr.interaction.toolkit", "3.0.0")),
            Spec("netcode", Req("com.unity.netcode.gameobjects", "2.0.0")),
            Spec("textmeshpro",
                Req("com.unity.ugui", "2.0.0"),
                Req("com.unity.textmeshpro", "3.0.0")),
        };

        public static void Register(
            string id,
            string packageId,
            string packageVersion,
            string assemblyName,
            IEnumerable<string> tools)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Module id is required.", nameof(id));
            var normalizedTools = (tools ?? Array.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            lock (Sync)
            {
                if (Modules.TryGetValue(id, out var existing))
                {
                    if (string.Equals(existing.PackageId, packageId, StringComparison.Ordinal)
                        && string.Equals(existing.PackageVersion, packageVersion, StringComparison.Ordinal)
                        && string.Equals(existing.AssemblyName, assemblyName, StringComparison.Ordinal)
                        && existing.Tools.SequenceEqual(normalizedTools, StringComparer.Ordinal))
                        return;
                    throw new InvalidOperationException($"Optional module '{id}' was already registered with different metadata.");
                }
                Modules.Add(id, new OptionalModuleInfo
                {
                    Id = id,
                    PackageId = packageId,
                    PackageVersion = packageVersion,
                    AssemblyName = assemblyName,
                    LoadedUtc = DateTimeOffset.UtcNow,
                    Tools = normalizedTools,
                });
            }
        }

        public static bool Contains(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            lock (Sync) return Modules.ContainsKey(id);
        }

        public static OptionalModuleInfo[] All
        {
            get
            {
                lock (Sync)
                    return Modules.Values.OrderBy(module => module.Id, StringComparer.Ordinal).ToArray();
            }
        }

        public static JArray Snapshot() => new JArray(All.Select(module => module.ToJson()));

        public static JArray CompatibilitySnapshot()
        {
            var result = new JArray();
            foreach (var spec in KnownModules.OrderBy(value => value.Id, StringComparer.Ordinal))
            {
                bool anyInstalled = false;
                bool anyCompatible = false;
                var alternatives = new JArray();
                foreach (var requirement in spec.Alternatives)
                {
                    PackageInfo package = null;
                    try { package = PackageInfo.FindForPackageName(requirement.PackageId); }
                    catch { }
                    var installed = package != null;
                    var compatible = installed && VersionAtLeast(package.version, requirement.MinimumVersion);
                    anyInstalled |= installed;
                    anyCompatible |= compatible;
                    alternatives.Add(new JObject
                    {
                        ["packageId"] = requirement.PackageId,
                        ["minimumVersion"] = requirement.MinimumVersion,
                        ["installed"] = installed,
                        ["installedVersion"] = package?.version,
                        ["compatible"] = compatible,
                    });
                }

                OptionalModuleInfo loadedModule;
                lock (Sync) Modules.TryGetValue(spec.Id, out loadedModule);
                var loaded = loadedModule != null;
                string status;
                if (loaded && anyCompatible) status = "loaded";
                else if (loaded) status = "loaded_incompatible";
                else if (anyCompatible) status = "available_not_loaded";
                else if (anyInstalled) status = "version_unsupported";
                else status = "package_missing";

                result.Add(new JObject
                {
                    ["id"] = spec.Id,
                    ["loaded"] = loaded,
                    ["status"] = status,
                    ["toolCount"] = loadedModule?.Tools?.Length ?? 0,
                    ["requirements"] = alternatives,
                });
            }
            return result;
        }

        private static ModuleSpec Spec(string id, params Requirement[] alternatives) =>
            new ModuleSpec { Id = id, Alternatives = alternatives ?? Array.Empty<Requirement>() };

        private static Requirement Req(string packageId, string minimumVersion) =>
            new Requirement { PackageId = packageId, MinimumVersion = minimumVersion };

        private static bool VersionAtLeast(string actual, string minimum)
        {
            var left = VersionParts(actual);
            var right = VersionParts(minimum);
            for (int index = 0; index < Math.Max(left.Length, right.Length); index++)
            {
                var leftValue = index < left.Length ? left[index] : 0;
                var rightValue = index < right.Length ? right[index] : 0;
                if (leftValue != rightValue) return leftValue > rightValue;
            }
            return true;
        }

        private static int[] VersionParts(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<int>();
            var core = value.Split('-', '+')[0];
            return core.Split('.').Select(part =>
            {
                var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
                return int.TryParse(digits, out var number) ? number : 0;
            }).ToArray();
        }
    }
}
