using System;
using Maicon.SceneAnalyzer.Editor.Core;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Mcp.Tools
{
    internal static class ReadHelpers
    {
        public const int DefaultMaxArrayItems = 16;

        public static JObject SerializeComponent(Component component, int maxArrayItems = DefaultMaxArrayItems)
        {
            if (component == null) return new JObject { ["missing"] = true };
            var root = new JObject
            {
                ["componentType"] = component.GetType().FullName,
                ["instanceId"] = component.GetInstanceID(),
                ["target"] = ToolTargetRef.Capture(component)?.ToJson(),
                ["enabled"] = component is Behaviour behaviour ? behaviour.enabled : (JToken)true,
            };

            var serializedObject = new SerializedObject(component);
            var iterator = serializedObject.GetIterator();
            var enterChildren = true;
            var properties = new JObject();
            var errors = new JArray();
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.name == "m_Script") continue;
                try
                {
                    properties[iterator.name] = SerializedValueCodec.Read(iterator, new SerializedValueCodec.ReadOptions
                    {
                        MaxArrayItems = Math.Max(1, maxArrayItems),
                        MaxDepth = 4,
                    });
                }
                catch (Exception e)
                {
                    properties[iterator.name] = new JObject
                    {
                        ["unsupported"] = true,
                        ["propertyType"] = iterator.propertyType.ToString(),
                        ["error"] = e.Message,
                    };
                    errors.Add(new JObject { ["property"] = iterator.propertyPath, ["error"] = e.Message });
                }
            }
            root["properties"] = properties;
            if (errors.Count > 0) root["serializationErrors"] = errors;
            return root;
        }

        public static JToken SerializeProperty(SerializedProperty property, int maxArrayItems)
        {
            return SerializedValueCodec.Read(property, new SerializedValueCodec.ReadOptions
            {
                MaxArrayItems = Math.Max(1, maxArrayItems),
                MaxDepth = 4,
            });
        }
    }
}
