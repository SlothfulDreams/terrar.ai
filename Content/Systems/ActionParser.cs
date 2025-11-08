using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Actions;
using Terraria;

namespace TerrarAI.Content.Systems
{
    public static class ActionParser
    {
        public static IReadOnlyList<AgentAction> Parse(string json, NPC agent, ActionValidator validator, Player? commander = null)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new ActionParserException("JSON payload was empty.");
            }

            if (agent == null)
            {
                throw new ArgumentNullException(nameof(agent));
            }

            validator.EnsureServerOrThrow();

            using var document = JsonDocument.Parse(json);

            // Try ReAct format first: {"observation": "...", "thought": "...", "action": {...}, "complete": false}
            if (document.RootElement.TryGetProperty("action", out var actionElement))
            {
                return ParseReActFormat(document.RootElement, validator);
            }

            // Fallback to legacy format: {"actions": [{...}, {...}]}
            if (!document.RootElement.TryGetProperty("actions", out var actionsElement) || actionsElement.ValueKind != JsonValueKind.Array)
            {
                throw new ActionParserException("Expected 'action' object (ReAct format) or 'actions' array (legacy format) at the root of the JSON payload.");
            }

            var actions = new List<AgentAction>(actionsElement.GetArrayLength());

            foreach (var element in actionsElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw new ActionParserException("Each action must be a JSON object.");
                }

                if (!element.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                {
                    throw new ActionParserException("Action is missing a valid 'type' string.");
                }

                var type = typeElement.GetString()?.ToLowerInvariant() ?? string.Empty;
                var parameters = GetParams(element);

                var action = CreateAction(type, parameters, validator);
                actions.Add(action);
            }

            if (actions.Count == 0)
            {
                throw new ActionParserException("No actions were provided.");
            }

            return actions;
        }

        private static IReadOnlyList<AgentAction> ParseReActFormat(JsonElement root, ActionValidator validator)
        {
            // Extract observation and thought (optional, for logging/debugging)
            var observation = root.TryGetProperty("observation", out var obsElement) && obsElement.ValueKind == JsonValueKind.String
                ? obsElement.GetString()
                : null;

            var thought = root.TryGetProperty("thought", out var thoughtElement) && thoughtElement.ValueKind == JsonValueKind.String
                ? thoughtElement.GetString()
                : null;

            // Log the agent's reasoning if available
            if (!string.IsNullOrWhiteSpace(thought))
            {
                TerrarAI.LogInfo($"[Agent Thought] {thought}");
            }

            // Parse the single action
            if (!root.TryGetProperty("action", out var actionElement) || actionElement.ValueKind != JsonValueKind.Object)
            {
                throw new ActionParserException("ReAct format requires an 'action' object.");
            }

            if (!actionElement.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                throw new ActionParserException("Action is missing a valid 'type' string.");
            }

            var type = typeElement.GetString()?.ToLowerInvariant() ?? string.Empty;
            var parameters = GetParams(actionElement);

            var action = CreateAction(type, parameters, validator);

            return new List<AgentAction> { action };
        }

        private static AgentAction CreateAction(string type, JsonElement parameters, ActionValidator validator)
        {
            return type switch
            {
                "say" => new SayAction(ReadString(parameters, "text", required: true)),
                "move" => CreateMoveAction(parameters, validator),
                "mine" => CreateMineAction(parameters, validator),
                "place" => CreatePlaceAction(parameters, validator),
                "complete" => new CompleteAction(ReadString(parameters, "message", required: false)),
                _ => throw new ActionParserException($"Unknown action type '{type}'.")
            };
        }

        private static AgentAction CreateMoveAction(JsonElement parameters, ActionValidator validator)
        {
            var x = ReadNumber(parameters, "x");
            var y = ReadNumber(parameters, "y");
            var clamped = validator.ClampPixelPosition(x, y);
            return new MoveAction(clamped);
        }

        private static AgentAction CreateMineAction(JsonElement parameters, ActionValidator validator)
        {
            var tileX = ReadInt(parameters, "tileX");
            var tileY = ReadInt(parameters, "tileY");
            var clamped = validator.ClampTilePosition(tileX, tileY);
            return new MineAction(clamped);
        }

        private static AgentAction CreatePlaceAction(JsonElement parameters, ActionValidator validator)
        {
            var tileX = ReadInt(parameters, "tileX");
            var tileY = ReadInt(parameters, "tileY");
            var blockType = ReadInt(parameters, "blockType");

            var clamped = validator.ClampTilePosition(tileX, tileY);
            var validatedBlock = validator.ValidateBlockType(blockType);
            return new PlaceBlockAction(clamped, validatedBlock);
        }

        private static JsonElement GetParams(JsonElement actionElement)
        {
            if (actionElement.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object)
            {
                return parameters;
            }

            return default;
        }

        private static string ReadString(JsonElement element, string propertyName, bool required)
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

        private static float ReadNumber(JsonElement element, string propertyName)
        {
            if (element.ValueKind == JsonValueKind.Undefined || !element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Number)
            {
                throw new ActionParserException($"Missing numeric property '{propertyName}'.");
            }

            return (float)prop.GetDouble();
        }

        private static int ReadInt(JsonElement element, string propertyName)
        {
            return (int)Math.Round(ReadNumber(element, propertyName));
        }
    }

    public sealed class ActionParserException : Exception
    {
        public ActionParserException(string message) : base(message)
        {
        }
    }
}
