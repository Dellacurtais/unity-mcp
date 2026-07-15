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

        public static void EnsureFolder(string assetPath)
        {
            var folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            EnsureFolderPath(folder);
        }

        private static void EnsureFolderPath(string folder)
        {
            if (string.IsNullOrEmpty(folder) || folder == "Assets" || AssetDatabase.IsValidFolder(folder)) return;
            var parent = Path.GetDirectoryName(folder)?.Replace('\\', '/') ?? "Assets";
            EnsureFolderPath(parent);
            var guid = AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
            if (string.IsNullOrEmpty(guid) && !AssetDatabase.IsValidFolder(folder))
                throw new InvalidOperationException($"could not create folder '{folder}'");
        }

        public static JObject Vec3(Vector3 value) => new JObject { ["x"] = value.x, ["y"] = value.y, ["z"] = value.z };
        public static JObject Vec2(Vector2 value) => new JObject { ["x"] = value.x, ["y"] = value.y };
    }

    public sealed class GetRenderPipelineContextTool : IMcpTool
    {
        public string Name => "get_render_pipeline_context";
        public string Description => "Reports Built-in/SRP context, active pipeline asset, quality override, color space, graphics API and rendering capabilities.";
        public JObject InputSchema => SystemToolSupport.EmptySchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var current = GraphicsSettings.currentRenderPipeline;
            var defaultPipeline = GraphicsSettings.defaultRenderPipeline;
            var qualityPipeline = QualitySettings.renderPipeline;
            return ToolResult.Ok(current == null ? "Built-in Render Pipeline" : current.GetType().Name, new JObject
            {
                ["pipeline"] = current == null ? "BuiltIn" : current.GetType().FullName,
                ["current"] = SystemToolSupport.ObjectJson(current),
                ["default"] = SystemToolSupport.ObjectJson(defaultPipeline),
                ["qualityOverride"] = SystemToolSupport.ObjectJson(qualityPipeline),
                ["qualityLevel"] = QualitySettings.GetQualityLevel(),
                ["qualityName"] = QualitySettings.names.ElementAtOrDefault(QualitySettings.GetQualityLevel()),
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
            var paths = AssetDatabase.FindAssets("t:Shader").Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            int offset = Math.Min((int?)args["offset"] ?? 0, paths.Length);
            int limit = (int?)args["limit"] ?? 200;
            var items = new JArray(paths.Skip(offset).Take(limit).Select(path => SystemToolSupport.ObjectJson(AssetDatabase.LoadAssetAtPath<Shader>(path))));
            return ToolResult.Ok($"{paths.Length} shader(s)", new JObject
            {
                ["shaders"] = items, ["offset"] = offset, ["count"] = items.Count,
                ["total"] = paths.Length, ["truncated"] = offset + items.Count < paths.Length,
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
            {
                var name = shader.GetPropertyName(index);
                var type = shader.GetPropertyType(index);
                JToken value;
                switch (type)
                {
                    case ShaderPropertyType.Color:
                        var color = material.GetColor(name);
                        value = new JObject { ["r"] = color.r, ["g"] = color.g, ["b"] = color.b, ["a"] = color.a };
                        break;
                    case ShaderPropertyType.Vector:
                        var vector = material.GetVector(name);
                        value = new JArray(vector.x, vector.y, vector.z, vector.w);
                        break;
                    case ShaderPropertyType.Texture:
                        value = SystemToolSupport.ObjectJson(material.GetTexture(name));
                        break;
                    case ShaderPropertyType.Int:
                        value = material.GetInteger(name);
                        break;
                    default:
                        value = material.GetFloat(name);
                        break;
                }
                properties.Add(new JObject
                {
                    ["name"] = name, ["description"] = shader.GetPropertyDescription(index),
                    ["type"] = type.ToString(), ["value"] = value,
                });
            }
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
        public string Description => "Creates a dirty Material asset for an exact Shader name without saving unrelated assets.";
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
                Undo.RegisterCreatedObjectUndo(material, "Create Material");
                EditorUtility.SetDirty(material);
                return ToolResult.Ok($"created material '{path}'", new JObject { ["material"] = SystemToolSupport.ObjectJson(material), ["savedAssets"] = false });
            }
            catch (Exception exception) { return ToolResult.Error("CREATE_MATERIAL_FAILED", exception.Message); }
        }
    }

    public sealed class SetMaterialPropertyTypedTool : IMcpTool
    {
        public string Name => "set_material_property_typed";
        public string Description => "Sets one Shader property on a Material asset with Undo and type validation; does not save the asset.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["assetPath"] = new JObject { ["type"] = "string" },
                ["propertyName"] = new JObject { ["type"] = "string" },
                ["value"] = new JObject(),
            },
            ["required"] = new JArray { "assetPath", "propertyName", "value" }, ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>((string)args["assetPath"]);
            if (material == null) return ToolResult.Error("MATERIAL_NOT_FOUND", "Material asset not found");
            var name = (string)args["propertyName"];
            if (!material.HasProperty(name)) return ToolResult.Error("PROPERTY_NOT_FOUND", name);
            int index = material.shader.FindPropertyIndex(name);
            var type = material.shader.GetPropertyType(index);
            try
            {
                using (var mutation = new MutationScope("Set Material Property"))
                {
                    mutation.Record(material);
                    switch (type)
                {
                    case ShaderPropertyType.Color: material.SetColor(name, JsonHelpers.ParseColor(args["value"])); break;
                    case ShaderPropertyType.Vector:
                        var array = args["value"] as JArray;
                        if (array == null || array.Count < 4) throw new InvalidOperationException("Vector requires [x,y,z,w]");
                        material.SetVector(name, new Vector4((float)array[0], (float)array[1], (float)array[2], (float)array[3])); break;
                    case ShaderPropertyType.Texture:
                        var reference = ToolTargetRef.FromJson(args["value"]);
                        var texture = ToolTargetResolver.Resolve(reference, out var resolveError) as Texture;
                        if (texture == null && args["value"].Type == JTokenType.String)
                            texture = AssetDatabase.LoadAssetAtPath<Texture>((string)args["value"]);
                        if (texture == null && args["value"].Type != JTokenType.Null) throw new InvalidOperationException(resolveError ?? "Texture not found");
                        material.SetTexture(name, texture); break;
                    case ShaderPropertyType.Int: material.SetInteger(name, (int)args["value"]); break;
                    default: material.SetFloat(name, (float)args["value"]); break;
                    }
                    mutation.Complete();
                }
                EditorUtility.SetDirty(material);
                return ToolResult.Ok($"set {name}", new JObject { ["material"] = SystemToolSupport.ObjectJson(material), ["propertyType"] = type.ToString(), ["savedAssets"] = false });
            }
            catch (Exception exception) { return ToolResult.Error("BAD_VALUE", exception.Message); }
        }
    }

    public sealed class GetLightingContextTool : IMcpTool
    {
        public string Name => "get_lighting_context";
        public string Description => "Reports RenderSettings, loaded lights, reflection probes, light probe groups and current bake state.";
        public JObject InputSchema => SystemToolSupport.EmptySchema();
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var reflectionProbes = UnityEngine.Object.FindObjectsByType<ReflectionProbe>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var lightProbeGroups = UnityEngine.Object.FindObjectsByType<LightProbeGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            return ToolResult.Ok($"{lights.Length} light(s)", new JObject
            {
                ["ambientMode"] = RenderSettings.ambientMode.ToString(),
                ["ambientIntensity"] = RenderSettings.ambientIntensity,
                ["fog"] = RenderSettings.fog,
                ["fogMode"] = RenderSettings.fogMode.ToString(),
                ["skybox"] = SystemToolSupport.ObjectJson(RenderSettings.skybox),
                ["sun"] = SystemToolSupport.ObjectJson(RenderSettings.sun),
                ["lightmappingRunning"] = Lightmapping.isRunning,
                ["lights"] = new JArray(lights.Select(light => new JObject
                {
                    ["target"] = ToolTargetRef.Capture(light)?.ToJson(), ["type"] = light.type.ToString(),
                    ["intensity"] = light.intensity, ["range"] = light.range, ["shadows"] = light.shadows.ToString(),
                })),
                ["reflectionProbes"] = new JArray(reflectionProbes.Select(SystemToolSupport.ObjectJson)),
                ["lightProbeGroups"] = new JArray(lightProbeGroups.Select(group => new JObject
                {
                    ["target"] = ToolTargetRef.Capture(group)?.ToJson(), ["probeCount"] = group.probePositions?.Length ?? 0,
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
        public string Description => "Runs a read-only Physics2D raycast, overlap point, overlap circle or overlap box query.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "raycast", "overlap_point", "overlap_circle", "overlap_box" } },
                ["origin"] = SchemaHelpers.Vec3Schema,
                ["direction"] = SchemaHelpers.Vec3Schema,
                ["distance"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["default"] = 1000 },
                ["size"] = SchemaHelpers.Vec3Schema,
                ["radius"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["default"] = 1 },
                ["angle"] = new JObject { ["type"] = "number", ["default"] = 0 },
                ["layerMask"] = new JObject { ["type"] = "integer", ["default"] = -1 },
            },
            ["required"] = new JArray { "kind", "origin" }, ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var kind = (string)args["kind"];
            var origin3 = JsonHelpers.ParseVector3(args["origin"]);
            var origin = new Vector2(origin3.x, origin3.y);
            int mask = (int?)args["layerMask"] ?? Physics2D.AllLayers;
            var hits = new JArray();
            if (kind == "raycast")
            {
                var direction3 = JsonHelpers.ParseVector3(args["direction"], Vector3.right);
                var hit = Physics2D.Raycast(origin, new Vector2(direction3.x, direction3.y), (float?)args["distance"] ?? 1000f, mask);
                if (hit.collider != null) hits.Add(new JObject
                {
                    ["collider"] = SystemToolSupport.ObjectJson(hit.collider), ["point"] = SystemToolSupport.Vec2(hit.point),
                    ["normal"] = SystemToolSupport.Vec2(hit.normal), ["distance"] = hit.distance, ["fraction"] = hit.fraction,
                });
            }
            else
            {
                Collider2D collider;
                if (kind == "overlap_point") collider = Physics2D.OverlapPoint(origin, mask);
                else if (kind == "overlap_circle") collider = Physics2D.OverlapCircle(origin, (float?)args["radius"] ?? 1f, mask);
                else
                {
                    var size3 = JsonHelpers.ParseVector3(args["size"], Vector3.one);
                    collider = Physics2D.OverlapBox(origin, new Vector2(size3.x, size3.y), (float?)args["angle"] ?? 0f, mask);
                }
                if (collider != null) hits.Add(SystemToolSupport.ObjectJson(collider));
            }
            return ToolResult.Ok($"{hits.Count} hit(s)", new JObject { ["kind"] = kind, ["hits"] = hits });
        }
    }

    public sealed class GetPhysics2DSettingsTool : IMcpTool
    {
        public string Name => "get_physics2d_settings";
        public string Description => "Reads global Physics2D settings and counts loaded Rigidbody2D/Collider2D components.";
        public JObject InputSchema => SystemToolSupport.EmptySchema();
        public bool IsWrite => false;
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
        public string Description => "Sets selected global Physics2D settings with an explicit project-settings capability.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["gravity"] = SchemaHelpers.Vec3Schema,
                ["velocityIterations"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100 },
                ["positionIterations"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100 },
                ["queriesHitTriggers"] = new JObject { ["type"] = "boolean" },
                ["queriesStartInColliders"] = new JObject { ["type"] = "boolean" },
            }, ["minProperties"] = 1, ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            if (args["gravity"] != null) { var v = JsonHelpers.ParseVector3(args["gravity"]); Physics2D.gravity = new Vector2(v.x, v.y); }
            if (args["velocityIterations"] != null) Physics2D.velocityIterations = (int)args["velocityIterations"];
            if (args["positionIterations"] != null) Physics2D.positionIterations = (int)args["positionIterations"];
            if (args["queriesHitTriggers"] != null) Physics2D.queriesHitTriggers = (bool)args["queriesHitTriggers"];
            if (args["queriesStartInColliders"] != null) Physics2D.queriesStartInColliders = (bool)args["queriesStartInColliders"];
            return new GetPhysics2DSettingsTool().Execute(new JObject());
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
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 5000, ["default"] = 500 },
            }, ["additionalProperties"] = false,
        };
        public bool IsWrite => false;
        public ToolResult Execute(JObject args)
        {
            var folder = (string)args["folder"] ?? "Assets";
            var sprites = AssetDatabase.FindAssets("t:Sprite", new[] { folder }).SelectMany(guid =>
                AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(guid)).OfType<Sprite>())
                .Distinct().Take((int?)args["limit"] ?? 500).ToArray();
            return ToolResult.Ok($"{sprites.Length} sprite(s)", new JObject
            {
                ["sprites"] = new JArray(sprites.Select(sprite => new JObject
                {
                    ["sprite"] = SystemToolSupport.ObjectJson(sprite), ["rect"] = new JObject
                    { ["x"] = sprite.rect.x, ["y"] = sprite.rect.y, ["width"] = sprite.rect.width, ["height"] = sprite.rect.height },
                    ["pivot"] = SystemToolSupport.Vec2(sprite.pivot), ["pixelsPerUnit"] = sprite.pixelsPerUnit,
                    ["border"] = new JArray(sprite.border.x, sprite.border.y, sprite.border.z, sprite.border.w),
                })),
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
        public ToolResult Execute(JObject args)
        {
            var parent = args["parentInstanceId"] != null ? EditorUtility.InstanceIDToObject((int)args["parentInstanceId"]) as GameObject : null;
            var gridObject = new GameObject(((string)args["name"] ?? "Tilemap") + " Grid", typeof(Grid));
            var tilemapObject = new GameObject((string)args["name"] ?? "Tilemap", typeof(Tilemap), typeof(TilemapRenderer));
            using (var mutation = new MutationScope("Create Tilemap"))
            {
                mutation.RegisterCreated(gridObject);
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
            using (var mutation = new MutationScope("Set Tilemap Cells"))
            {
                mutation.Record(tilemap);
                for (int index = 0; index < positions.Count; index++) tilemap.SetTile(positions[index], tiles[index]);
                mutation.Complete();
            }
            return ToolResult.Ok($"updated {positions.Count} cell(s)", new JObject { ["target"] = ToolTargetRef.Capture(tilemap)?.ToJson() });
        }
    }

    public sealed class ReadTerrainTool : IMcpTool
    {
        public string Name => "read_terrain";
        public string Description => "Reads Terrain and TerrainData dimensions, resolution, layers, trees and detail metadata.";
        public JObject InputSchema => SystemToolSupport.TargetSchema();
        public bool IsWrite => false;
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
                ["size"] = SchemaHelpers.Vec3Schema,
                ["heightmapResolution"] = new JObject { ["type"] = "integer", ["minimum"] = 33, ["maximum"] = 4097, ["default"] = 513 },
            }, ["required"] = new JArray { "terrainDataPath" }, ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            if (!SystemToolSupport.TryAssetPath((string)args["terrainDataPath"], ".asset", out var path, out var error)) return ToolResult.Error("BAD_ARGS", error);
            if (AssetDatabase.LoadMainAssetAtPath(path) != null) return ToolResult.Error("ASSET_EXISTS", path);
            var resolution = (int?)args["heightmapResolution"] ?? 513;
            if ((resolution - 1 & resolution - 2) != 0) return ToolResult.Error("BAD_RESOLUTION", "heightmapResolution must be 2^n + 1");
            var data = new TerrainData
            {
                name = Path.GetFileNameWithoutExtension(path),
                heightmapResolution = resolution,
                size = JsonHelpers.ParseVector3(args["size"], new Vector3(1000f, 600f, 1000f)),
            };
            try
            {
                SystemToolSupport.EnsureFolder(path);
                AssetDatabase.CreateAsset(data, path);
                Undo.RegisterCreatedObjectUndo(data, "Create TerrainData");
                var gameObject = Terrain.CreateTerrainGameObject(data);
                gameObject.name = (string)args["name"] ?? "Terrain";
                Undo.RegisterCreatedObjectUndo(gameObject, "Create Terrain");
                EditorUtility.SetDirty(data);
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
                return ToolResult.Ok("created Terrain", new JObject
                {
                    ["target"] = ToolTargetRef.Capture(gameObject)?.ToJson(), ["terrainData"] = SystemToolSupport.ObjectJson(data), ["savedAssets"] = false,
                });
            }
            catch (Exception exception)
            {
                if (AssetDatabase.LoadMainAssetAtPath(path) != null) AssetDatabase.DeleteAsset(path);
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
        public ToolResult Execute(JObject args)
        {
            var gameObject = SystemToolSupport.GameObject(args, out var error);
            if (gameObject == null) return error;
            var terrain = gameObject.GetComponent<Terrain>();
            if (terrain == null || terrain.terrainData == null) return ToolResult.Error("TERRAIN_NOT_FOUND", gameObject.name);
            var rows = (JArray)args["heights"];
            int height = rows.Count;
            int width = ((JArray)rows[0]).Count;
            if (rows.Any(row => !(row is JArray array) || array.Count != width)) return ToolResult.Error("BAD_HEIGHTS", "all rows must have equal width");
            int xBase = (int?)args["xBase"] ?? 0, yBase = (int?)args["yBase"] ?? 0;
            if (xBase + width > terrain.terrainData.heightmapResolution || yBase + height > terrain.terrainData.heightmapResolution)
                return ToolResult.Error("HEIGHT_PATCH_OUT_OF_RANGE", "height patch exceeds heightmapResolution");
            var values = new float[height, width];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) values[y, x] = (float)rows[y][x];
            Undo.RecordObject(terrain.terrainData, "Set Terrain Heights");
            terrain.terrainData.SetHeights(xBase, yBase, values);
            EditorUtility.SetDirty(terrain.terrainData);
            return ToolResult.Ok($"updated {width}x{height} heights", new JObject { ["terrainData"] = SystemToolSupport.ObjectJson(terrain.terrainData), ["savedAssets"] = false });
        }
    }

    public sealed class ListUiDocumentsTool : IMcpTool
    {
        public string Name => "list_ui_documents";
        public string Description => "Lists loaded UI Toolkit UIDocument components and their VisualTreeAsset/PanelSettings bindings.";
        public JObject InputSchema => SystemToolSupport.EmptySchema();
        public bool IsWrite => false;
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
        public ToolResult Execute(JObject args)
        {
            if (!SystemToolSupport.TryAssetPath((string)args["assetPath"], ".asset", out var path, out var error)) return ToolResult.Error("BAD_ARGS", error);
            if (AssetDatabase.LoadMainAssetAtPath(path) != null) return ToolResult.Error("ASSET_EXISTS", path);
            SystemToolSupport.EnsureFolder(path);
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.name = Path.GetFileNameWithoutExtension(path);
            settings.scaleMode = (PanelScaleMode)Enum.Parse(typeof(PanelScaleMode), (string)args["scaleMode"] ?? "ScaleWithScreenSize", true);
            settings.referenceResolution = new Vector2Int(Mathf.RoundToInt((float?)args["referenceWidth"] ?? 1920), Mathf.RoundToInt((float?)args["referenceHeight"] ?? 1080));
            settings.match = (float?)args["match"] ?? 0.5f;
            AssetDatabase.CreateAsset(settings, path);
            Undo.RegisterCreatedObjectUndo(settings, "Create PanelSettings");
            EditorUtility.SetDirty(settings);
            return ToolResult.Ok($"created PanelSettings '{path}'", new JObject { ["panelSettings"] = SystemToolSupport.ObjectJson(settings), ["savedAssets"] = false });
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
        public ToolResult Execute(JObject args)
        {
            var parent = args["parentInstanceId"] != null ? EditorUtility.InstanceIDToObject((int)args["parentInstanceId"]) as GameObject : null;
            var gameObject = new GameObject((string)args["name"] ?? "UI Document");
            var document = gameObject.AddComponent<UIDocument>();
            document.visualTreeAsset = args["uxmlPath"] != null ? AssetDatabase.LoadAssetAtPath<VisualTreeAsset>((string)args["uxmlPath"]) : null;
            document.panelSettings = args["panelSettingsPath"] != null ? AssetDatabase.LoadAssetAtPath<PanelSettings>((string)args["panelSettingsPath"]) : null;
            document.sortingOrder = (float?)args["sortingOrder"] ?? 0f;
            if (args["uxmlPath"] != null && document.visualTreeAsset == null) { UnityEngine.Object.DestroyImmediate(gameObject); return ToolResult.Error("UXML_NOT_FOUND", (string)args["uxmlPath"]); }
            if (args["panelSettingsPath"] != null && document.panelSettings == null) { UnityEngine.Object.DestroyImmediate(gameObject); return ToolResult.Error("PANEL_SETTINGS_NOT_FOUND", (string)args["panelSettingsPath"]); }
            using (var mutation = new MutationScope("Create UIDocument"))
            {
                mutation.RegisterCreated(gameObject);
                if (parent != null) { SceneManager.MoveGameObjectToScene(gameObject, parent.scene); mutation.SetParent(gameObject.transform, parent.transform); }
                mutation.Touch(document);
                mutation.Complete();
            }
            return ToolResult.Ok("created UIDocument", new JObject { ["target"] = ToolTargetRef.Capture(document)?.ToJson() });
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
        public ToolResult Execute(JObject args)
        {
            var path = (string)args["assetPath"];
            if (!SystemToolSupport.TryAssetPath(path, null, out path, out var error)
                || (!path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".uss", StringComparison.OrdinalIgnoreCase)))
                return ToolResult.Error("BAD_ARGS", error ?? "assetPath must be .uxml or .uss");
            var absolute = Path.GetFullPath(Path.Combine(Bootstrap.UnityProjectRoot, path));
            if (!File.Exists(absolute)) return ToolResult.Error("UI_ASSET_NOT_FOUND", path);
            var text = File.ReadAllText(absolute);
            int max = (int?)args["maxCharacters"] ?? 100000;
            return ToolResult.Ok($"read '{path}'", new JObject
            {
                ["assetPath"] = path, ["content"] = text.Length > max ? text.Substring(0, max) : text,
                ["totalCharacters"] = text.Length, ["truncated"] = text.Length > max,
            });
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
        public ToolResult Execute(JObject args)
        {
            var path = (string)args["assetPath"];
            if (!SystemToolSupport.TryAssetPath(path, null, out path, out var error)
                || (!path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".uss", StringComparison.OrdinalIgnoreCase)))
                return ToolResult.Error("BAD_ARGS", error ?? "assetPath must be .uxml or .uss");
            var absolute = Path.GetFullPath(Path.Combine(Bootstrap.UnityProjectRoot, path));
            if (File.Exists(absolute) && !((bool?)args["overwrite"] ?? false)) return ToolResult.Error("ASSET_EXISTS", path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute));
            File.WriteAllText(absolute, (string)args["content"] ?? string.Empty, new System.Text.UTF8Encoding(false));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return ToolResult.Ok($"wrote '{path}'", new JObject
            {
                ["assetPath"] = path, ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["asset"] = SystemToolSupport.ObjectJson(AssetDatabase.LoadMainAssetAtPath(path)),
            });
        }
    }

    public sealed class CreateAnimationClipAssetTool : IMcpTool
    {
        public string Name => "create_animation_clip_asset";
        public string Description => "Creates an empty AnimationClip asset with frame rate and wrap settings; no global save.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["assetPath"] = new JObject { ["type"] = "string" }, ["frameRate"] = new JObject { ["type"] = "number", ["minimum"] = 1, ["maximum"] = 240, ["default"] = 60 },
                ["wrapMode"] = new JObject { ["type"] = "string", ["default"] = "Default" },
            }, ["required"] = new JArray { "assetPath" }, ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            if (!SystemToolSupport.TryAssetPath((string)args["assetPath"], ".anim", out var path, out var error)) return ToolResult.Error("BAD_ARGS", error);
            if (AssetDatabase.LoadMainAssetAtPath(path) != null) return ToolResult.Error("ASSET_EXISTS", path);
            if (!Enum.TryParse((string)args["wrapMode"] ?? "Default", true, out WrapMode wrapMode)) return ToolResult.Error("BAD_WRAP_MODE", (string)args["wrapMode"]);
            SystemToolSupport.EnsureFolder(path);
            var clip = new AnimationClip { name = Path.GetFileNameWithoutExtension(path), frameRate = (float?)args["frameRate"] ?? 60f, wrapMode = wrapMode };
            AssetDatabase.CreateAsset(clip, path);
            Undo.RegisterCreatedObjectUndo(clip, "Create AnimationClip");
            EditorUtility.SetDirty(clip);
            return ToolResult.Ok($"created AnimationClip '{path}'", new JObject { ["clip"] = SystemToolSupport.ObjectJson(clip), ["savedAssets"] = false });
        }
    }

    public sealed class SetAnimationFloatCurveTool : IMcpTool
    {
        public string Name => "set_animation_float_curve";
        public string Description => "Adds, replaces or removes one AnimationClip float curve via AnimationUtility.SetEditorCurve with Undo.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["clipPath"] = new JObject { ["type"] = "string" }, ["relativePath"] = new JObject { ["type"] = "string", ["default"] = "" },
                ["componentType"] = new JObject { ["type"] = "string" }, ["propertyName"] = new JObject { ["type"] = "string" },
                ["keys"] = new JObject
                {
                    ["type"] = "array", ["maxItems"] = 10000,
                    ["items"] = new JObject
                    {
                        ["type"] = "object", ["properties"] = new JObject
                        {
                            ["time"] = new JObject { ["type"] = "number" }, ["value"] = new JObject { ["type"] = "number" },
                            ["inTangent"] = new JObject { ["type"] = "number", ["default"] = 0 }, ["outTangent"] = new JObject { ["type"] = "number", ["default"] = 0 },
                        }, ["required"] = new JArray { "time", "value" }, ["additionalProperties"] = false,
                    },
                },
            }, ["required"] = new JArray { "clipPath", "componentType", "propertyName", "keys" }, ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>((string)args["clipPath"]);
            if (clip == null) return ToolResult.Error("CLIP_NOT_FOUND", (string)args["clipPath"]);
            var type = ComponentTypeInspector.ResolveType((string)args["componentType"]);
            if (type == null || !typeof(Component).IsAssignableFrom(type)) return ToolResult.Error("COMPONENT_TYPE_NOT_FOUND", (string)args["componentType"]);
            var keyTokens = (JArray)args["keys"];
            AnimationCurve curve = null;
            if (keyTokens.Count > 0)
            {
                var keys = keyTokens.Select(token => new Keyframe((float)token["time"], (float)token["value"], (float?)token["inTangent"] ?? 0f, (float?)token["outTangent"] ?? 0f)).ToArray();
                curve = new AnimationCurve(keys);
            }
            Undo.RecordObject(clip, "Set Animation Curve");
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve((string)args["relativePath"] ?? string.Empty, type, (string)args["propertyName"]), curve);
            EditorUtility.SetDirty(clip);
            return ToolResult.Ok(curve == null ? "removed animation curve" : $"set {curve.length} key(s)", new JObject { ["clip"] = SystemToolSupport.ObjectJson(clip), ["savedAssets"] = false });
        }
    }

    public sealed class CreateAvatarMaskTool : IMcpTool
    {
        public string Name => "create_avatar_mask";
        public string Description => "Creates an AvatarMask asset and optionally configures humanoid body-part activation.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["assetPath"] = new JObject { ["type"] = "string" },
                ["activeBodyParts"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["maxItems"] = 32 },
            }, ["required"] = new JArray { "assetPath" }, ["additionalProperties"] = false,
        };
        public bool IsWrite => true;
        public ToolResult Execute(JObject args)
        {
            if (!SystemToolSupport.TryAssetPath((string)args["assetPath"], ".mask", out var path, out var error)) return ToolResult.Error("BAD_ARGS", error);
            if (AssetDatabase.LoadMainAssetAtPath(path) != null) return ToolResult.Error("ASSET_EXISTS", path);
            var active = new HashSet<string>(((JArray)args["activeBodyParts"] ?? new JArray()).Values<string>(), StringComparer.OrdinalIgnoreCase);
            var mask = new AvatarMask { name = Path.GetFileNameWithoutExtension(path) };
            foreach (AvatarMaskBodyPart part in Enum.GetValues(typeof(AvatarMaskBodyPart)))
                if (part != AvatarMaskBodyPart.LastBodyPart) mask.SetHumanoidBodyPartActive(part, active.Count == 0 || active.Contains(part.ToString()));
            SystemToolSupport.EnsureFolder(path);
            AssetDatabase.CreateAsset(mask, path);
            Undo.RegisterCreatedObjectUndo(mask, "Create AvatarMask");
            EditorUtility.SetDirty(mask);
            return ToolResult.Ok($"created AvatarMask '{path}'", new JObject { ["avatarMask"] = SystemToolSupport.ObjectJson(mask), ["savedAssets"] = false });
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
