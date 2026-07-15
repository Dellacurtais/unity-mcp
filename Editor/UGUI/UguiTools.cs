using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Maicon.SceneAnalyzer.Editor.Authoring;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Mcp;
using Maicon.SceneAnalyzer.Editor.Mcp.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Maicon.SceneAnalyzer.Editor.UGUI
{
    internal static class UguiToolSupport
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
                foreach (var property in requiredExtra) required.Add(property);
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false,
            };
        }

        public static GameObject Resolve(JObject args, out ToolResult error)
        {
            error = null;
            var gameObject = ToolTargetResolver.ResolveGameObject(args, out var message);
            if (gameObject == null) error = ToolResult.Error("TARGET_NOT_FOUND", message);
            return gameObject;
        }

        public static RectTransform CreateRect(string name, Transform parent)
        {
            var gameObject = new GameObject(name, typeof(RectTransform));
            var rect = gameObject.GetComponent<RectTransform>();
            if (parent != null) rect.SetParent(parent, false);
            rect.localScale = Vector3.one;
            return rect;
        }

        public static Font DefaultFont()
        {
            try { return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
            catch { return null; }
        }

        public static JObject ElementJson(Component component)
        {
            var rect = component != null ? component.GetComponent<RectTransform>() : null;
            return new JObject
            {
                ["target"] = ToolTargetRef.Capture(component)?.ToJson(),
                ["gameObject"] = ToolTargetRef.Capture(component != null ? component.gameObject : null)?.ToJson(),
                ["componentType"] = component != null ? component.GetType().FullName : null,
                ["rect"] = rect != null ? new JObject
                {
                    ["anchorMin"] = new JObject { ["x"] = rect.anchorMin.x, ["y"] = rect.anchorMin.y },
                    ["anchorMax"] = new JObject { ["x"] = rect.anchorMax.x, ["y"] = rect.anchorMax.y },
                    ["pivot"] = new JObject { ["x"] = rect.pivot.x, ["y"] = rect.pivot.y },
                    ["anchoredPosition"] = new JObject { ["x"] = rect.anchoredPosition.x, ["y"] = rect.anchoredPosition.y },
                    ["sizeDelta"] = new JObject { ["x"] = rect.sizeDelta.x, ["y"] = rect.sizeDelta.y },
                } : null,
            };
        }

        public static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        public static Component AddInputModule(GameObject eventSystem)
        {
            var inputType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule", false))
                .FirstOrDefault(type => type != null && typeof(BaseInputModule).IsAssignableFrom(type));
            return inputType != null
                ? Undo.AddComponent(eventSystem, inputType)
                : Undo.AddComponent<StandaloneInputModule>(eventSystem);
        }

        public static JObject EventJson(string name, UnityEventBase unityEvent)
        {
            var listeners = new JArray();
            for (int index = 0; index < unityEvent.GetPersistentEventCount(); index++)
            {
                var target = unityEvent.GetPersistentTarget(index);
                listeners.Add(new JObject
                {
                    ["index"] = index,
                    ["methodName"] = unityEvent.GetPersistentMethodName(index),
                    ["target"] = ToolTargetRef.Capture(target)?.ToJson(),
                });
            }
            return new JObject { ["name"] = name, ["persistentListeners"] = listeners };
        }
    }

    public sealed class ListUguiElementsTool : IMcpTool
    {
        public string Name => "list_ugui_elements";
        public string Description => "Lists loaded uGUI Canvas, Graphic, Selectable and layout components with stable target references.";
        public JObject InputSchema => UguiToolSupport.EmptySchema();
        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var elements = UnityEngine.Object.FindObjectsByType<UIBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(component => component is Graphic || component is Selectable || component is LayoutGroup
                    || component is CanvasScaler || component is GraphicRaycaster)
                .ToArray();
            var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            return ToolResult.Ok($"{canvases.Length} Canvas(es), {elements.Length} uGUI component(s)", new JObject
            {
                ["canvases"] = new JArray(canvases.Select(canvas => new JObject
                {
                    ["target"] = ToolTargetRef.Capture(canvas)?.ToJson(),
                    ["renderMode"] = canvas.renderMode.ToString(),
                    ["sortingOrder"] = canvas.sortingOrder,
                    ["targetDisplay"] = canvas.targetDisplay,
                })),
                ["elements"] = new JArray(elements.Select(UguiToolSupport.ElementJson)),
            });
        }
    }

    public sealed class CreateUguiCanvasTool : IMcpTool
    {
        public string Name => "create_ugui_canvas";
        public string Description => "Creates a Canvas with CanvasScaler, GraphicRaycaster and an EventSystem when needed.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["name"] = new JObject { ["type"] = "string", ["default"] = "Canvas" },
                ["renderMode"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray { "ScreenSpaceOverlay", "ScreenSpaceCamera", "WorldSpace" },
                    ["default"] = "ScreenSpaceOverlay",
                },
                ["referenceWidth"] = new JObject { ["type"] = "number", ["minimum"] = 1, ["default"] = 1920 },
                ["referenceHeight"] = new JObject { ["type"] = "number", ["minimum"] = 1, ["default"] = 1080 },
                ["createEventSystem"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["parentInstanceId"] = new JObject { ["type"] = "integer" },
            },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var parent = args["parentInstanceId"] != null
                ? EditorUtility.InstanceIDToObject((int)args["parentInstanceId"]) as GameObject
                : null;
            if (!Enum.TryParse((string)args["renderMode"] ?? "ScreenSpaceOverlay", true, out RenderMode mode))
                return ToolResult.Error("BAD_RENDER_MODE", (string)args["renderMode"]);

            var gameObject = new GameObject((string)args["name"] ?? "Canvas",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = gameObject.GetComponent<Canvas>();
            canvas.renderMode = mode;
            var scaler = gameObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(
                (float?)args["referenceWidth"] ?? 1920f,
                (float?)args["referenceHeight"] ?? 1080f);
            GameObject eventSystemObject = null;
            try
            {
                using (var mutation = new MutationScope("Create uGUI Canvas"))
                {
                    mutation.RegisterCreated(gameObject);
                    if (parent != null)
                    {
                        SceneManager.MoveGameObjectToScene(gameObject, parent.scene);
                        mutation.SetParent(gameObject.transform, parent.transform);
                    }
                    if ((bool?)args["createEventSystem"] ?? true)
                    {
                        var existing = UnityEngine.Object.FindFirstObjectByType<EventSystem>(FindObjectsInactive.Include);
                        if (existing == null)
                        {
                            eventSystemObject = new GameObject("EventSystem", typeof(EventSystem));
                            if (parent != null) SceneManager.MoveGameObjectToScene(eventSystemObject, parent.scene);
                            UguiToolSupport.AddInputModule(eventSystemObject);
                            mutation.RegisterCreated(eventSystemObject);
                        }
                    }
                    mutation.Complete();
                }
                return ToolResult.Ok("created uGUI Canvas", new JObject
                {
                    ["canvas"] = UguiToolSupport.ElementJson(canvas),
                    ["eventSystem"] = eventSystemObject != null
                        ? ToolTargetRef.Capture(eventSystemObject)?.ToJson()
                        : null,
                });
            }
            catch (Exception exception)
            {
                if (gameObject != null) UnityEngine.Object.DestroyImmediate(gameObject);
                if (eventSystemObject != null) UnityEngine.Object.DestroyImmediate(eventSystemObject);
                return ToolResult.Error("CREATE_CANVAS_FAILED", exception.Message);
            }
        }
    }

    public sealed class CreateUguiElementTool : IMcpTool
    {
        public string Name => "create_ugui_element";
        public string Description => "Creates a production-shaped uGUI panel, image, text, button, slider, toggle or scroll view under a RectTransform parent.";
        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["parentInstanceId"] = new JObject { ["type"] = "integer" },
                ["kind"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray { "panel", "image", "text", "button", "slider", "toggle", "scroll_view" },
                },
                ["name"] = new JObject { ["type"] = "string" },
                ["text"] = new JObject { ["type"] = "string" },
                ["width"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["default"] = 160 },
                ["height"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["default"] = 40 },
                ["color"] = SchemaHelpers.ColorSchema,
                ["spritePath"] = new JObject { ["type"] = "string" },
                ["minValue"] = new JObject { ["type"] = "number", ["default"] = 0 },
                ["maxValue"] = new JObject { ["type"] = "number", ["default"] = 1 },
                ["value"] = new JObject { ["type"] = "number", ["default"] = 0 },
                ["isOn"] = new JObject { ["type"] = "boolean", ["default"] = false },
            },
            ["required"] = new JArray { "parentInstanceId", "kind" },
            ["additionalProperties"] = false,
        };
        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var parent = EditorUtility.InstanceIDToObject((int)args["parentInstanceId"]) as GameObject;
            if (parent == null || parent.GetComponent<RectTransform>() == null)
                return ToolResult.Error("PARENT_NOT_RECT_TRANSFORM", "parentInstanceId must resolve to a RectTransform GameObject");
            var kind = (string)args["kind"];
            var name = (string)args["name"] ?? char.ToUpperInvariant(kind[0]) + kind.Substring(1).Replace('_', ' ');
            var rect = UguiToolSupport.CreateRect(name, parent.transform);
            rect.sizeDelta = new Vector2((float?)args["width"] ?? 160f, (float?)args["height"] ?? 40f);
            try
            {
                var primary = Build(kind, rect, args);
                Undo.RegisterCreatedObjectUndo(rect.gameObject, "Create uGUI Element");
                EditorSceneManager.MarkSceneDirty(parent.scene);
                return ToolResult.Ok($"created uGUI {kind}", new JObject { ["element"] = UguiToolSupport.ElementJson(primary) });
            }
            catch (Exception exception)
            {
                UnityEngine.Object.DestroyImmediate(rect.gameObject);
                return ToolResult.Error("CREATE_UI_ELEMENT_FAILED", exception.Message);
            }
        }

        private static Component Build(string kind, RectTransform root, JObject args)
        {
            var color = JsonHelpers.ParseColor(args["color"], kind == "text" ? Color.black : Color.white);
            if (kind == "text")
            {
                var text = root.gameObject.AddComponent<Text>();
                text.font = UguiToolSupport.DefaultFont();
                text.text = (string)args["text"] ?? "Text";
                text.color = color;
                text.alignment = TextAnchor.MiddleCenter;
                text.raycastTarget = false;
                return text;
            }

            var image = root.gameObject.AddComponent<Image>();
            image.color = color;
            if (args["spritePath"] != null)
            {
                image.sprite = AssetDatabase.LoadAssetAtPath<Sprite>((string)args["spritePath"]);
                if (image.sprite == null) throw new InvalidOperationException($"Sprite not found: {(string)args["spritePath"]}");
            }
            if (kind == "panel" || kind == "image") return image;
            if (kind == "button")
            {
                var button = root.gameObject.AddComponent<Button>();
                button.targetGraphic = image;
                var labelRect = UguiToolSupport.CreateRect("Text", root);
                UguiToolSupport.Stretch(labelRect);
                var label = labelRect.gameObject.AddComponent<Text>();
                label.font = UguiToolSupport.DefaultFont();
                label.text = (string)args["text"] ?? "Button";
                label.alignment = TextAnchor.MiddleCenter;
                label.color = Color.black;
                label.raycastTarget = false;
                return button;
            }
            if (kind == "slider")
            {
                var slider = root.gameObject.AddComponent<Slider>();
                slider.targetGraphic = image;
                var fillArea = UguiToolSupport.CreateRect("Fill Area", root);
                UguiToolSupport.Stretch(fillArea);
                fillArea.offsetMin = new Vector2(10, 10);
                fillArea.offsetMax = new Vector2(-10, -10);
                var fill = UguiToolSupport.CreateRect("Fill", fillArea);
                UguiToolSupport.Stretch(fill);
                var fillImage = fill.gameObject.AddComponent<Image>();
                fillImage.color = Color.green;
                var handle = UguiToolSupport.CreateRect("Handle", root);
                handle.sizeDelta = new Vector2(20, 0);
                handle.gameObject.AddComponent<Image>();
                slider.fillRect = fill;
                slider.handleRect = handle;
                slider.minValue = (float?)args["minValue"] ?? 0f;
                slider.maxValue = (float?)args["maxValue"] ?? 1f;
                slider.value = (float?)args["value"] ?? slider.minValue;
                return slider;
            }
            if (kind == "toggle")
            {
                var toggle = root.gameObject.AddComponent<Toggle>();
                toggle.targetGraphic = image;
                toggle.isOn = (bool?)args["isOn"] ?? false;
                var checkmark = UguiToolSupport.CreateRect("Checkmark", root);
                checkmark.anchorMin = new Vector2(0, 0.5f);
                checkmark.anchorMax = new Vector2(0, 0.5f);
                checkmark.pivot = new Vector2(0, 0.5f);
                checkmark.sizeDelta = new Vector2(24, 24);
                var checkImage = checkmark.gameObject.AddComponent<Image>();
                checkImage.color = Color.green;
                toggle.graphic = checkImage;
                var labelRect = UguiToolSupport.CreateRect("Label", root);
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = new Vector2(30, 0);
                labelRect.offsetMax = Vector2.zero;
                var label = labelRect.gameObject.AddComponent<Text>();
                label.font = UguiToolSupport.DefaultFont();
                label.text = (string)args["text"] ?? "Toggle";
                label.color = Color.black;
                label.alignment = TextAnchor.MiddleLeft;
                return toggle;
            }
            if (kind == "scroll_view")
            {
                var scroll = root.gameObject.AddComponent<ScrollRect>();
                var viewport = UguiToolSupport.CreateRect("Viewport", root);
                UguiToolSupport.Stretch(viewport);
                viewport.gameObject.AddComponent<RectMask2D>();
                var viewportImage = viewport.gameObject.AddComponent<Image>();
                viewportImage.color = new Color(1, 1, 1, 0.05f);
                var content = UguiToolSupport.CreateRect("Content", viewport);
                content.anchorMin = new Vector2(0, 1);
                content.anchorMax = new Vector2(1, 1);
                content.pivot = new Vector2(0.5f, 1);
                content.sizeDelta = Vector2.zero;
                scroll.viewport = viewport;
                scroll.content = content;
                scroll.horizontal = false;
                scroll.vertical = true;
                return scroll;
            }
            throw new InvalidOperationException($"Unsupported kind: {kind}");
        }
    }

    public sealed class SetRectTransformTool : IMcpTool
    {
        public string Name => "set_rect_transform";
        public string Description => "Sets a RectTransform anchor preset, pivot, anchored position, size or offsets with Undo.";
        public JObject InputSchema => UguiToolSupport.TargetSchema(new JObject
        {
            ["anchorPreset"] = new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray
                {
                    "TopLeft", "TopCenter", "TopRight", "MiddleLeft", "MiddleCenter", "MiddleRight",
                    "BottomLeft", "BottomCenter", "BottomRight", "StretchAll",
                },
            },
            ["pivot"] = SchemaHelpers.Vec3Schema,
            ["anchoredPosition"] = SchemaHelpers.Vec3Schema,
            ["sizeDelta"] = SchemaHelpers.Vec3Schema,
            ["offsetMin"] = SchemaHelpers.Vec3Schema,
            ["offsetMax"] = SchemaHelpers.Vec3Schema,
        });
        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var gameObject = UguiToolSupport.Resolve(args, out var error);
            if (gameObject == null) return error;
            var rect = gameObject.GetComponent<RectTransform>();
            if (rect == null) return ToolResult.Error("RECT_TRANSFORM_NOT_FOUND", gameObject.name);
            using (var mutation = new MutationScope("Set RectTransform"))
            {
                mutation.Record(rect);
                if (args["anchorPreset"] != null) ApplyPreset(rect, (string)args["anchorPreset"]);
                if (args["pivot"] != null)
                {
                    var value = JsonHelpers.ParseVector3(args["pivot"]);
                    rect.pivot = new Vector2(value.x, value.y);
                }
                if (args["anchoredPosition"] != null)
                {
                    var value = JsonHelpers.ParseVector3(args["anchoredPosition"]);
                    rect.anchoredPosition = new Vector2(value.x, value.y);
                }
                if (args["sizeDelta"] != null)
                {
                    var value = JsonHelpers.ParseVector3(args["sizeDelta"]);
                    rect.sizeDelta = new Vector2(value.x, value.y);
                }
                if (args["offsetMin"] != null)
                {
                    var value = JsonHelpers.ParseVector3(args["offsetMin"]);
                    rect.offsetMin = new Vector2(value.x, value.y);
                }
                if (args["offsetMax"] != null)
                {
                    var value = JsonHelpers.ParseVector3(args["offsetMax"]);
                    rect.offsetMax = new Vector2(value.x, value.y);
                }
                mutation.Complete();
            }
            return ToolResult.Ok("updated RectTransform", UguiToolSupport.ElementJson(rect));
        }

        private static void ApplyPreset(RectTransform rect, string preset)
        {
            var values = new Dictionary<string, Tuple<Vector2, Vector2, Vector2>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TopLeft"] = Tuple.Create(new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1)),
                ["TopCenter"] = Tuple.Create(new Vector2(.5f, 1), new Vector2(.5f, 1), new Vector2(.5f, 1)),
                ["TopRight"] = Tuple.Create(new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1)),
                ["MiddleLeft"] = Tuple.Create(new Vector2(0, .5f), new Vector2(0, .5f), new Vector2(0, .5f)),
                ["MiddleCenter"] = Tuple.Create(new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(.5f, .5f)),
                ["MiddleRight"] = Tuple.Create(new Vector2(1, .5f), new Vector2(1, .5f), new Vector2(1, .5f)),
                ["BottomLeft"] = Tuple.Create(new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0)),
                ["BottomCenter"] = Tuple.Create(new Vector2(.5f, 0), new Vector2(.5f, 0), new Vector2(.5f, 0)),
                ["BottomRight"] = Tuple.Create(new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0)),
                ["StretchAll"] = Tuple.Create(Vector2.zero, Vector2.one, new Vector2(.5f, .5f)),
            };
            var value = values[preset];
            rect.anchorMin = value.Item1;
            rect.anchorMax = value.Item2;
            rect.pivot = value.Item3;
            if (preset == "StretchAll")
            {
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }
        }
    }

    public sealed class ConfigureUguiLayoutTool : IMcpTool
    {
        public string Name => "configure_ugui_layout";
        public string Description => "Adds or replaces a Horizontal, Vertical or Grid LayoutGroup on a RectTransform.";
        public JObject InputSchema => UguiToolSupport.TargetSchema(new JObject
        {
            ["layoutType"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "Horizontal", "Vertical", "Grid" } },
            ["spacing"] = new JObject { ["type"] = "number", ["default"] = 0 },
            ["padding"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 },
            ["cellWidth"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["default"] = 100 },
            ["cellHeight"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["default"] = 100 },
            ["childAlignment"] = new JObject { ["type"] = "string", ["default"] = "UpperLeft" },
        }, "layoutType");
        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var gameObject = UguiToolSupport.Resolve(args, out var error);
            if (gameObject == null) return error;
            if (gameObject.GetComponent<RectTransform>() == null)
                return ToolResult.Error("RECT_TRANSFORM_NOT_FOUND", gameObject.name);
            if (!Enum.TryParse((string)args["childAlignment"] ?? "UpperLeft", true, out TextAnchor alignment))
                return ToolResult.Error("BAD_ALIGNMENT", (string)args["childAlignment"]);
            foreach (var layout in gameObject.GetComponents<LayoutGroup>()) Undo.DestroyObjectImmediate(layout);
            LayoutGroup group;
            if ((string)args["layoutType"] == "Horizontal")
            {
                var horizontal = Undo.AddComponent<HorizontalLayoutGroup>(gameObject);
                horizontal.spacing = (float?)args["spacing"] ?? 0;
                group = horizontal;
            }
            else if ((string)args["layoutType"] == "Vertical")
            {
                var vertical = Undo.AddComponent<VerticalLayoutGroup>(gameObject);
                vertical.spacing = (float?)args["spacing"] ?? 0;
                group = vertical;
            }
            else
            {
                var grid = Undo.AddComponent<GridLayoutGroup>(gameObject);
                grid.spacing = Vector2.one * ((float?)args["spacing"] ?? 0);
                grid.cellSize = new Vector2((float?)args["cellWidth"] ?? 100, (float?)args["cellHeight"] ?? 100);
                group = grid;
            }
            group.childAlignment = alignment;
            var padding = (int?)args["padding"] ?? 0;
            group.padding = new RectOffset(padding, padding, padding, padding);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return ToolResult.Ok($"configured {group.GetType().Name}", UguiToolSupport.ElementJson(group));
        }
    }

    public sealed class InspectUguiEventsTool : IMcpTool
    {
        public string Name => "inspect_ugui_events";
        public string Description => "Lists persistent UnityEvent listeners on a Button, Toggle, Slider or ScrollRect.";
        public JObject InputSchema => UguiToolSupport.TargetSchema();
        public bool IsWrite => false;

        public ToolResult Execute(JObject args)
        {
            var gameObject = UguiToolSupport.Resolve(args, out var error);
            if (gameObject == null) return error;
            var events = new JArray();
            var button = gameObject.GetComponent<Button>();
            if (button != null) events.Add(UguiToolSupport.EventJson("Button.onClick", button.onClick));
            var toggle = gameObject.GetComponent<Toggle>();
            if (toggle != null) events.Add(UguiToolSupport.EventJson("Toggle.onValueChanged", toggle.onValueChanged));
            var slider = gameObject.GetComponent<Slider>();
            if (slider != null) events.Add(UguiToolSupport.EventJson("Slider.onValueChanged", slider.onValueChanged));
            var scroll = gameObject.GetComponent<ScrollRect>();
            if (scroll != null) events.Add(UguiToolSupport.EventJson("ScrollRect.onValueChanged", scroll.onValueChanged));
            return ToolResult.Ok($"{events.Count} event(s)", new JObject
            {
                ["target"] = ToolTargetRef.Capture(gameObject)?.ToJson(),
                ["events"] = events,
            });
        }
    }

    public sealed class BindButtonClickTool : IMcpTool
    {
        public string Name => "bind_button_click";
        public string Description => "Adds a persistent no-argument void listener to Button.onClick using an explicit component target and method.";
        public JObject InputSchema => UguiToolSupport.TargetSchema(new JObject
        {
            ["listenerInstanceId"] = new JObject { ["type"] = "integer" },
            ["componentType"] = new JObject { ["type"] = "string" },
            ["methodName"] = new JObject { ["type"] = "string" },
        }, "listenerInstanceId", "componentType", "methodName");
        public bool IsWrite => true;

        public ToolResult Execute(JObject args)
        {
            var gameObject = UguiToolSupport.Resolve(args, out var error);
            if (gameObject == null) return error;
            var button = gameObject.GetComponent<Button>();
            if (button == null) return ToolResult.Error("BUTTON_NOT_FOUND", gameObject.name);
            var listenerObject = EditorUtility.InstanceIDToObject((int)args["listenerInstanceId"]) as GameObject;
            if (listenerObject == null)
                return ToolResult.Error("LISTENER_NOT_FOUND", "listenerInstanceId must reference a GameObject");
            var type = ComponentTypeInspector.ResolveType((string)args["componentType"]);
            var component = type != null ? listenerObject.GetComponent(type) : null;
            if (component == null) return ToolResult.Error("LISTENER_COMPONENT_NOT_FOUND", (string)args["componentType"]);
            var method = type.GetMethod((string)args["methodName"], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            if (method == null || method.ReturnType != typeof(void))
                return ToolResult.Error("LISTENER_METHOD_INVALID", "method must be instance void with no parameters");
            var action = (UnityAction)Delegate.CreateDelegate(typeof(UnityAction), component, method, false);
            if (action == null) return ToolResult.Error("LISTENER_BIND_FAILED", method.Name);
            Undo.RecordObject(button, "Bind Button Click");
            UnityEventTools.AddPersistentListener(button.onClick, action);
            EditorUtility.SetDirty(button);
            EditorSceneManager.MarkSceneDirty(button.gameObject.scene);
            return ToolResult.Ok($"bound {type.Name}.{method.Name}", new JObject
            {
                ["event"] = UguiToolSupport.EventJson("Button.onClick", button.onClick),
            });
        }
    }
}
