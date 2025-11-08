using System;
using System.Collections.Generic;
using System.Text.Json;
using TerrarAI.Content.Systems;

namespace TerrarAI.Content.Actions
{
    public delegate AgentAction ActionFactory(JsonElement parameters, ActionValidator validator);

    public static class ActionRegistry
    {
        private static readonly Dictionary<string, ActionFactory> _factories = new(StringComparer.OrdinalIgnoreCase);
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

        public static AgentAction Create(string actionType, JsonElement parameters, ActionValidator validator)
        {
            EnsureInitialized();

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
            Register("mine", MineAction.CreateFromParameters);
            Register("place", PlaceBlockAction.CreateFromParameters);
            Register("say", SayAction.CreateFromParameters);
            Register("complete", CompleteAction.CreateFromParameters);

            _initialized = true;
        }
    }
}
