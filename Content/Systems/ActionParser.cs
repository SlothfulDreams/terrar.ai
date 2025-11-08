using System;
using System.Collections.Generic;
using System.Text.Json;
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

            // Create WorldContext for natural language parameter support
            var worldContext = new WorldContext(agent);

            using var document = JsonDocument.Parse(json);

            // Try ReAct format first: {"observation": "...", "thought": "...", "action": {...}, "complete": false}
            if (document.RootElement.TryGetProperty("action", out var actionElement))
            {
                return ParseReActFormat(document.RootElement, validator, worldContext, agent);
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
                var parameters = ActionParameterReader.GetParams(element);

                var action = ActionRegistry.Create(type, parameters, validator, worldContext, agent);
                actions.Add(action);
            }

            if (actions.Count == 0)
            {
                throw new ActionParserException("No actions were provided.");
            }

            return actions;
        }

        private static IReadOnlyList<AgentAction> ParseReActFormat(JsonElement root, ActionValidator validator, WorldContext worldContext, NPC agent)
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
            var parameters = ActionParameterReader.GetParams(actionElement);

            var action = ActionRegistry.Create(type, parameters, validator, worldContext, agent);

            return new List<AgentAction> { action };
        }
    }

    public sealed class ActionParserException : Exception
    {
        public ActionParserException(string message) : base(message)
        {
        }
    }
}
