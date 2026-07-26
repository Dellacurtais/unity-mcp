using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;
using UnityEngine.UIElements;

namespace Maicon.SceneAnalyzer.Editor.SystemTools
{
    internal static class SystemToolSupport
    {
        public static JObject EmptySchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };

        public static JObject TargetSchema(JObject extra = null, params string[] requiredExtra)
        {
            var properties = new JObject { ["instanceId"] = new JObject { ["type"] = "integer" } };
            if (extra != null)
                foreach (var property in extra.Properties()) properties[property.Name] = property.Value.DeepClone();
            var required = new JArray { "instanceId" };
            if (requiredExtra != null)
                foreach (var propertyName in requiredExtra) required.Add(propertyName);
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false,
            };
        }

        public static GameObject GameObject(JObject args, out ToolResult error)
        {
            error = null;
            var gameObject = ToolTargetResolver.ResolveGameObject(args, out var message);
            if (gameObject == null) error = ToolResult.Error("TARGET_NOT_FOUND", message);
            return gameObject;
        }

        public static JObject ObjectJson(UnityEngine.Object target) => new JObject
        {
            ["name"] = target != null ? target.name : null,
            ["type"] = target != null ? target.GetType().FullName : null,
            ["target"] = ToolTargetRef.Capture(target)?.ToJson(),
            ["assetPath"] = target != null ? AssetDatabase.GetAssetPath(target) : null,
        };

        public static JObject OutputSchema(JObject properties, params string[] required) => new JObject
        {
            ["type"] = "object",
            ["properties"] = properties ?? new JObject(),
            ["required"] = new JArray(required ?? Array.Empty<string>()),
            ["additionalProperties"] = true,
        };

        public static bool TryUiSourcePath(string path, out string normalized, out string absolute, out string error)
        {
            absolute = null;
            if (!TryAssetPath(path, null, out normalized, out error)
                || (!normalized.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase)
                    && !normalized.EndsWith(".uss", StringComparison.OrdinalIgnoreCase)))
            {
                error = error ?? "assetPath must be .uxml or .uss";
                return false;
            }

            absolute = Path.GetFullPath(Path.Combine(Bootstrap.UnityProjectRoot, normalized));
            var assetsRoot = Path.GetFullPath(Path.Combine(Bootstrap.UnityProjectRoot, "Assets"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
            {
                error = "assetPath must stay under Assets/";
                absolute = null;
                return false;
            }
            return true;
        }

        public static bool TryAssetPath(string path, string extension, out string normalized, out string error)
        {
            normalized = (path ?? string.Empty).Replace('\\', '/').Trim();
            error = null;
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("../") || normalized.EndsWith("/..", StringComparison.Ordinal))
            {
                error = "path must stay under Assets/";
                return false;
            }
            if (!string.IsNullOrEmpty(extension) && !normalized.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                error = $"path must end with {extension}";
                return false;
            }
            return true;
        }

        public static void EnsureFolder(string assetPath, List<string> createdFolders = null)
        {
            var folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            EnsureFolderPath(folder, createdFolders);
        }

        private static void EnsureFolderPath(string folder, List<string> createdFolders)
        {
            if (string.IsNullOrEmpty(folder) || folder == "Assets" || AssetDatabase.IsValidFolder(folder)) return;
            if (AssetDatabase.AssetPathExists(folder))
                throw new InvalidOperationException($"cannot create folder because an asset already exists at '{folder}'");
            var parent = Path.GetDirectoryName(folder)?.Replace('\\', '/') ?? "Assets";
            EnsureFolderPath(parent, createdFolders);
            var guid = AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
            if (string.IsNullOrEmpty(guid) && !AssetDatabase.IsValidFolder(folder))
                throw new InvalidOperationException($"could not create folder '{folder}'");
            createdFolders?.Add(folder);
        }

        public static void CleanupEmptyFolders(List<string> createdFolders)
        {
            if (createdFolders == null) return;
            for (var index = createdFolders.Count - 1; index >= 0; index--)
                if (AssetDatabase.IsValidFolder(createdFolders[index])
                    && AssetDatabase.FindAssets(string.Empty, new[] { createdFolders[index] }).Length == 0)
                    AssetDatabase.DeleteAsset(createdFolders[index]);
        }

        public static JObject Vec3(Vector3 value) => new JObject { ["x"] = value.x, ["y"] = value.y, ["z"] = value.z };
        public static JObject Vec2(Vector2 value) => new JObject { ["x"] = value.x, ["y"] = value.y };

        public static JObject ColorJson(Color value) => new JObject
        {
            ["r"] = value.r, ["g"] = value.g, ["b"] = value.b, ["a"] = value.a,
        };

        public static JArray VectorJson(Vector4 value) => new JArray(value.x, value.y, value.z, value.w);

        public static bool TryFiniteNumber(JToken token, out float value)
        {
            value = 0f;
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) return false;
            value = (float)token;
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public static bool TryFiniteVector2(JToken token, out Vector2 value)
        {
            value = Vector2.zero;
            if (token is JArray array && array.Count == 2
                && TryFiniteNumber(array[0], out var x)
                && TryFiniteNumber(array[1], out var y))
            {
                value = new Vector2(x, y);
                return true;
            }
            if (token is JObject obj
                && obj.Count == 2
                && obj["x"] != null && obj["y"] != null
                && TryFiniteNumber(obj["x"], out x)
                && TryFiniteNumber(obj["y"], out y))
            {
                value = new Vector2(x, y);
                return true;
            }
            return false;
        }

        public static bool TryFiniteVector3(JToken token, out Vector3 value)
        {
            value = Vector3.zero;
            if (token is JArray array && array.Count == 3
                && TryFiniteNumber(array[0], out var x)
                && TryFiniteNumber(array[1], out var y)
                && TryFiniteNumber(array[2], out var z))
            {
                value = new Vector3(x, y, z);
                return true;
            }
            if (token is JObject obj
                && obj.Count == 3
                && obj["x"] != null && obj["y"] != null && obj["z"] != null
                && TryFiniteNumber(obj["x"], out x)
                && TryFiniteNumber(obj["y"], out y)
                && TryFiniteNumber(obj["z"], out z))
            {
                value = new Vector3(x, y, z);
                return true;
            }
            return false;
        }

        public static bool TryColor(JToken token, out Color color, out string error)
        {
            color = default;
            error = null;
            if (token?.Type == JTokenType.String)
            {
                if (ColorUtility.TryParseHtmlString((string)token, out color)) return true;
                error = "Color string must use a Unity HTML color such as #RRGGBB or #RRGGBBAA";
                return false;
            }
            if (token is JArray array && (array.Count == 3 || array.Count == 4))
            {
                if (!TryFiniteNumber(array[0], out var r) || !TryFiniteNumber(array[1], out var g)
                    || !TryFiniteNumber(array[2], out var b)
                    || (array.Count == 4 && !TryFiniteNumber(array[3], out _)))
                {
                    error = "Color array values must be finite numbers";
                    return false;
                }
                var alpha = array.Count == 4 ? (float)array[3] : 1f;
                color = new Color(r, g, b, alpha);
                return true;
            }
            if (token is JObject obj
                && TryFiniteNumber(obj["r"], out var red)
                && TryFiniteNumber(obj["g"], out var green)
                && TryFiniteNumber(obj["b"], out var blue)
                && (obj["a"] == null || TryFiniteNumber(obj["a"], out _)))
            {
                color = new Color(red, green, blue, obj["a"] != null ? (float)obj["a"] : 1f);
                return true;
            }
            error = "Color requires #RRGGBB[AA], [r,g,b,a], or {r,g,b,a}";
            return false;
        }

        public static JToken MaterialPropertyValue(Material material, Shader shader, int index)
        {
            var name = shader.GetPropertyName(index);
            switch (shader.GetPropertyType(index))
            {
                case ShaderPropertyType.Color: return ColorJson(material.GetColor(name));
                case ShaderPropertyType.Vector: return VectorJson(material.GetVector(name));
                case ShaderPropertyType.Texture: return ObjectJson(material.GetTexture(name));
                case ShaderPropertyType.Int: return material.GetInteger(name);
                default: return material.GetFloat(name);
            }
        }

        public static JObject MaterialPropertyJson(Material material, Shader shader, int index)
        {
            var name = shader.GetPropertyName(index);
            var type = shader.GetPropertyType(index);
            var property = new JObject
            {
                ["name"] = name,
                ["description"] = shader.GetPropertyDescription(index),
                ["type"] = type.ToString(),
                ["flags"] = shader.GetPropertyFlags(index).ToString(),
                ["value"] = MaterialPropertyValue(material, shader, index),
            };
            if (type == ShaderPropertyType.Range)
            {
                var limits = shader.GetPropertyRangeLimits(index);
                property["rangeLimits"] = new JObject { ["min"] = limits.x, ["max"] = limits.y };
            }
            else if (type == ShaderPropertyType.Texture)
            {
                property["textureDimension"] = shader.GetPropertyTextureDimension(index).ToString();
                property["scale"] = Vec2(material.GetTextureScale(name));
                property["offset"] = Vec2(material.GetTextureOffset(name));
            }
            return property;
        }
    }

    public sealed class GetRenderPipelineContextTool : IMcpTool
    {
        public string Name => "get_render_pipeline_context";
        public string Description => "Reports the effective/default render pipeline, every quality-level override, color space, graphics API, and rendering capabilities.";
        public JObject InputSchema => SystemToolSupport.EmptySchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var current = GraphicsSettings.currentRenderPipeline;
            var defaultPipeline = GraphicsSettings.defaultRenderPipeline;
            var qualityPipeline = QualitySettings.renderPipeline;
            var activeQuality = QualitySettings.GetQualityLevel();
            var qualityNames = QualitySettings.names;
            var qualityLevels = new JArray();
            for (var index = 0; index < qualityNames.Length; index++)
            {
                var qualityOverride = QualitySettings.GetRenderPipelineAssetAt(index);
                var effective = qualityOverride != null ? qualityOverride : defaultPipeline;
                qualityLevels.Add(new JObject
                {
                    ["index"] = index,
                    ["name"] = qualityNames[index],
                    ["active"] = index == activeQuality,
                    ["override"] = SystemToolSupport.ObjectJson(qualityOverride),
                    ["pipeline"] = SystemToolSupport.ObjectJson(effective),
                    ["pipelineType"] = effective == null ? "BuiltIn" : effective.GetType().FullName,
                });
            }

            return ToolResult.Ok(current == null ? "Built-in Render Pipeline" : current.GetType().Name, new JObject
            {
                ["pipeline"] = current == null ? "BuiltIn" : current.GetType().FullName,
                ["current"] = SystemToolSupport.ObjectJson(current),
                ["default"] = SystemToolSupport.ObjectJson(defaultPipeline),
                ["qualityOverride"] = SystemToolSupport.ObjectJson(qualityPipeline),
                ["qualityLevel"] = activeQuality,
                ["qualityName"] = qualityNames.ElementAtOrDefault(activeQuality),
                ["qualityLevels"] = qualityLevels,
                ["colorSpace"] = QualitySettings.activeColorSpace.ToString(),
                ["graphicsDeviceType"] = SystemInfo.graphicsDeviceType.ToString(),
                ["graphicsDeviceName"] = SystemInfo.graphicsDeviceName,
                ["graphicsMemorySizeMb"] = SystemInfo.graphicsMemorySize,
                ["supportsComputeShaders"] = SystemInfo.supportsComputeShaders,
                ["supportsInstancing"] = SystemInfo.supportsInstancing,
                ["supportsRayTracing"] = SystemInfo.supportsRayTracing,
            });
        }
    }

    public sealed class ListShadersTool : IMcpTool
    {
        public string Name => "list_shaders";
        public string Description => "Lists project Shader assets with pagination and optional name filtering.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["contains"] = new JObject { ["type"] = "string" },
                ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 2000, ["default"] = 200 },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var contains = (string)args["contains"] ?? string.Empty;
            var shaders = AssetDatabase.FindAssets("t:Shader")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(path => new { Path = path, Shader = AssetDatabase.LoadAssetAtPath<Shader>(path) })
                .Where(entry => entry.Shader != null)
                .Where(entry => entry.Shader.name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0
                    || entry.Path.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(entry => entry.Path, StringComparer.Ordinal)
                .ToArray();
            int offset = Math.Min((int?)args["offset"] ?? 0, shaders.Length);
            int limit = (int?)args["limit"] ?? 200;
            var items = new JArray(shaders.Skip(offset).Take(limit).Select(entry =>
            {
                var item = SystemToolSupport.ObjectJson(entry.Shader);
                item["supported"] = entry.Shader.isSupported;
                return item;
            }));
            return ToolResult.Ok($"{shaders.Length} shader(s)", new JObject
            {
                ["shaders"] = items, ["offset"] = offset, ["count"] = items.Count,
                ["total"] = shaders.Length, ["truncated"] = offset + items.Count < shaders.Length,
            });
        }
    }

    public sealed class ReadMaterialTool : IMcpTool
    {
        public string Name => "read_material";
        public string Description => "Reads a Material asset and all properties declared by its Shader using typed values.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["assetPath"] = new JObject { ["type"] = "string" } },
            ["required"] = new JArray { "assetPath" }, ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>((string)args["assetPath"]);
            if (material == null) return ToolResult.Error("MATERIAL_NOT_FOUND", "Material asset not found");
            var properties = new JArray();
            var shader = material.shader;
            for (int index = 0; shader != null && index < shader.GetPropertyCount(); index++)
                properties.Add(SystemToolSupport.MaterialPropertyJson(material, shader, index));
            return ToolResult.Ok($"material '{material.name}'", new JObject
            {
                ["material"] = SystemToolSupport.ObjectJson(material),
                ["shader"] = SystemToolSupport.ObjectJson(shader),
                ["renderQueue"] = material.renderQueue,
                ["enableInstancing"] = material.enableInstancing,
                ["keywords"] = new JArray(material.shaderKeywords),
                ["properties"] = properties,
            });
        }
    }

    public sealed class CreateMaterialTool : IMcpTool
    {
        public string Name => "create_material";
        public string Description => "Creates and persists a Material asset for an exact Shader name without saving unrelated assets.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["assetPath"] = new JObject { ["type"] = "string" },
                ["shaderName"] = new JObject { ["type"] = "string" },
            },
            ["required"] = new JArray { "assetPath", "shaderName" }, ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            if (!SystemToolSupport.TryAssetPath((string)args["assetPath"], ".mat", out var path, out var error))
                return ToolResult.Error("BAD_ARGS", error);
            if (AssetDatabase.LoadMainAssetAtPath(path) != null) return ToolResult.Error("ASSET_EXISTS", path);
            var shader = Shader.Find((string)args["shaderName"]);
            if (shader == null) return ToolResult.Error("SHADER_NOT_FOUND", (string)args["shaderName"]);
            try
            {
                SystemToolSupport.EnsureFolder(path);
                var material = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
                AssetDatabase.CreateAsset(material, path);
                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssetIfDirty(material);
                return ToolResult.Ok($"created material '{path}'", new JObject
                {
                    ["material"] = SystemToolSupport.ObjectJson(material),
                    ["assetCreated"] = true,
                    ["savedAssets"] = true,
                    ["undoRegistered"] = false,
                });
            }
            catch (Exception exception) { return ToolResult.Error("CREATE_MATERIAL_FAILED", exception.Message); }
        }
    }

    public sealed class SetMaterialPropertyTypedTool : IMcpTool
    {
        public string Name => "set_material_property_typed";
        public string Description => "Sets one Shader property on a Material asset under Assets/ with Undo and type validation; does not save the asset.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["assetPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["propertyName"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["value"] = new JObject(),
            },
            ["required"] = new JArray { "assetPath", "propertyName", "value" }, ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            if (!SystemToolSupport.TryAssetPath((string)args["assetPath"], ".mat", out var assetPath, out var pathError))
                return ToolResult.Error("BAD_ASSET_PATH", pathError);
            var material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (material == null) return ToolResult.Error("MATERIAL_NOT_FOUND", "Material asset not found");
            var name = (string)args["propertyName"];
            if (!material.HasProperty(name)) return ToolResult.Error("PROPERTY_NOT_FOUND", name);
            int index = material.shader.FindPropertyIndex(name);
            var type = material.shader.GetPropertyType(index);
            var token = args["value"];
            Color color = default;
            Vector4 vector = default;
            Texture texture = null;
            float number = 0f;
            int integer = 0;
            string validationError = null;
            try
            {
                switch (type)
                {
                    case ShaderPropertyType.Color:
                        if (!SystemToolSupport.TryColor(token, out color, out validationError))
                            return ToolResult.Error("BAD_VALUE", validationError);
                        break;
                    case ShaderPropertyType.Vector:
                        var array = token as JArray;
                        if (array == null || array.Count != 4
                            || !SystemToolSupport.TryFiniteNumber(array[0], out var x)
                            || !SystemToolSupport.TryFiniteNumber(array[1], out var y)
                            || !SystemToolSupport.TryFiniteNumber(array[2], out var z)
                            || !SystemToolSupport.TryFiniteNumber(array[3], out var w))
                            return ToolResult.Error("BAD_VALUE", "Vector requires exactly four finite numbers: [x,y,z,w]");
                        vector = new Vector4(x, y, z, w);
                        break;
                    case ShaderPropertyType.Texture:
                        if (token.Type != JTokenType.Null)
                        {
                            var reference = ToolTargetRef.FromJson(token);
                            texture = ToolTargetResolver.Resolve(reference, out var resolveError) as Texture;
                            if (texture == null && token.Type == JTokenType.String)
                                texture = AssetDatabase.LoadAssetAtPath<Texture>((string)token);
                            if (texture == null) return ToolResult.Error("BAD_VALUE", resolveError ?? "Texture not found");
                        }
                        break;
                    case ShaderPropertyType.Int:
                        if (token.Type != JTokenType.Integer)
                            return ToolResult.Error("BAD_VALUE", "Integer property requires an integer JSON value");
                        integer = (int)token;
                        break;
                    case ShaderPropertyType.Range:
                        if (!SystemToolSupport.TryFiniteNumber(token, out number))
                            return ToolResult.Error("BAD_VALUE", "Range property requires a finite number");
                        var limits = material.shader.GetPropertyRangeLimits(index);
                        if (number < limits.x || number > limits.y)
                            return ToolResult.Error("BAD_VALUE", $"Range property requires a value between {limits.x} and {limits.y}");
                        break;
                    default:
                        if (!SystemToolSupport.TryFiniteNumber(token, out number))
                            return ToolResult.Error("BAD_VALUE", "Float property requires a finite number");
                        break;
                }

                using (var mutation = new MutationScope("Set Material Property"))
                {
                    mutation.Record(material);
                    switch (type)
                    {
                        case ShaderPropertyType.Color: material.SetColor(name, color); break;
                        case ShaderPropertyType.Vector: material.SetVector(name, vector); break;
                        case ShaderPropertyType.Texture: material.SetTexture(name, texture); break;
                        case ShaderPropertyType.Int: material.SetInteger(name, integer); break;
                        default: material.SetFloat(name, number); break;
                    }
                    mutation.Complete();
                }
                return ToolResult.Ok($"set {name}", new JObject
                {
                    ["material"] = SystemToolSupport.ObjectJson(material),
                    ["propertyType"] = type.ToString(),
                    ["effectiveValue"] = SystemToolSupport.MaterialPropertyValue(material, material.shader, index),
                    ["savedAssets"] = false,
                });
            }
            catch (Exception exception) { return ToolResult.Error("BAD_VALUE", exception.Message); }
        }
    }

    public sealed class GetLightingContextTool : IMcpTool
    {
        public string Name => "get_lighting_context";
        public string Description => "Reports active-scene RenderSettings plus loaded scene lights, reflection probes, light probe groups, and bake state.";
        public JObject InputSchema => SystemToolSupport.EmptySchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var activeScene = SceneManager.GetActiveScene();
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(light => light.gameObject.scene.IsValid() && light.gameObject.scene.isLoaded)
                .OrderBy(light => (int)light.gameObject.scene.handle)
                .ThenBy(light => light.name, StringComparer.Ordinal)
                .ToArray();
            var reflectionProbes = UnityEngine.Object.FindObjectsByType<ReflectionProbe>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(probe => probe.gameObject.scene.IsValid() && probe.gameObject.scene.isLoaded)
                .OrderBy(probe => (int)probe.gameObject.scene.handle)
                .ThenBy(probe => probe.name, StringComparer.Ordinal)
                .ToArray();
            var lightProbeGroups = UnityEngine.Object.FindObjectsByType<LightProbeGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(group => group.gameObject.scene.IsValid() && group.gameObject.scene.isLoaded)
                .OrderBy(group => (int)group.gameObject.scene.handle)
                .ThenBy(group => group.name, StringComparer.Ordinal)
                .ToArray();

            return ToolResult.Ok($"{lights.Length} light(s)", new JObject
            {
                ["activeScene"] = new JObject
                {
                    ["name"] = activeScene.name,
                    ["path"] = activeScene.path,
                    ["handle"] = (int)activeScene.handle,
                    ["dirty"] = activeScene.isDirty,
                },
                ["ambient"] = new JObject
                {
                    ["mode"] = RenderSettings.ambientMode.ToString(),
                    ["intensity"] = RenderSettings.ambientIntensity,
                    ["light"] = new JObject { ["r"] = RenderSettings.ambientLight.r, ["g"] = RenderSettings.ambientLight.g, ["b"] = RenderSettings.ambientLight.b, ["a"] = RenderSettings.ambientLight.a },
                    ["skyColor"] = new JObject { ["r"] = RenderSettings.ambientSkyColor.r, ["g"] = RenderSettings.ambientSkyColor.g, ["b"] = RenderSettings.ambientSkyColor.b, ["a"] = RenderSettings.ambientSkyColor.a },
                    ["equatorColor"] = new JObject { ["r"] = RenderSettings.ambientEquatorColor.r, ["g"] = RenderSettings.ambientEquatorColor.g, ["b"] = RenderSettings.ambientEquatorColor.b, ["a"] = RenderSettings.ambientEquatorColor.a },
                    ["groundColor"] = new JObject { ["r"] = RenderSettings.ambientGroundColor.r, ["g"] = RenderSettings.ambientGroundColor.g, ["b"] = RenderSettings.ambientGroundColor.b, ["a"] = RenderSettings.ambientGroundColor.a },
                },
                ["fog"] = new JObject
                {
                    ["enabled"] = RenderSettings.fog,
                    ["mode"] = RenderSettings.fogMode.ToString(),
                    ["color"] = new JObject { ["r"] = RenderSettings.fogColor.r, ["g"] = RenderSettings.fogColor.g, ["b"] = RenderSettings.fogColor.b, ["a"] = RenderSettings.fogColor.a },
                    ["density"] = RenderSettings.fogDensity,
                    ["startDistance"] = RenderSettings.fogStartDistance,
                    ["endDistance"] = RenderSettings.fogEndDistance,
                },
                ["skybox"] = SystemToolSupport.ObjectJson(RenderSettings.skybox),
                ["sun"] = SystemToolSupport.ObjectJson(RenderSettings.sun),
                ["reflectionIntensity"] = RenderSettings.reflectionIntensity,
                ["lightmappingRunning"] = Lightmapping.isRunning,
                ["lightCount"] = lights.Length,
                ["reflectionProbeCount"] = reflectionProbes.Length,
                ["lightProbeGroupCount"] = lightProbeGroups.Length,
                ["lights"] = new JArray(lights.Select(light => new JObject
                {
                    ["target"] = ToolTargetRef.Capture(light)?.ToJson(),
                    ["name"] = light.name,
                    ["scene"] = light.gameObject.scene.name,
                    ["enabled"] = light.enabled,
                    ["activeInHierarchy"] = light.gameObject.activeInHierarchy,
                    ["type"] = light.type.ToString(),
                    ["color"] = new JObject { ["r"] = light.color.r, ["g"] = light.color.g, ["b"] = light.color.b, ["a"] = light.color.a },
                    ["intensity"] = light.intensity,
                    ["range"] = light.range,
                    ["spotAngle"] = light.spotAngle,
                    ["innerSpotAngle"] = light.innerSpotAngle,
                    ["areaSize"] = SystemToolSupport.Vec2(light.areaSize),
                    ["shadows"] = light.shadows.ToString(),
                    ["mode"] = light.lightmapBakeType.ToString(),
                    ["cullingMask"] = light.cullingMask,
                    ["position"] = SystemToolSupport.Vec3(light.transform.position),
                    ["rotationEuler"] = SystemToolSupport.Vec3(light.transform.eulerAngles),
                })),
                ["reflectionProbes"] = new JArray(reflectionProbes.Select(probe => new JObject
                {
                    ["target"] = ToolTargetRef.Capture(probe)?.ToJson(),
                    ["name"] = probe.name,
                    ["scene"] = probe.gameObject.scene.name,
                    ["enabled"] = probe.enabled,
                    ["mode"] = probe.mode.ToString(),
                    ["refreshMode"] = probe.refreshMode.ToString(),
                    ["resolution"] = probe.resolution,
                    ["size"] = SystemToolSupport.Vec3(probe.size),
                    ["blendDistance"] = probe.blendDistance,
                })),
                ["lightProbeGroups"] = new JArray(lightProbeGroups.Select(group => new JObject
                {
                    ["target"] = ToolTargetRef.Capture(group)?.ToJson(),
                    ["name"] = group.name,
                    ["scene"] = group.gameObject.scene.name,
                    ["enabled"] = group.enabled,
                    ["probeCount"] = group.probePositions?.Length ?? 0,
                })),
            });
        }
    }

    public sealed class ListLodGroupsTool : IMcpTool
    {
        public string Name => "list_lod_groups";
        public string Description => "Lists loaded LODGroups, thresholds and renderer target references.";
        public JObject InputSchema => SystemToolSupport.EmptySchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var groups = UnityEngine.Object.FindObjectsByType<LODGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var result = new JArray(groups.Select(group => new JObject
            {
                ["target"] = ToolTargetRef.Capture(group)?.ToJson(),
                ["size"] = group.size, ["fadeMode"] = group.fadeMode.ToString(),
                ["lods"] = new JArray(group.GetLODs().Select((lod, index) => new JObject
                {
                    ["index"] = index, ["screenRelativeTransitionHeight"] = lod.screenRelativeTransitionHeight,
                    ["fadeTransitionWidth"] = lod.fadeTransitionWidth,
                    ["renderers"] = new JArray(lod.renderers.Where(renderer => renderer != null).Select(SystemToolSupport.ObjectJson)),
                })),
            }));
            return ToolResult.Ok($"{groups.Length} LODGroup(s)", new JObject { ["groups"] = result });
        }
    }

    public sealed class ConfigureLodGroupTool : IMcpTool
    {
        public string Name => "configure_lod_group";
        public string Description => "Replaces one LODGroup's levels using explicit renderer instance IDs and recalculates bounds.";
        public JObject InputSchema => SystemToolSupport.TargetSchema(new JObject
        {
            ["levels"] = new JObject
            {
                ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 16,
                ["items"] = new JObject
                {
                    ["type"] = "object", ["properties"] = new JObject
                    {
                        ["screenRelativeTransitionHeight"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 },
                        ["fadeTransitionWidth"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 },
                        ["rendererInstanceIds"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" } },
                    },
                    ["required"] = new JArray { "screenRelativeTransitionHeight", "rendererInstanceIds" }, ["additionalProperties"] = false,
                },
            },
        }, "levels");
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var gameObject = SystemToolSupport.GameObject(args, out var error);
            if (gameObject == null) return error;
            var levels = new List<LOD>();
            float previousThreshold = 1.0001f;
            foreach (var token in (JArray)args["levels"])
            {
                var item = (JObject)token;
                var threshold = (float)item["screenRelativeTransitionHeight"];
                if (threshold >= previousThreshold)
                    return ToolResult.Error("BAD_LOD_ORDER", "LOD transition heights must be strictly descending");
                previousThreshold = threshold;
                var rendererIds = ((JArray)item["rendererInstanceIds"]).Values<int>().ToArray();
                var renderers = new Renderer[rendererIds.Length];
                for (int index = 0; index < rendererIds.Length; index++)
                {
                    renderers[index] = EditorUtility.InstanceIDToObject(rendererIds[index]) as Renderer;
                    if (renderers[index] == null)
                        return ToolResult.Error("RENDERER_NOT_FOUND", $"rendererInstanceIds[{index}]={rendererIds[index]}");
                }
                var lod = new LOD(threshold, renderers)
                {
                    fadeTransitionWidth = (float?)item["fadeTransitionWidth"] ?? 0f,
                };
                levels.Add(lod);
            }
            LODGroup group;
            using (var mutation = new MutationScope("Configure LODGroup"))
            {
                group = gameObject.GetComponent<LODGroup>();
                if (group == null) group = mutation.AddComponent<LODGroup>(gameObject);
                else mutation.Record(group);
                group.SetLODs(levels.ToArray());
                group.RecalculateBounds();
                mutation.Complete();
            }
            return ToolResult.Ok($"configured {levels.Count} LOD level(s)", new JObject { ["target"] = ToolTargetRef.Capture(group)?.ToJson() });
        }
    }

    public sealed class SetStaticEditorFlagsTool : IMcpTool
    {
        public string Name => "set_static_editor_flags";
        public string Description => "Sets Unity StaticEditorFlags on a GameObject with Undo.";
        public JObject InputSchema => SystemToolSupport.TargetSchema(new JObject
        {
            ["flags"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["maxItems"] = 16 },
        }, "flags");
        public bool IsWrite => true;
        public JObject OutputSchema => GameObjectAuthoringSchemas.StaticFlagsResult;
        public ToolResult Execute(JObject args)
        {
            var gameObject = SystemToolSupport.GameObject(args, out var error);
            if (gameObject == null) return error;
            StaticEditorFlags flags = 0;
            foreach (var text in ((JArray)args["flags"]).Values<string>())
            {
                if (!Enum.TryParse(text, true, out StaticEditorFlags parsed)) return ToolResult.Error("BAD_FLAG", text);
                flags |= parsed;
            }
            Undo.RecordObject(gameObject, "Set Static Flags");
            GameObjectUtility.SetStaticEditorFlags(gameObject, flags);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return ToolResult.Ok($"static flags = {flags}", new JObject { ["target"] = ToolTargetRef.Capture(gameObject)?.ToJson(), ["flags"] = flags.ToString() });
        }
    }

    public sealed class Physics2DQueryTool : IMcpTool
    {
        public string Name => "physics2d_query";
        public string Description => "Runs a read-only Physics2D raycast, overlap point, overlap circle or overlap box query and returns every matching collider in deterministic order.";
        public JObject InputSchema => new JObject
        {
            ["oneOf"] = new JArray
            {
                Variant("raycast", new JObject
                {
                    ["direction"] = SchemaHelpers.StrictVec2Schema,
                    ["distance"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["default"] = 1000 },
                }, "direction"),
                Variant("overlap_point", new JObject()),
                Variant("overlap_circle", new JObject
                {
                    ["radius"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                }, "radius"),
                Variant("overlap_box", new JObject
                {
                    ["size"] = SchemaHelpers.NonNegativeVec2Schema,
                    ["angle"] = new JObject { ["type"] = "number", ["default"] = 0 },
                }, "size"),
            },
        };
        public bool IsWrite => false;
        public JObject OutputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["kind"] = new JObject { ["type"] = "string" },
                ["count"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["hits"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            },
            ["required"] = new JArray { "kind", "count", "hits" },
        };

        public ToolResult Execute(JObject args)
        {
            var kind = ((string)args["kind"] ?? string.Empty).Trim().ToLowerInvariant();
            if (!SystemToolSupport.TryFiniteVector2(args["origin"], out var origin))
                return ToolResult.Error("INVALID_ARGUMENT", "origin must be a finite Vector2 [x,y] or {x,y}");
            if (!TryLayerMask(args["layerMask"], out var mask, out var maskError))
                return ToolResult.Error("INVALID_ARGUMENT", maskError);

            var filter = ContactFilter2D.noFilter;
            filter.SetLayerMask(mask);
            filter.useTriggers = Physics2D.queriesHitTriggers;
            Physics2D.SyncTransforms();

            var hits = new JArray();
            switch (kind)
            {
                case "raycast":
                    if (!SystemToolSupport.TryFiniteVector2(args["direction"], out var direction) || direction.sqrMagnitude <= 1e-12f)
                        return ToolResult.Error("INVALID_ARGUMENT", "direction must be a finite non-zero Vector2");
                    var distance = 1000f;
                    if (args["distance"] != null
                        && (!SystemToolSupport.TryFiniteNumber(args["distance"], out distance) || distance < 0f))
                        return ToolResult.Error("INVALID_ARGUMENT", "distance must be a non-negative finite number");
                    var rayHits = new List<RaycastHit2D>();
                    Physics2D.defaultPhysicsScene.Raycast(origin, direction.normalized, distance, filter, rayHits);
                    rayHits.Sort(CompareRayHits);
                    foreach (var hit in rayHits)
                    {
                        hits.Add(new JObject
                        {
                            ["collider"] = SystemToolSupport.ObjectJson(hit.collider),
                            ["point"] = SystemToolSupport.Vec2(hit.point),
                            ["normal"] = SystemToolSupport.Vec2(hit.normal),
                            ["distance"] = hit.distance,
                            ["fraction"] = hit.fraction,
                        });
                    }
                    break;
                case "overlap_point":
                    AddColliders(OverlapPoint(origin, filter), hits);
                    break;
                case "overlap_circle":
                    if (!SystemToolSupport.TryFiniteNumber(args["radius"], out var radius) || radius < 0f)
                        return ToolResult.Error("INVALID_ARGUMENT", "radius must be a non-negative finite number");
                    AddColliders(OverlapCircle(origin, radius, filter), hits);
                    break;
                case "overlap_box":
                    if (!SystemToolSupport.TryFiniteVector2(args["size"], out var size) || size.x < 0f || size.y < 0f)
                        return ToolResult.Error("INVALID_ARGUMENT", "size must be a non-negative finite Vector2");
                    var angle = 0f;
                    if (args["angle"] != null && !SystemToolSupport.TryFiniteNumber(args["angle"], out angle))
                        return ToolResult.Error("INVALID_ARGUMENT", "angle must be finite");
                    AddColliders(OverlapBox(origin, size, angle, filter), hits);
                    break;
                default:
                    return ToolResult.Error("INVALID_ARGUMENT", $"unknown kind '{kind}'");
            }

            return ToolResult.Ok($"{hits.Count} hit(s)", new JObject
            {
                ["kind"] = kind,
                ["count"] = hits.Count,
                ["hits"] = hits,
            });
        }

        private static JObject Variant(string kind, JObject extraProperties, params string[] requiredExtra)
        {
            var properties = new JObject
            {
                ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray { kind } },
                ["origin"] = SchemaHelpers.StrictVec2Schema,
                ["layerMask"] = SchemaHelpers.LayerMaskSchema,
            };
            foreach (var property in extraProperties.Properties()) properties[property.Name] = property.Value;
            var required = new JArray { "kind", "origin" };
            foreach (var property in requiredExtra) required.Add(property);
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false,
            };
        }

        private static bool TryLayerMask(JToken token, out int mask, out string error)
        {
            mask = Physics2D.AllLayers;
            error = null;
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token.Type == JTokenType.Integer)
            {
                if (!long.TryParse(token.ToString(), out var rawMask) || rawMask < int.MinValue || rawMask > int.MaxValue)
                {
                    error = "layerMask integer must be between -2147483648 and 2147483647";
                    return false;
                }
                mask = (int)rawMask;
                return true;
            }
            if (token.Type == JTokenType.String)
            {
                var name = ((string)token ?? string.Empty).Trim();
                var layer = LayerMask.NameToLayer(name);
                if (layer < 0)
                {
                    error = $"unknown layer '{name}'";
                    return false;
                }
                mask = 1 << layer;
                return true;
            }
            if (token is JArray names && names.Count > 0)
            {
                mask = 0;
                foreach (var item in names)
                {
                    if (item.Type != JTokenType.String)
                    {
                        error = "layerMask arrays must contain only layer names";
                        return false;
                    }
                    var name = ((string)item ?? string.Empty).Trim();
                    var layer = LayerMask.NameToLayer(name);
                    if (layer < 0)
                    {
                        error = $"unknown layer '{name}'";
                        return false;
                    }
                    mask |= 1 << layer;
                }
                return true;
            }
            error = "layerMask must be an integer, layer name, or non-empty array of layer names";
            return false;
        }

        private static List<Collider2D> OverlapPoint(Vector2 point, ContactFilter2D filter)
        {
            var results = new List<Collider2D>();
            Physics2D.defaultPhysicsScene.OverlapPoint(point, filter, results);
            return results;
        }

        private static List<Collider2D> OverlapCircle(Vector2 point, float radius, ContactFilter2D filter)
        {
            var results = new List<Collider2D>();
            Physics2D.defaultPhysicsScene.OverlapCircle(point, radius, filter, results);
            return results;
        }

        private static List<Collider2D> OverlapBox(Vector2 point, Vector2 size, float angle, ContactFilter2D filter)
        {
            var results = new List<Collider2D>();
            Physics2D.defaultPhysicsScene.OverlapBox(point, size, angle, filter, results);
            return results;
        }

        private static void AddColliders(List<Collider2D> colliders, JArray hits)
        {
            colliders.Sort((left, right) =>
            {
                var name = string.Compare(left != null ? left.gameObject.name : string.Empty, right != null ? right.gameObject.name : string.Empty, StringComparison.Ordinal);
                if (name != 0) return name;
                return (left != null ? left.GetInstanceID() : 0).CompareTo(right != null ? right.GetInstanceID() : 0);
            });
            foreach (var collider in colliders) hits.Add(SystemToolSupport.ObjectJson(collider));
        }

        private static int CompareRayHits(RaycastHit2D left, RaycastHit2D right)
        {
            var distance = left.distance.CompareTo(right.distance);
            if (distance != 0) return distance;
            return (left.collider != null ? left.collider.GetInstanceID() : 0)
                .CompareTo(right.collider != null ? right.collider.GetInstanceID() : 0);
        }
    }

    public sealed class GetPhysics2DSettingsTool : IMcpTool
    {
        public string Name => "get_physics2d_settings";
        public string Description => "Reads global Physics2D settings and counts loaded Rigidbody2D/Collider2D components.";
        public JObject InputSchema => SystemToolSupport.EmptySchema();
        public bool IsWrite => false;
        public JObject OutputSchema => CreateOutputSchema(false);
        internal static JObject CreateOutputSchema(bool includeMutationMetadata)
        {
            var properties = new JObject
            {
                ["gravity"] = SchemaHelpers.StrictVec2Schema,
                ["velocityIterations"] = new JObject { ["type"] = "integer", ["minimum"] = 1 },
                ["positionIterations"] = new JObject { ["type"] = "integer", ["minimum"] = 1 },
                ["queriesHitTriggers"] = new JObject { ["type"] = "boolean" },
                ["queriesStartInColliders"] = new JObject { ["type"] = "boolean" },
                ["rigidbodyCount"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                ["colliderCount"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
            };
            var required = new JArray
            {
                "gravity", "velocityIterations", "positionIterations",
                "queriesHitTriggers", "queriesStartInColliders", "rigidbodyCount", "colliderCount",
            };
            if (includeMutationMetadata)
            {
                properties["persistedImmediately"] = new JObject { ["type"] = "boolean" };
                properties["undoRegistered"] = new JObject { ["type"] = "boolean" };
                properties["changedKeys"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } };
                required.Add("persistedImmediately");
                required.Add("undoRegistered");
                required.Add("changedKeys");
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false,
            };
        }
        public ToolResult Execute(JObject args) => ToolResult.Ok("Physics2D settings", new JObject
        {
            ["gravity"] = SystemToolSupport.Vec2(Physics2D.gravity),
            ["velocityIterations"] = Physics2D.velocityIterations,
            ["positionIterations"] = Physics2D.positionIterations,
            ["queriesHitTriggers"] = Physics2D.queriesHitTriggers,
            ["queriesStartInColliders"] = Physics2D.queriesStartInColliders,
            ["rigidbodyCount"] = UnityEngine.Object.FindObjectsByType<Rigidbody2D>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
            ["colliderCount"] = UnityEngine.Object.FindObjectsByType<Collider2D>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
        });
    }

    public sealed class SetPhysics2DSettingsTool : IMcpTool
    {
        public string Name => "set_physics2d_settings";
        public string Description => "Sets selected Physics2D project settings. The validated batch persists immediately and is not registered with Undo.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["gravity"] = SchemaHelpers.StrictVec2Schema,
                ["velocityIterations"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100 },
                ["positionIterations"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100 },
                ["queriesHitTriggers"] = new JObject { ["type"] = "boolean" },
                ["queriesStartInColliders"] = new JObject { ["type"] = "boolean" },
            }, ["minProperties"] = 1, ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public JObject OutputSchema => GetPhysics2DSettingsTool.CreateOutputSchema(true);
        public ToolResult Execute(JObject args)
        {
            if (args == null || !args.Properties().Any())
                return ToolResult.Error("INVALID_ARGUMENT", "at least one Physics2D setting is required");

            Vector2? gravity = null;
            int? velocityIterations = null;
            int? positionIterations = null;
            bool? queriesHitTriggers = null;
            bool? queriesStartInColliders = null;

            if (args["gravity"] != null)
            {
                if (!SystemToolSupport.TryFiniteVector2(args["gravity"], out var value))
                    return ToolResult.Error("INVALID_ARGUMENT", "gravity must be a finite Vector2 [x,y] or {x,y}");
                gravity = value;
            }
            if (args["velocityIterations"] != null)
            {
                if (args["velocityIterations"].Type != JTokenType.Integer)
                    return ToolResult.Error("INVALID_ARGUMENT", "velocityIterations must be an integer between 1 and 100");
                var value = (int)args["velocityIterations"];
                if (value < 1 || value > 100)
                    return ToolResult.Error("INVALID_ARGUMENT", "velocityIterations must be between 1 and 100");
                velocityIterations = value;
            }
            if (args["positionIterations"] != null)
            {
                if (args["positionIterations"].Type != JTokenType.Integer)
                    return ToolResult.Error("INVALID_ARGUMENT", "positionIterations must be an integer between 1 and 100");
                var value = (int)args["positionIterations"];
                if (value < 1 || value > 100)
                    return ToolResult.Error("INVALID_ARGUMENT", "positionIterations must be between 1 and 100");
                positionIterations = value;
            }
            if (args["queriesHitTriggers"] != null)
            {
                if (args["queriesHitTriggers"].Type != JTokenType.Boolean)
                    return ToolResult.Error("INVALID_ARGUMENT", "queriesHitTriggers must be a boolean");
                queriesHitTriggers = (bool)args["queriesHitTriggers"];
            }
            if (args["queriesStartInColliders"] != null)
            {
                if (args["queriesStartInColliders"].Type != JTokenType.Boolean)
                    return ToolResult.Error("INVALID_ARGUMENT", "queriesStartInColliders must be a boolean");
                queriesStartInColliders = (bool)args["queriesStartInColliders"];
            }

            if (gravity.HasValue) Physics2D.gravity = gravity.Value;
            if (velocityIterations.HasValue) Physics2D.velocityIterations = velocityIterations.Value;
            if (positionIterations.HasValue) Physics2D.positionIterations = positionIterations.Value;
            if (queriesHitTriggers.HasValue) Physics2D.queriesHitTriggers = queriesHitTriggers.Value;
            if (queriesStartInColliders.HasValue) Physics2D.queriesStartInColliders = queriesStartInColliders.Value;

            var result = new GetPhysics2DSettingsTool().Execute(new JObject());
            if (result.Json is JObject payload)
            {
                payload["persistedImmediately"] = true;
                payload["undoRegistered"] = false;
                payload["changedKeys"] = new JArray(args.Properties().Select(property => property.Name));
            }
            return result;
        }
    }

    public sealed class ListSpritesTool : IMcpTool
    {
        public string Name => "list_sprites";
        public string Description => "Lists Sprite sub-assets, rects, pivots, borders and source texture paths.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["folder"] = new JObject { ["type"] = "string", ["default"] = "Assets" },
                ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 5000, ["default"] = 500 },
            }, ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public JObject OutputSchema => SystemToolSupport.OutputSchema(new JObject
        {
            ["sprites"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            ["total"] = new JObject { ["type"] = "integer" },
            ["offset"] = new JObject { ["type"] = "integer" },
            ["limit"] = new JObject { ["type"] = "integer" },
            ["truncated"] = new JObject { ["type"] = "boolean" },
        }, "sprites", "total", "offset", "limit", "truncated");
        public ToolResult Execute(JObject args)
        {
            var folder = ((string)args["folder"] ?? "Assets").Replace('\\', '/').Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(folder)) folder = "Assets";
            if (!(string.Equals(folder, "Assets", StringComparison.OrdinalIgnoreCase)
                || folder.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                || !AssetDatabase.IsValidFolder(folder))
                return ToolResult.Error("FOLDER_NOT_FOUND", folder);

            var offset = (int?)args["offset"] ?? 0;
            var limit = (int?)args["limit"] ?? 500;
            if (offset < 0 || limit < 1 || limit > 5000)
                return ToolResult.Error("BAD_PAGINATION", "offset must be non-negative and limit must be between 1 and 5000");
            var sprites = AssetDatabase.FindAssets("t:Sprite", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct(StringComparer.Ordinal)
                .SelectMany(path => AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().Select(sprite =>
                {
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out _, out long localFileId);
                    return new { Sprite = sprite, Path = path, LocalFileId = localFileId };
                }))
                .OrderBy(entry => entry.Path, StringComparer.Ordinal)
                .ThenBy(entry => entry.LocalFileId)
                .ToArray();
            var page = sprites.Skip(offset).Take(limit).ToArray();
            return ToolResult.Ok($"{page.Length} of {sprites.Length} sprite(s)", new JObject
            {
                ["sprites"] = new JArray(page.Select(entry => new JObject
                {
                    ["sprite"] = SystemToolSupport.ObjectJson(entry.Sprite),
                    ["sourceTexturePath"] = entry.Path,
                    ["localFileId"] = entry.LocalFileId,
                    ["rect"] = new JObject
                    { ["x"] = entry.Sprite.rect.x, ["y"] = entry.Sprite.rect.y, ["width"] = entry.Sprite.rect.width, ["height"] = entry.Sprite.rect.height },
                    ["pivot"] = SystemToolSupport.Vec2(entry.Sprite.pivot), ["pixelsPerUnit"] = entry.Sprite.pixelsPerUnit,
                    ["border"] = new JArray(entry.Sprite.border.x, entry.Sprite.border.y, entry.Sprite.border.z, entry.Sprite.border.w),
                })),
                ["total"] = sprites.Length,
                ["offset"] = offset,
                ["limit"] = limit,
                ["truncated"] = offset + page.Length < sprites.Length,
            });
        }
    }

    public sealed class CreateTilemapTool : IMcpTool
    {
        public string Name => "create_tilemap";
        public string Description => "Creates a Grid and child Tilemap/TilemapRenderer in the selected scene with Undo.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["name"] = new JObject { ["type"] = "string", ["default"] = "Tilemap" },
                ["parentInstanceId"] = new JObject { ["type"] = "integer" },
            }, ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public JObject OutputSchema => SystemToolSupport.OutputSchema(new JObject
        {
            ["grid"] = new JObject { ["type"] = "object" },
            ["tilemap"] = new JObject { ["type"] = "object" },
        }, "grid", "tilemap");
        public ToolResult Execute(JObject args)
        {
            GameObject parent = null;
            if (args["parentInstanceId"] != null)
            {
                parent = EditorUtility.EntityIdToObject((EntityId)(int)args["parentInstanceId"]) as GameObject;
                if (parent == null) return ToolResult.Error("PARENT_NOT_FOUND", "parentInstanceId must reference a GameObject");
            }

            GameObject gridObject = null;
            GameObject tilemapObject = null;
            try
            {
                using (var mutation = new MutationScope("Create Tilemap"))
                {
                    var name = (string)args["name"] ?? "Tilemap";
                    gridObject = new GameObject(name + " Grid", typeof(Grid));
                    mutation.RegisterCreated(gridObject);
                    tilemapObject = new GameObject(name, typeof(Tilemap), typeof(TilemapRenderer));
                    mutation.RegisterCreated(tilemapObject);
                    mutation.SetParent(tilemapObject.transform, gridObject.transform);
                    if (parent != null)
                    {
                        SceneManager.MoveGameObjectToScene(gridObject, parent.scene);
                        mutation.SetParent(gridObject.transform, parent.transform);
                    }
                    mutation.Complete();
                }
                return ToolResult.Ok("created Tilemap", new JObject
                {
                    ["grid"] = ToolTargetRef.Capture(gridObject)?.ToJson(),
                    ["tilemap"] = ToolTargetRef.Capture(tilemapObject.GetComponent<Tilemap>())?.ToJson(),
                });
            }
            catch (Exception exception)
            {
                if (tilemapObject != null) UnityEngine.Object.DestroyImmediate(tilemapObject);
                if (gridObject != null) UnityEngine.Object.DestroyImmediate(gridObject);
                return ToolResult.Error("CREATE_TILEMAP_FAILED", exception.Message);
            }
        }
    }

    public sealed class SetTilemapCellsTool : IMcpTool
    {
        public string Name => "set_tilemap_cells";
        public string Description => "Sets or clears bounded Tilemap cells using TileBase asset paths with Undo.";
        public JObject InputSchema => SystemToolSupport.TargetSchema(new JObject
        {
            ["cells"] = new JObject
            {
                ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 5000,
                ["items"] = new JObject
                {
                    ["type"] = "object", ["properties"] = new JObject
                    {
                        ["x"] = new JObject { ["type"] = "integer" }, ["y"] = new JObject { ["type"] = "integer" },
                        ["z"] = new JObject { ["type"] = "integer", ["default"] = 0 }, ["tileAssetPath"] = new JObject { ["type"] = "string" },
                    }, ["required"] = new JArray { "x", "y" }, ["additionalProperties"] = false,
                },
            },
        }, "cells");
        public bool IsWrite => true;
        public JObject OutputSchema => SystemToolSupport.OutputSchema(new JObject
        {
            ["target"] = new JObject { ["type"] = "object" },
            ["updatedCells"] = new JObject { ["type"] = "integer" },
        }, "target", "updatedCells");
        public ToolResult Execute(JObject args)
        {
            var gameObject = SystemToolSupport.GameObject(args, out var error);
            if (gameObject == null) return error;
            var tilemap = gameObject.GetComponent<Tilemap>();
            if (tilemap == null) return ToolResult.Error("TILEMAP_NOT_FOUND", gameObject.name);
            var positions = new List<Vector3Int>();
            var tiles = new List<TileBase>();
            foreach (var token in (JArray)args["cells"])
            {
                var cell = (JObject)token;
                TileBase tile = null;
                if (cell["tileAssetPath"] != null)
                {
                    tile = AssetDatabase.LoadAssetAtPath<TileBase>((string)cell["tileAssetPath"]);
                    if (tile == null) return ToolResult.Error("TILE_NOT_FOUND", (string)cell["tileAssetPath"]);
                }
                positions.Add(new Vector3Int((int)cell["x"], (int)cell["y"], (int?)cell["z"] ?? 0));
                tiles.Add(tile);
            }
            try
            {
                using (var mutation = new MutationScope("Set Tilemap Cells"))
                {
                    mutation.Record(tilemap);
                    tilemap.SetTiles(positions.ToArray(), tiles.ToArray());
                    mutation.Complete();
                }
                return ToolResult.Ok($"updated {positions.Count} cell(s)", new JObject
                {
                    ["target"] = ToolTargetRef.Capture(tilemap)?.ToJson(),
                    ["updatedCells"] = positions.Count,
                });
            }
            catch (Exception exception)
            {
                return ToolResult.Error("SET_TILEMAP_CELLS_FAILED", exception.Message);
            }
        }
    }

    public sealed class ReadTerrainTool : IMcpTool
    {
        public string Name => "read_terrain";
        public string Description => "Reads Terrain and TerrainData dimensions, resolution, layers, trees and detail metadata.";
        public JObject InputSchema => SystemToolSupport.TargetSchema();
        public bool IsWrite => false;
        public JObject OutputSchema => SystemToolSupport.OutputSchema(new JObject
        {
            ["target"] = new JObject { ["type"] = "object" },
            ["terrainData"] = new JObject { ["type"] = "object" },
            ["size"] = new JObject { ["type"] = "object" },
            ["heightmapResolution"] = new JObject { ["type"] = "integer" },
            ["alphamapResolution"] = new JObject { ["type"] = "integer" },
            ["detailResolution"] = new JObject { ["type"] = "integer" },
            ["terrainLayers"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
            ["treePrototypeCount"] = new JObject { ["type"] = "integer" },
            ["treeInstanceCount"] = new JObject { ["type"] = "integer" },
            ["detailPrototypeCount"] = new JObject { ["type"] = "integer" },
        }, "target", "terrainData", "size", "heightmapResolution", "alphamapResolution", "detailResolution", "terrainLayers", "treePrototypeCount", "treeInstanceCount", "detailPrototypeCount");
        public ToolResult Execute(JObject args)
        {
            var gameObject = SystemToolSupport.GameObject(args, out var error);
            if (gameObject == null) return error;
            var terrain = gameObject.GetComponent<Terrain>();
            if (terrain == null || terrain.terrainData == null) return ToolResult.Error("TERRAIN_NOT_FOUND", gameObject.name);
            var data = terrain.terrainData;
            return ToolResult.Ok($"terrain '{terrain.name}'", new JObject
            {
                ["target"] = ToolTargetRef.Capture(terrain)?.ToJson(), ["terrainData"] = SystemToolSupport.ObjectJson(data),
                ["size"] = SystemToolSupport.Vec3(data.size), ["heightmapResolution"] = data.heightmapResolution,
                ["alphamapResolution"] = data.alphamapResolution, ["detailResolution"] = data.detailResolution,
                ["terrainLayers"] = new JArray((data.terrainLayers ?? Array.Empty<TerrainLayer>()).Select(SystemToolSupport.ObjectJson)),
                ["treePrototypeCount"] = data.treePrototypes?.Length ?? 0, ["treeInstanceCount"] = data.treeInstanceCount,
                ["detailPrototypeCount"] = data.detailPrototypes?.Length ?? 0,
            });
        }
    }

    public sealed class CreateTerrainTool : IMcpTool
    {
        public string Name => "create_terrain";
        public string Description => "Creates TerrainData asset and a Terrain GameObject. The asset remains dirty until explicitly saved.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["name"] = new JObject { ["type"] = "string", ["default"] = "Terrain" },
                ["terrainDataPath"] = new JObject { ["type"] = "string" },
                ["size"] = SchemaHelpers.StrictVec3Schema,
                ["heightmapResolution"] = new JObject { ["type"] = "integer", ["minimum"] = 33, ["maximum"] = 4097, ["default"] = 513 },
            }, ["required"] = new JArray { "terrainDataPath" }, ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public JObject OutputSchema => SystemToolSupport.OutputSchema(new JObject
        {
            ["target"] = new JObject { ["type"] = "object" },
            ["terrainData"] = new JObject { ["type"] = "object" },
            ["savedAssets"] = new JObject { ["type"] = "boolean" },
        }, "target", "terrainData", "savedAssets");
        public ToolResult Execute(JObject args)
        {
            if (!SystemToolSupport.TryAssetPath((string)args["terrainDataPath"], ".asset", out var path, out var error)) return ToolResult.Error("BAD_ARGS", error);
            if (AssetDatabase.AssetPathExists(path)) return ToolResult.Error("ASSET_EXISTS", path);
            var resolution = (int?)args["heightmapResolution"] ?? 513;
            var resolutionBase = resolution - 1;
            if (resolution < 33 || resolution > 4097 || (resolutionBase & (resolutionBase - 1)) != 0)
                return ToolResult.Error("BAD_RESOLUTION", "heightmapResolution must be 2^n + 1 between 33 and 4097");
            var size = new Vector3(1000f, 600f, 1000f);
            if (args["size"] != null
                && (!SystemToolSupport.TryFiniteVector3(args["size"], out size) || size.x <= 0 || size.y <= 0 || size.z <= 0))
                return ToolResult.Error("BAD_SIZE", "size must contain three finite positive values");

            var createdFolders = new List<string>();
            TerrainData data = null;
            GameObject gameObject = null;
            try
            {
                SystemToolSupport.EnsureFolder(path, createdFolders);
                data = new TerrainData
                {
                    name = Path.GetFileNameWithoutExtension(path),
                    heightmapResolution = resolution,
                    size = size,
                };
                AssetDatabase.CreateAsset(data, path);
                using (var mutation = new MutationScope("Create Terrain"))
                {
                    gameObject = Terrain.CreateTerrainGameObject(data);
                    gameObject.name = (string)args["name"] ?? "Terrain";
                    mutation.RegisterCreated(gameObject, "Create Terrain");
                    mutation.Touch(data);
                    mutation.Complete();
                }
                return ToolResult.Ok("created Terrain", new JObject
                {
                    ["target"] = ToolTargetRef.Capture(gameObject)?.ToJson(), ["terrainData"] = SystemToolSupport.ObjectJson(data), ["savedAssets"] = false,
                });
            }
            catch (Exception exception)
            {
                if (gameObject != null) UnityEngine.Object.DestroyImmediate(gameObject);
                if (AssetDatabase.AssetPathExists(path)) AssetDatabase.DeleteAsset(path);
                else if (data != null) UnityEngine.Object.DestroyImmediate(data);
                SystemToolSupport.CleanupEmptyFolders(createdFolders);
                return ToolResult.Error("CREATE_TERRAIN_FAILED", exception.Message);
            }
        }
    }

    public sealed class SetTerrainHeightsTool : IMcpTool
    {
        public string Name => "set_terrain_heights";
        public string Description => "Writes a bounded normalized height patch into TerrainData with Undo; values must be rows of 0..1 heights.";
        public JObject InputSchema => SystemToolSupport.TargetSchema(new JObject
        {
            ["xBase"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 },
            ["yBase"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 },
            ["heights"] = new JObject
            {
                ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 512,
                ["items"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 }, ["minItems"] = 1, ["maxItems"] = 512 },
            },
        }, "heights");
        public bool IsWrite => true;
        public JObject OutputSchema => SystemToolSupport.OutputSchema(new JObject
        {
            ["terrainData"] = new JObject { ["type"] = "object" },
            ["xBase"] = new JObject { ["type"] = "integer" },
            ["yBase"] = new JObject { ["type"] = "integer" },
            ["width"] = new JObject { ["type"] = "integer" },
            ["height"] = new JObject { ["type"] = "integer" },
            ["savedAssets"] = new JObject { ["type"] = "boolean" },
        }, "terrainData", "xBase", "yBase", "width", "height", "savedAssets");
        public ToolResult Execute(JObject args)
        {
            var gameObject = SystemToolSupport.GameObject(args, out var error);
            if (gameObject == null) return error;
            var terrain = gameObject.GetComponent<Terrain>();
            if (terrain == null || terrain.terrainData == null) return ToolResult.Error("TERRAIN_NOT_FOUND", gameObject.name);
            if (!(args["heights"] is JArray rows) || rows.Count == 0 || !(rows[0] is JArray firstRow) || firstRow.Count == 0)
                return ToolResult.Error("BAD_HEIGHTS", "heights must be a non-empty rectangular array");
            int height = rows.Count;
            int width = firstRow.Count;
            if (height > 512 || width > 512 || rows.Any(row => !(row is JArray array) || array.Count != width))
                return ToolResult.Error("BAD_HEIGHTS", "heights must be rectangular and at most 512x512");
            var values = new float[height, width];
            for (int y = 0; y < height; y++)
            {
                var row = (JArray)rows[y];
                for (int x = 0; x < width; x++)
                {
                    if (!SystemToolSupport.TryFiniteNumber(row[x], out var value) || value < 0 || value > 1)
                        return ToolResult.Error("BAD_HEIGHTS", $"height[{y},{x}] must be finite and between 0 and 1");
                    values[y, x] = value;
                }
            }
            int xBase = (int?)args["xBase"] ?? 0, yBase = (int?)args["yBase"] ?? 0;
            var resolution = terrain.terrainData.heightmapResolution;
            if (xBase < 0 || yBase < 0 || width > resolution || height > resolution
                || xBase > resolution - width || yBase > resolution - height)
                return ToolResult.Error("HEIGHT_PATCH_OUT_OF_RANGE", "height patch exceeds heightmapResolution");
            try
            {
                using (var mutation = new MutationScope("Set Terrain Heights"))
                {
                    Undo.RegisterCompleteObjectUndo(terrain.terrainData, "Set Terrain Heights");
                    terrain.terrainData.SetHeights(xBase, yBase, values);
                    mutation.Touch(terrain.terrainData);
                    mutation.Complete();
                }
                return ToolResult.Ok($"updated {width}x{height} heights", new JObject
                {
                    ["terrainData"] = SystemToolSupport.ObjectJson(terrain.terrainData),
                    ["xBase"] = xBase, ["yBase"] = yBase, ["width"] = width, ["height"] = height,
                    ["savedAssets"] = false,
                });
            }
            catch (Exception exception)
            {
                return ToolResult.Error("SET_TERRAIN_HEIGHTS_FAILED", exception.Message);
            }
        }
    }

    public sealed class ListUiDocumentsTool : IMcpTool
    {
        public string Name => "list_ui_documents";
        public string Description => "Lists loaded UI Toolkit UIDocument components and their VisualTreeAsset/PanelSettings bindings.";
        public JObject InputSchema => SystemToolSupport.EmptySchema();
        public bool IsWrite => false;
        public JObject OutputSchema => SystemToolSupport.OutputSchema(new JObject
        {
            ["documents"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object" } },
        }, "documents");
        public ToolResult Execute(JObject args)
        {
            var documents = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            return ToolResult.Ok($"{documents.Length} UIDocument(s)", new JObject
            {
                ["documents"] = new JArray(documents.Select(document => new JObject
                {
                    ["target"] = ToolTargetRef.Capture(document)?.ToJson(), ["visualTreeAsset"] = SystemToolSupport.ObjectJson(document.visualTreeAsset),
                    ["panelSettings"] = SystemToolSupport.ObjectJson(document.panelSettings), ["sortingOrder"] = document.sortingOrder,
                })),
            });
        }
    }

    public sealed class CreatePanelSettingsTool : IMcpTool
    {
        public string Name => "create_panel_settings";
        public string Description => "Creates a UI Toolkit PanelSettings asset with runtime scaling settings; does not save unrelated assets.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["assetPath"] = new JObject { ["type"] = "string" },
                ["scaleMode"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "ConstantPixelSize", "ScaleWithScreenSize", "ConstantPhysicalSize" }, ["default"] = "ScaleWithScreenSize" },
                ["referenceWidth"] = new JObject { ["type"] = "number", ["minimum"] = 1, ["default"] = 1920 },
                ["referenceHeight"] = new JObject { ["type"] = "number", ["minimum"] = 1, ["default"] = 1080 },
                ["match"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1, ["default"] = 0.5 },
            }, ["required"] = new JArray { "assetPath" }, ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public JObject OutputSchema => SystemToolSupport.OutputSchema(new JObject
        {
            ["panelSettings"] = new JObject { ["type"] = "object" },
            ["savedAssets"] = new JObject { ["type"] = "boolean" },
        }, "panelSettings", "savedAssets");
        public ToolResult Execute(JObject args)
        {
            if (!SystemToolSupport.TryAssetPath((string)args["assetPath"], ".asset", out var path, out var error))
                return ToolResult.Error("BAD_ARGS", error);
            if (AssetDatabase.AssetPathExists(path)) return ToolResult.Error("ASSET_EXISTS", path);
            if (!Enum.TryParse((string)args["scaleMode"] ?? "ScaleWithScreenSize", true, out PanelScaleMode scaleMode))
                return ToolResult.Error("BAD_SCALE_MODE", (string)args["scaleMode"]);
            if (!SystemToolSupport.TryFiniteNumber(args["referenceWidth"] ?? 1920, out var width) || width < 1
                || !SystemToolSupport.TryFiniteNumber(args["referenceHeight"] ?? 1080, out var height) || height < 1
                || !SystemToolSupport.TryFiniteNumber(args["match"] ?? 0.5f, out var match) || match < 0 || match > 1)
                return ToolResult.Error("BAD_ARGS", "reference dimensions and match must be finite and within the advertised ranges");

            var createdFolders = new List<string>();
            PanelSettings settings = null;
            try
            {
                SystemToolSupport.EnsureFolder(path, createdFolders);
                settings = ScriptableObject.CreateInstance<PanelSettings>();
                settings.name = Path.GetFileNameWithoutExtension(path);
                settings.scaleMode = scaleMode;
                settings.referenceResolution = new Vector2Int(Mathf.RoundToInt(width), Mathf.RoundToInt(height));
                settings.match = match;
                AssetDatabase.CreateAsset(settings, path);
                Undo.RegisterCreatedObjectUndo(settings, "Create PanelSettings");
                EditorUtility.SetDirty(settings);
                return ToolResult.Ok($"created PanelSettings '{path}'", new JObject
                {
                    ["panelSettings"] = SystemToolSupport.ObjectJson(settings),
                    ["savedAssets"] = false,
                });
            }
            catch (Exception exception)
            {
                if (AssetDatabase.AssetPathExists(path)) AssetDatabase.DeleteAsset(path);
                else if (settings != null) UnityEngine.Object.DestroyImmediate(settings);
                SystemToolSupport.CleanupEmptyFolders(createdFolders);
                return ToolResult.Error("CREATE_PANEL_SETTINGS_FAILED", exception.Message);
            }
        }
    }

    public sealed class CreateUiDocumentTool : IMcpTool
    {
        public string Name => "create_ui_document";
        public string Description => "Creates a UIDocument GameObject and optionally binds existing UXML and PanelSettings assets.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["name"] = new JObject { ["type"] = "string", ["default"] = "UI Document" },
                ["uxmlPath"] = new JObject { ["type"] = "string" },
                ["panelSettingsPath"] = new JObject { ["type"] = "string" },
                ["parentInstanceId"] = new JObject { ["type"] = "integer" },
                ["sortingOrder"] = new JObject { ["type"] = "number", ["default"] = 0 },
            }, ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public JObject OutputSchema => SystemToolSupport.OutputSchema(new JObject
        {
            ["target"] = new JObject { ["type"] = "object" },
        }, "target");
        public ToolResult Execute(JObject args)
        {
            GameObject parent = null;
            if (args["parentInstanceId"] != null)
            {
                parent = EditorUtility.EntityIdToObject((EntityId)(int)args["parentInstanceId"]) as GameObject;
                if (parent == null) return ToolResult.Error("PARENT_NOT_FOUND", "parentInstanceId must reference a GameObject");
            }
            var visualTree = args["uxmlPath"] != null
                ? AssetDatabase.LoadAssetAtPath<VisualTreeAsset>((string)args["uxmlPath"])
                : null;
            if (args["uxmlPath"] != null && visualTree == null)
                return ToolResult.Error("UXML_NOT_FOUND", (string)args["uxmlPath"]);
            var panelSettings = args["panelSettingsPath"] != null
                ? AssetDatabase.LoadAssetAtPath<PanelSettings>((string)args["panelSettingsPath"])
                : null;
            if (args["panelSettingsPath"] != null && panelSettings == null)
                return ToolResult.Error("PANEL_SETTINGS_NOT_FOUND", (string)args["panelSettingsPath"]);
            if (!SystemToolSupport.TryFiniteNumber(args["sortingOrder"] ?? 0f, out var sortingOrder))
                return ToolResult.Error("BAD_SORTING_ORDER", "sortingOrder must be finite");

            GameObject gameObject = null;
            try
            {
                gameObject = new GameObject((string)args["name"] ?? "UI Document");
                UIDocument document;
                using (var mutation = new MutationScope("Create UIDocument"))
                {
                    mutation.RegisterCreated(gameObject);
                    document = mutation.AddComponent<UIDocument>(gameObject);
                    document.visualTreeAsset = visualTree;
                    document.panelSettings = panelSettings;
                    document.sortingOrder = sortingOrder;
                    if (parent != null)
                    {
                        SceneManager.MoveGameObjectToScene(gameObject, parent.scene);
                        mutation.SetParent(gameObject.transform, parent.transform);
                    }
                    mutation.Touch(document);
                    mutation.Complete();
                }
                return ToolResult.Ok("created UIDocument", new JObject { ["target"] = ToolTargetRef.Capture(document)?.ToJson() });
            }
            catch (Exception exception)
            {
                if (gameObject != null) UnityEngine.Object.DestroyImmediate(gameObject);
                return ToolResult.Error("CREATE_UI_DOCUMENT_FAILED", exception.Message);
            }
        }
    }

    public sealed class ReadUiAssetTool : IMcpTool
    {
        public string Name => "read_ui_asset";
        public string Description => "Reads bounded UTF-8 text from one Assets/*.uxml or Assets/*.uss source file.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["assetPath"] = new JObject { ["type"] = "string" },
                ["maxCharacters"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 1000000, ["default"] = 100000 },
            }, ["required"] = new JArray { "assetPath" }, ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public JObject OutputSchema => SystemToolSupport.OutputSchema(new JObject
        {
            ["assetPath"] = new JObject { ["type"] = "string" },
            ["content"] = new JObject { ["type"] = "string" },
            ["totalCharacters"] = new JObject { ["type"] = "integer" },
            ["truncated"] = new JObject { ["type"] = "boolean" },
        }, "assetPath", "content", "totalCharacters", "truncated");
        public ToolResult Execute(JObject args)
        {
            if (!SystemToolSupport.TryUiSourcePath((string)args["assetPath"], out var path, out var absolute, out var error))
                return ToolResult.Error("BAD_ARGS", error);
            if (!File.Exists(absolute)) return ToolResult.Error("UI_ASSET_NOT_FOUND", path);
            try
            {
                var text = File.ReadAllText(absolute);
                int max = (int?)args["maxCharacters"] ?? 100000;
                return ToolResult.Ok($"read '{path}'", new JObject
                {
                    ["assetPath"] = path,
                    ["content"] = text.Length > max ? text.Substring(0, max) : text,
                    ["totalCharacters"] = text.Length,
                    ["truncated"] = text.Length > max,
                });
            }
            catch (Exception exception)
            {
                return ToolResult.Error("UI_ASSET_READ_FAILED", exception.Message);
            }
        }
    }

    public sealed class WriteUiAssetTool : IMcpTool
    {
        public string Name => "write_ui_asset";
        public string Description => "Creates or replaces one Assets/*.uxml or Assets/*.uss source file, then imports it explicitly.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["assetPath"] = new JObject { ["type"] = "string" }, ["content"] = new JObject { ["type"] = "string", ["maxLength"] = 1000000 },
                ["overwrite"] = new JObject { ["type"] = "boolean", ["default"] = false },
            }, ["required"] = new JArray { "assetPath", "content" }, ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public JObject OutputSchema => SystemToolSupport.OutputSchema(new JObject
        {
            ["assetPath"] = new JObject { ["type"] = "string" },
            ["guid"] = new JObject { ["type"] = "string" },
            ["asset"] = new JObject { ["type"] = "object" },
            ["importedWithWarnings"] = new JObject { ["type"] = "boolean" },
        }, "assetPath", "guid", "asset", "importedWithWarnings");
        public ToolResult Execute(JObject args)
        {
            if (!SystemToolSupport.TryUiSourcePath((string)args["assetPath"], out var path, out var absolute, out var error))
                return ToolResult.Error("BAD_ARGS", error);
            var existed = File.Exists(absolute);
            if (existed && !((bool?)args["overwrite"] ?? false)) return ToolResult.Error("ASSET_EXISTS", path);
            byte[] previous = null;
            var createdFolders = new List<string>();
            try
            {
                if (existed) previous = File.ReadAllBytes(absolute);
                SystemToolSupport.EnsureFolder(path, createdFolders);
                File.WriteAllText(absolute, (string)args["content"] ?? string.Empty, new System.Text.UTF8Encoding(false));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

                UnityEngine.Object asset;
                var importedWithWarnings = false;
                if (path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase))
                {
                    var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
                    if (visualTree == null || visualTree.importedWithErrors)
                        throw new InvalidOperationException("UXML import failed or completed with errors");
                    importedWithWarnings = visualTree.importedWithWarnings;
                    asset = visualTree;
                }
                else
                {
                    asset = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                    if (asset == null) throw new InvalidOperationException("USS import failed");
                }

                return ToolResult.Ok($"wrote '{path}'", new JObject
                {
                    ["assetPath"] = path,
                    ["guid"] = AssetDatabase.AssetPathToGUID(path),
                    ["asset"] = SystemToolSupport.ObjectJson(asset),
                    ["importedWithWarnings"] = importedWithWarnings,
                });
            }
            catch (Exception exception)
            {
                string rollbackError = null;
                try
                {
                    if (existed)
                    {
                        File.WriteAllBytes(absolute, previous);
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                    }
                    else if (!AssetDatabase.DeleteAsset(path))
                    {
                        if (File.Exists(absolute)) File.Delete(absolute);
                        if (File.Exists(absolute + ".meta")) File.Delete(absolute + ".meta");
                    }
                }
                catch (Exception rollbackException) { rollbackError = rollbackException.Message; }
                SystemToolSupport.CleanupEmptyFolders(createdFolders);
                var message = rollbackError == null
                    ? exception.Message
                    : $"{exception.Message}; rollback failed: {rollbackError}";
                return ToolResult.Error("UI_ASSET_WRITE_FAILED", message);
            }
        }
    }

    public sealed class CreateAnimationClipAssetTool : IMcpTool
    {
        public string Name => "create_animation_clip_asset";
        public string Description => "Creates an empty standalone .anim asset under Assets/ with validated frame rate and WrapMode; no global save.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["assetPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["frameRate"] = new JObject { ["type"] = "number", ["minimum"] = 1, ["maximum"] = 240, ["default"] = 60 },
                ["wrapMode"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray { "Default", "Once", "Loop", "PingPong", "ClampForever" },
                    ["default"] = "Default",
                },
            }, ["required"] = new JArray { "assetPath" }, ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => AnimationToolContracts.AssetWriteDescriptor(Name);
        public ToolResult Execute(JObject args)
        {
            if (!SystemToolSupport.TryAssetPath((string)args["assetPath"], ".anim", out var path, out var error))
                return ToolResult.Error("BAD_ASSET_PATH", error);
            if (AssetDatabase.LoadMainAssetAtPath(path) != null) return ToolResult.Error("ASSET_EXISTS", path);

            var frameRateToken = args["frameRate"];
            var frameRate = frameRateToken == null ? 60d : (double)frameRateToken;
            if (double.IsNaN(frameRate) || double.IsInfinity(frameRate) || frameRate < 1d || frameRate > 240d)
                return ToolResult.Error("BAD_FRAME_RATE", "frameRate must be finite and between 1 and 240");
            if (!Enum.TryParse((string)args["wrapMode"] ?? "Default", true, out WrapMode wrapMode)
                || !Enum.IsDefined(typeof(WrapMode), wrapMode)
                || wrapMode == WrapMode.Clamp)
                return ToolResult.Error("BAD_WRAP_MODE", (string)args["wrapMode"] ?? "Default");

            SystemToolSupport.EnsureFolder(path);
            var clip = new AnimationClip
            {
                name = Path.GetFileNameWithoutExtension(path),
                frameRate = (float)frameRate,
                wrapMode = wrapMode,
            };
            try { AssetDatabase.CreateAsset(clip, path); }
            catch (Exception exception)
            {
                UnityEngine.Object.DestroyImmediate(clip);
                return ToolResult.Error("CREATE_ASSET_FAILED", exception.Message);
            }
            return ToolResult.Ok($"created AnimationClip '{path}'", new JObject
            {
                ["clip"] = SystemToolSupport.ObjectJson(clip),
                ["assetCreated"] = true,
                ["savedAssets"] = false,
                ["undoRegistered"] = false,
            });
        }
    }

    public sealed class SetAnimationFloatCurveTool : IMcpTool
    {
        public string Name => "set_animation_float_curve";
        public string Description => "Adds, replaces or removes one float curve on a standalone .anim asset after full key preflight, with Undo.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["clipPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["relativePath"] = new JObject { ["type"] = "string", ["default"] = "" },
                ["componentType"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["propertyName"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["keys"] = new JObject
                {
                    ["type"] = "array", ["maxItems"] = 10000,
                    ["items"] = new JObject
                    {
                        ["type"] = "object", ["properties"] = new JObject
                        {
                            ["time"] = new JObject { ["type"] = "number" },
                            ["value"] = new JObject { ["type"] = "number" },
                            ["inTangent"] = new JObject { ["type"] = "number", ["default"] = 0 },
                            ["outTangent"] = new JObject { ["type"] = "number", ["default"] = 0 },
                        }, ["required"] = new JArray { "time", "value" }, ["additionalProperties"] = false,
                    },
                },
            }, ["required"] = new JArray { "clipPath", "componentType", "propertyName", "keys" }, ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => AnimationToolContracts.AssetWriteDescriptor(Name, true);
        public ToolResult Execute(JObject args)
        {
            if (!SystemToolSupport.TryAssetPath((string)args["clipPath"], ".anim", out var clipPath, out var pathError))
                return ToolResult.Error("BAD_ASSET_PATH", pathError);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip == null) return ToolResult.Error("CLIP_NOT_FOUND", clipPath);
            if (AssetDatabase.LoadMainAssetAtPath(clipPath) != clip)
                return ToolResult.Error("CLIP_NOT_EDITABLE", "clip must be the main asset of a standalone .anim file");

            var type = ComponentTypeInspector.ResolveType((string)args["componentType"]);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
                return ToolResult.Error("COMPONENT_TYPE_NOT_FOUND", (string)args["componentType"]);
            var propertyName = ((string)args["propertyName"] ?? string.Empty).Trim();
            if (propertyName.Length == 0) return ToolResult.Error("BAD_ARGS", "propertyName is required");

            var keyTokens = (JArray)args["keys"];
            var keys = new List<Keyframe>(keyTokens.Count);
            for (var index = 0; index < keyTokens.Count; index++)
            {
                var token = keyTokens[index];
                if (!TryFinite(token["time"], out var time)
                    || !TryFinite(token["value"], out var value)
                    || !TryFinite(token["inTangent"], out var inTangent, 0f)
                    || !TryFinite(token["outTangent"], out var outTangent, 0f))
                    return ToolResult.Error("BAD_KEYFRAME", $"keys[{index}] values must be finite numbers");
                keys.Add(new Keyframe(time, value, inTangent, outTangent));
            }

            var binding = EditorCurveBinding.FloatCurve((string)args["relativePath"] ?? string.Empty, type, propertyName);
            var curve = keys.Count == 0 ? null : new AnimationCurve(keys.OrderBy(key => key.time).ToArray());
            using (var mutation = new MutationScope("Set Animation Float Curve"))
            {
                mutation.Record(clip, "Set Animation Float Curve");
                AnimationUtility.SetEditorCurve(clip, binding, curve);
                mutation.Complete();
            }
            return ToolResult.Ok(curve == null ? "removed animation curve" : $"set {curve.length} key(s)", new JObject
            {
                ["clip"] = SystemToolSupport.ObjectJson(clip),
                ["binding"] = new JObject
                {
                    ["relativePath"] = binding.path,
                    ["componentType"] = binding.type.FullName,
                    ["propertyName"] = binding.propertyName,
                },
                ["keyCount"] = curve != null ? curve.length : 0,
                ["removed"] = curve == null,
                ["savedAssets"] = false,
            });
        }

        private static bool TryFinite(JToken token, out float value, float fallback = 0f)
        {
            value = fallback;
            if (token == null) return true;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) return false;
            var number = (double)token;
            if (double.IsNaN(number) || double.IsInfinity(number) || number < -float.MaxValue || number > float.MaxValue) return false;
            value = (float)number;
            return true;
        }
    }

    public sealed class CreateAvatarMaskTool : IMcpTool
    {
        private static readonly string[] BodyPartNames = Enum.GetValues(typeof(AvatarMaskBodyPart))
            .Cast<AvatarMaskBodyPart>()
            .Where(part => part != AvatarMaskBodyPart.LastBodyPart)
            .Select(part => part.ToString())
            .ToArray();

        public string Name => "create_avatar_mask";
        public string Description => "Creates an AvatarMask asset under Assets/ and optionally activates only the listed humanoid body parts; omitted means all parts active.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["assetPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["activeBodyParts"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "string", ["enum"] = new JArray(BodyPartNames) },
                    ["maxItems"] = BodyPartNames.Length,
                },
            }, ["required"] = new JArray { "assetPath" }, ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolDescriptor Descriptor => AnimationToolContracts.AssetWriteDescriptor(Name);
        public ToolResult Execute(JObject args)
        {
            if (!SystemToolSupport.TryAssetPath((string)args["assetPath"], ".mask", out var path, out var error))
                return ToolResult.Error("BAD_ASSET_PATH", error);
            if (AssetDatabase.LoadMainAssetAtPath(path) != null) return ToolResult.Error("ASSET_EXISTS", path);

            var requested = args["activeBodyParts"] as JArray;
            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (requested != null)
            {
                foreach (var token in requested)
                {
                    var name = (string)token;
                    if (string.IsNullOrEmpty(name) || !BodyPartNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                        return ToolResult.Error("BAD_BODY_PART", name ?? "null");
                    active.Add(name);
                }
            }

            var mask = new AvatarMask { name = Path.GetFileNameWithoutExtension(path) };
            foreach (AvatarMaskBodyPart part in Enum.GetValues(typeof(AvatarMaskBodyPart)))
                if (part != AvatarMaskBodyPart.LastBodyPart)
                    mask.SetHumanoidBodyPartActive(part, requested == null || active.Contains(part.ToString()));
            SystemToolSupport.EnsureFolder(path);
            try { AssetDatabase.CreateAsset(mask, path); }
            catch (Exception exception)
            {
                UnityEngine.Object.DestroyImmediate(mask);
                return ToolResult.Error("CREATE_ASSET_FAILED", exception.Message);
            }

            var effective = new JArray();
            foreach (AvatarMaskBodyPart part in Enum.GetValues(typeof(AvatarMaskBodyPart)))
                if (part != AvatarMaskBodyPart.LastBodyPart && mask.GetHumanoidBodyPartActive(part)) effective.Add(part.ToString());
            return ToolResult.Ok($"created AvatarMask '{path}'", new JObject
            {
                ["avatarMask"] = SystemToolSupport.ObjectJson(mask),
                ["activeBodyParts"] = effective,
                ["assetCreated"] = true,
                ["savedAssets"] = false,
                ["undoRegistered"] = false,
            });
        }
    }

    public sealed class GetPerformanceSnapshotTool : IMcpTool
    {
        public string Name => "get_performance_snapshot";
        public string Description => "Returns current memory, frame timing, object counts and rendering capability counters without starting the Profiler.";
        public JObject InputSchema => SystemToolSupport.EmptySchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            FrameTimingManager.CaptureFrameTimings();
            var timings = new FrameTiming[4];
            var count = FrameTimingManager.GetLatestTimings((uint)timings.Length, timings);
            var frames = new JArray(timings.Take((int)count).Select(timing => new JObject
            {
                ["cpuFrameTimeMs"] = timing.cpuFrameTime, ["gpuFrameTimeMs"] = timing.gpuFrameTime,
                ["cpuMainThreadFrameTimeMs"] = timing.cpuMainThreadFrameTime,
                ["cpuRenderThreadFrameTimeMs"] = timing.cpuRenderThreadFrameTime,
                ["cpuPresentWaitTimeMs"] = timing.cpuMainThreadPresentWaitTime,
                ["syncInterval"] = timing.syncInterval, ["widthScale"] = timing.widthScale, ["heightScale"] = timing.heightScale,
            }));
            return ToolResult.Ok("performance snapshot", new JObject
            {
                ["memory"] = new JObject
                {
                    ["totalAllocatedBytes"] = Profiler.GetTotalAllocatedMemoryLong(), ["totalReservedBytes"] = Profiler.GetTotalReservedMemoryLong(),
                    ["totalUnusedReservedBytes"] = Profiler.GetTotalUnusedReservedMemoryLong(), ["monoUsedBytes"] = Profiler.GetMonoUsedSizeLong(),
                    ["monoHeapBytes"] = Profiler.GetMonoHeapSizeLong(),
                },
                ["frameTimings"] = frames,
                ["targetFrameRate"] = Application.targetFrameRate, ["vSyncCount"] = QualitySettings.vSyncCount,
                ["gameObjects"] = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
                ["renderers"] = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
                ["rigidbodies3D"] = UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
                ["rigidbodies2D"] = UnityEngine.Object.FindObjectsByType<Rigidbody2D>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
            });
        }
    }
}
