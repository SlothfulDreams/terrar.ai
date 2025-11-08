using System;
using System.Text.Json;

namespace TerrarAI.Content.Systems
{
    internal static class ActionParameterReader
    {
        public static JsonElement GetParams(JsonElement actionElement)
        {
            if (actionElement.ValueKind == JsonValueKind.Object &&
                actionElement.TryGetProperty("params", out var parameters) &&
                parameters.ValueKind == JsonValueKind.Object)
            {
                return parameters;
            }

            return default;
        }

        public static string ReadString(JsonElement element, string propertyName, bool required)
        {
            if (element.ValueKind == JsonValueKind.Undefined)
            {
                if (required)
                {
                    throw new ActionParserException($"Action is missing parameters. Expected '{propertyName}'.");
                }

                return string.Empty;
            }

            if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
            {
                if (required)
                {
                    throw new ActionParserException($"Missing string property '{propertyName}'.");
                }

                return string.Empty;
            }

            return prop.GetString() ?? string.Empty;
        }

        public static float ReadNumber(JsonElement element, string propertyName)
        {
            if (element.ValueKind == JsonValueKind.Undefined ||
                !element.TryGetProperty(propertyName, out var prop) ||
                prop.ValueKind != JsonValueKind.Number)
            {
                throw new ActionParserException($"Missing numeric property '{propertyName}'.");
            }

            return (float)prop.GetDouble();
        }

        public static int ReadInt(JsonElement element, string propertyName)
        {
            return (int)Math.Round(ReadNumber(element, propertyName));
        }
    }
}
