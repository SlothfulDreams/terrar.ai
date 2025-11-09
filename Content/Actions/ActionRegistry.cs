using System;
using System.Collections.Generic;
using System.Text.Json;
using TerrarAI.Content.Systems;
using Terraria;

namespace TerrarAI.Content.Actions
{
    public delegate AgentAction ActionFactory(JsonElement parameters, ActionValidator validator);
    public delegate AgentAction ExtendedActionFactory(JsonElement parameters, ActionValidator validator, WorldContext? worldContext, NPC? agent);

    public static class ActionRegistry
    {
        private static readonly Dictionary<string, ActionFactory> _factories = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ExtendedActionFactory> _extendedFactories = new(StringComparer.OrdinalIgnoreCase);
        private static bool _initialized;

        public static void Register(string actionType, ActionFactory factory)
        {
            if (string.IsNullOrWhiteSpace(actionType))
            {
                throw new ArgumentException("Action type cannot be null or whitespace.", nameof(actionType));
            }

            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            _factories[actionType] = factory;
        }

        public static void RegisterExtended(string actionType, ExtendedActionFactory factory)
        {
            if (string.IsNullOrWhiteSpace(actionType))
            {
                throw new ArgumentException("Action type cannot be null or whitespace.", nameof(actionType));
            }

            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            _extendedFactories[actionType] = factory;
        }

        public static AgentAction Create(string actionType, JsonElement parameters, ActionValidator validator, WorldContext? worldContext = null, NPC? agent = null)
        {
            EnsureInitialized();

            // Try extended factory first (supports natural language parameters)
            if (_extendedFactories.TryGetValue(actionType, out var extendedFactory))
            {
                return extendedFactory(parameters, validator, worldContext, agent);
            }

            // Fall back to standard factory
            if (!_factories.TryGetValue(actionType, out var factory))
            {
                throw new ActionParserException($"Unknown action type '{actionType}'.");
            }

            return factory(parameters, validator);
        }

        private static void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }

            Register("move", MoveAction.CreateFromParameters);
            RegisterExtended("mine", MineAction.CreateFromParameters);
            Register("chop", ChopAction.CreateFromParameters);
            Register("hellevator", HellevatorAction.CreateFromParameters);
            Register("build", BuildAction.CreateFromParameters);
            Register("say", SayAction.CreateFromParameters);
            Register("complete", CompleteAction.CreateFromParameters);

            _initialized = true;
        }
    }
}
