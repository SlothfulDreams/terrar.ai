using System;
using System.Text.Json;
using Microsoft.Xna.Framework;
using TerrarAI;
using TerrarAI.Content.Systems;
using Terraria;

namespace TerrarAI.Content.Actions
{
    public sealed class MoveAction : AgentAction
    {
        private readonly Vector2 _targetPixels;
        private readonly float _tolerance;
        private readonly float _speed;
        private MovementHelper.MovementState _movementState;

        public MoveAction(Vector2 targetPixels, float tolerance = 32f, float speed = 4f)
        {
            _targetPixels = targetPixels;
            _tolerance = tolerance;
            _speed = speed;
            _movementState = MovementHelper.MovementState.Create(tolerance);
        }

        public override string Name => "move";

        public Vector2 TargetPosition => _targetPixels;

        protected override AgentActionResult OnTick(AgentActionContext context)
        {
            if (!ServerAuthority.IsServer)
            {
                return AgentActionResult.Failure("MoveAction must run on the server.");
            }

            var npc = context.Agent;

            if (TerrarAI_Config.Get().EnableCreativeMode)
            {
                npc.Center = _targetPixels;
                npc.velocity = Vector2.Zero;
                return AgentActionResult.Success($"Snapped to {_targetPixels} (creative mode).");
            }

            var settings = MovementHelper.MovementSettings.Create(_speed, _tolerance);
            return MovementHelper.MoveTowards(npc, _targetPixels, ref _movementState, settings);
        }

        public override void Reset()
        {
            base.Reset();
            _movementState.Reset(_tolerance);
        }

        public static AgentAction CreateFromParameters(JsonElement parameters, ActionValidator validator)
        {
            var x = ActionParameterReader.ReadNumber(parameters, "x");
            var y = ActionParameterReader.ReadNumber(parameters, "y");
            var clamped = validator.ClampPixelPosition(x, y);

            float tolerance = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("tolerance", out var tol) && tol.ValueKind == JsonValueKind.Number
                ? (float)tol.GetDouble()
                : 32f;

            float speed = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("speed", out var spd) && spd.ValueKind == JsonValueKind.Number
                ? Math.Clamp((float)spd.GetDouble(), 1f, 10f)
                : 4f;

            return new MoveAction(clamped, tolerance, speed);
        }
    }
}
