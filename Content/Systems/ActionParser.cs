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

            // Get claimed trees from MultiAgentCoordinator
            var claimedTrees = MultiAgentCoordinator.GetClaimedTrees();

            // Create WorldContext for natural language parameter support
            var worldContext = new WorldContext(agent, commander, null, null, claimedTrees);

            using var document = JsonDocument.Parse(json);

            // Expect ReAct format: {"observation": "...", "thought": "...", "action": {...}, "complete": false}
            if (!document.RootElement.TryGetProperty("action", out _))
            {
                throw new ActionParserException("Missing 'action' object in response payload.");
            }

            return ParseReActFormat(document.RootElement, validator, worldContext, agent);
        }

        private static IReadOnlyList<AgentAction> ParseReActFormat(JsonElement root, ActionValidator validator, WorldContext worldContext, NPC agent)
        {
            if (!root.TryGetProperty("action", out var actionElement))
            {
                throw new ActionParserException("ReAct format requires an 'action' entry.");
            }

            if (actionElement.ValueKind != JsonValueKind.Object)
            {
                throw new ActionParserException("ReAct responses must provide a single action object. Arrays are no longer supported.");
            }

            LogContextualInfo(root);
            return new[] { ParseSingleAction(actionElement, validator, worldContext, agent) };
        }

        private static AgentAction ParseSingleAction(JsonElement element, ActionValidator validator, WorldContext worldContext, NPC agent)
        {
            if (!element.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                throw new ActionParserException("Action is missing a valid 'type' string.");
            }

            var type = typeElement.GetString()?.ToLowerInvariant() ?? string.Empty;
            var parameters = ActionParameterReader.GetParams(element);
            return ActionRegistry.Create(type, parameters, validator, worldContext, agent);
        }

        private static void LogContextualInfo(JsonElement root)
        {
            if (root.TryGetProperty("observation", out var obsElement) && obsElement.ValueKind == JsonValueKind.String)
            {
                var observation = obsElement.GetString();
                if (!string.IsNullOrWhiteSpace(observation))
                {
                    TerrarAI.LogInfo($"[Agent Observation] {observation}");
                }
            }

            if (root.TryGetProperty("thought", out var thoughtElement) && thoughtElement.ValueKind == JsonValueKind.String)
            {
                var thought = thoughtElement.GetString();
                if (!string.IsNullOrWhiteSpace(thought))
                {
                    TerrarAI.LogInfo($"[Agent Thought] {thought}");
                }
            }
        }
    }

    public sealed class ActionParserException : Exception
    {
        public ActionParserException(string message) : base(message)
        {
        }
    }
}
