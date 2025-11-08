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
        private readonly Point _targetTile;
        private readonly float _tolerance;
        private readonly float _speed;
        private MovementHelper.MovementState _movementState;

        public MoveAction(Point targetTile, float tolerance = 1f, float speed = 3f)
        {
            _targetTile = targetTile;
            _tolerance = tolerance;
            _speed = speed;
            _movementState = MovementHelper.MovementState.Create(tolerance * 16f);
        }

        public override string Name => "move";

        public Point TargetTile => _targetTile;

        private Vector2 GetTargetPixelCenter()
        {
            return new Vector2(_targetTile.X * 16f + 8f, _targetTile.Y * 16f + 8f);
        }

        protected override AgentActionResult OnTick(AgentActionContext context)
        {
            if (!ServerAuthority.IsServer)
            {
                return AgentActionResult.Failure("MoveAction must run on the server.");
            }

            var npc = context.Agent;
            var targetPixels = GetTargetPixelCenter();

            if (TerrarAI_Config.Get().EnableCreativeMode)
            {
                npc.Center = targetPixels;
                npc.velocity = Vector2.Zero;
                return AgentActionResult.Success($"Snapped to tile({_targetTile.X},{_targetTile.Y}) (creative mode).");
            }

            var settings = MovementHelper.MovementSettings.Create(_speed, _tolerance * 16f);
            return MovementHelper.MoveTowards(npc, targetPixels, ref _movementState, settings);
        }

        public override void Reset()
        {
            base.Reset();
            _movementState.Reset(_tolerance * 16f);
        }

        public static AgentAction CreateFromParameters(JsonElement parameters, ActionValidator validator)
        {
            var tileX = ActionParameterReader.ReadInt(parameters, "tileX");
            var tileY = ActionParameterReader.ReadInt(parameters, "tileY");
            var clamped = validator.ClampTilePosition(tileX, tileY);

            float tolerance = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("tolerance", out var tol) && tol.ValueKind == JsonValueKind.Number
                ? (float)tol.GetDouble()
                : 1f;

            float speed = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("speed", out var spd) && spd.ValueKind == JsonValueKind.Number
                ? Math.Clamp((float)spd.GetDouble(), 1f, 6f)
                : 3f;

            tolerance = Math.Clamp(tolerance, 0.25f, 4f);

            return new MoveAction(clamped, tolerance, speed);
        }
    }
}
