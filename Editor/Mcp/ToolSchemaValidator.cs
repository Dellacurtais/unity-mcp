using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Maicon.SceneAnalyzer.Editor.Mcp
{
    public static class ToolSchemaValidator
    {
        private static readonly HashSet<string> SupportedTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "null", "object", "array", "string", "boolean", "integer", "number",
        };

        public static bool ValidateSchema(JObject schema, out string error)
        {
            var errors = new List<string>();
            ValidateSchemaNode(schema, "$", errors);
            error = string.Join("; ", errors);
            return errors.Count == 0;
        }

        public static bool ValidateArguments(JObject arguments, JObject schema, out string error)
        {
            var errors = new List<string>();
            ValidateValue(arguments ?? new JObject(), schema ?? new JObject { ["type"] = "object" }, "$", errors);
            error = string.Join("; ", errors);
            return errors.Count == 0;
        }

        private static void ValidateSchemaNode(JObject schema, string path, List<string> errors)
        {
            if (schema == null) { errors.Add(path + ": schema must be an object"); return; }
            ValidateTypeDeclaration(schema["type"], path + ".type", errors);
            var properties = schema["properties"] as JObject;
            if (schema["properties"] != null && properties == null)
                errors.Add(path + ".properties: must be an object");
            if (properties != null)
                foreach (var property in properties.Properties())
                {
                    if (!(property.Value is JObject child)) errors.Add(path + ".properties." + property.Name + ": must be an object");
                    else ValidateSchemaNode(child, path + ".properties." + property.Name, errors);
                }

            if (schema["required"] != null && !(schema["required"] is JArray))
                errors.Add(path + ".required: must be an array");
            if (schema["required"] is JArray required)
            {
                var seenRequired = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in required)
                {
                    if (item.Type != JTokenType.String) errors.Add(path + ".required: entries must be strings");
                    else if (!seenRequired.Add((string)item)) errors.Add(path + ".required: duplicate property '" + item + "'");
                    else if (properties == null || properties[(string)item] == null) errors.Add(path + ".required: unknown property '" + item + "'");
                }
            }

            if (AllowsType(schema["type"], "array") && schema["items"] == null)
                errors.Add(path + ": array schema requires items");
            if (schema["items"] != null && !(schema["items"] is JObject))
                errors.Add(path + ".items: must be an object");
            if (schema["items"] is JObject items) ValidateSchemaNode(items, path + ".items", errors);
            if (schema["enum"] != null && (!(schema["enum"] is JArray enumValues) || enumValues.Count == 0))
                errors.Add(path + ".enum: must be a non-empty array");
            if (schema["additionalProperties"] != null && schema["additionalProperties"].Type != JTokenType.Boolean)
                errors.Add(path + ".additionalProperties: only boolean values are supported");

            foreach (var keyword in new[] { "oneOf", "anyOf", "allOf" })
            {
                if (schema[keyword] == null) continue;
                if (!(schema[keyword] is JArray variants))
                {
                    errors.Add(path + "." + keyword + ": must be an array");
                    continue;
                }
                if (variants.Count == 0) errors.Add(path + "." + keyword + ": must not be empty");
                for (int index = 0; index < variants.Count; index++)
                {
                    if (variants[index] is JObject variant) ValidateSchemaNode(variant, path + "." + keyword + "[" + index + "]", errors);
                    else errors.Add(path + "." + keyword + "[" + index + "]: must be an object");
                }
            }
        }

        private static void ValidateTypeDeclaration(JToken declaration, string path, List<string> errors)
        {
            if (declaration == null) return;
            if (declaration.Type == JTokenType.String)
            {
                var type = (string)declaration;
                if (!SupportedTypes.Contains(type)) errors.Add(path + ": unsupported type '" + type + "'");
                return;
            }
            if (declaration is JArray types)
            {
                if (types.Count == 0) errors.Add(path + ": type array must not be empty");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int index = 0; index < types.Count; index++)
                {
                    if (types[index].Type != JTokenType.String)
                    {
                        errors.Add(path + "[" + index + "]: type name must be a string");
                        continue;
                    }
                    var type = (string)types[index];
                    if (!SupportedTypes.Contains(type)) errors.Add(path + "[" + index + "]: unsupported type '" + type + "'");
                    else if (!seen.Add(type)) errors.Add(path + "[" + index + "]: duplicate type '" + type + "'");
                }
                return;
            }
            errors.Add(path + ": type must be a string or array of strings");
        }

        private static void ValidateValue(JToken value, JObject schema, string path, List<string> errors)
        {
            if (schema == null) return;
            if (schema["oneOf"] is JArray oneOf && MatchingVariantCount(value, oneOf) != 1) errors.Add(path + ": does not match exactly one allowed shape");
            if (schema["anyOf"] is JArray anyOf && !AnyVariant(value, anyOf)) errors.Add(path + ": does not match any allowed shape");
            if (schema["allOf"] is JArray allOf)
                foreach (var variant in allOf.OfType<JObject>()) ValidateValue(value, variant, path, errors);
            if (!MatchesDeclaredType(value, schema["type"])) { errors.Add(path + ": expected " + schema["type"] + ", got " + value.Type); return; }
            if (schema["enum"] is JArray allowed && !allowed.Any(x => JToken.DeepEquals(x, value))) errors.Add(path + ": value is not in enum");

            if (value is JObject obj)
            {
                if ((int?)schema["minProperties"] is int minProperties && obj.Count < minProperties)
                    errors.Add(path + ": too few properties");
                if ((int?)schema["maxProperties"] is int maxProperties && obj.Count > maxProperties)
                    errors.Add(path + ": too many properties");
                var properties = schema["properties"] as JObject ?? new JObject();
                if (schema["required"] is JArray required)
                    foreach (var item in required.Values<string>())
                        if (obj[item] == null) errors.Add(path + ": missing required property '" + item + "'");
                foreach (var property in obj.Properties())
                {
                    if (properties[property.Name] is JObject propertySchema) ValidateValue(property.Value, propertySchema, path + "." + property.Name, errors);
                    else if ((bool?)schema["additionalProperties"] == false) errors.Add(path + ": unknown property '" + property.Name + "'");
                }
            }
            else if (value is JArray array && schema["items"] is JObject itemSchema)
            {
                for (int i = 0; i < array.Count; i++) ValidateValue(array[i], itemSchema, path + "[" + i + "]", errors);
                if ((int?)schema["minItems"] is int minItems && array.Count < minItems) errors.Add(path + ": too few items");
                if ((int?)schema["maxItems"] is int maxItems && array.Count > maxItems) errors.Add(path + ": too many items");
            }

            if (value.Type == JTokenType.String)
            {
                var text = (string)value ?? string.Empty;
                if ((int?)schema["minLength"] is int minLength && text.Length < minLength) errors.Add(path + ": shorter than minLength " + minLength);
                if ((int?)schema["maxLength"] is int maxLength && text.Length > maxLength) errors.Add(path + ": longer than maxLength " + maxLength);
            }

            if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)
            {
                var number = (double)value;
                if ((double?)schema["minimum"] is double min && number < min) errors.Add(path + ": below minimum " + min);
                if ((double?)schema["maximum"] is double max && number > max) errors.Add(path + ": above maximum " + max);
            }
        }

        private static bool AnyVariant(JToken value, JArray variants) => MatchingVariantCount(value, variants) > 0;

        private static int MatchingVariantCount(JToken value, JArray variants)
        {
            int matches = 0;
            foreach (var variant in variants.OfType<JObject>())
            {
                var local = new List<string>();
                ValidateValue(value, variant, "$", local);
                if (local.Count == 0) matches++;
            }
            return matches;
        }

        private static bool MatchesDeclaredType(JToken value, JToken declaration)
        {
            if (declaration == null) return true;
            if (declaration is JArray types)
                return types.Any(item => item.Type == JTokenType.String && MatchesType(value, (string)item));
            return declaration.Type == JTokenType.String && MatchesType(value, (string)declaration);
        }

        private static bool AllowsType(JToken declaration, string type)
        {
            if (declaration is JArray types)
                return types.Any(item => item.Type == JTokenType.String && string.Equals((string)item, type, StringComparison.Ordinal));
            return declaration?.Type == JTokenType.String && string.Equals((string)declaration, type, StringComparison.Ordinal);
        }

        private static bool MatchesType(JToken value, string type)
        {
            switch (type)
            {
                case null: return true;
                case "null": return value.Type == JTokenType.Null;
                case "object": return value.Type == JTokenType.Object;
                case "array": return value.Type == JTokenType.Array;
                case "string": return value.Type == JTokenType.String;
                case "boolean": return value.Type == JTokenType.Boolean;
                case "integer": return value.Type == JTokenType.Integer;
                case "number": return value.Type == JTokenType.Integer || value.Type == JTokenType.Float;
                default: return false;
            }
        }
    }
}
