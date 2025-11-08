using Microsoft.Xna.Framework;
using TerrarAI.Content.Systems;
using Terraria;

namespace TerrarAI.Content.Actions
{
    public sealed class MoveAction : AgentAction
    {
        private readonly Vector2 _targetPixels;
        private readonly float _tolerance;

        public MoveAction(Vector2 targetPixels, float tolerance = 32f)
        {
            _targetPixels = targetPixels;
            _tolerance = tolerance;
        }

        public override string Name => "move";

        public Vector2 TargetPosition => _targetPixels;

        public override AgentActionResult Execute(AgentActionContext context)
        {
            if (!ServerAuthority.IsServer)
            {
                return AgentActionResult.Failure("MoveAction must run on the server.");
            }

            var npc = context.Agent;
            var delta = _targetPixels - npc.Center;
            var distanceSq = delta.LengthSquared();

            if (distanceSq <= _tolerance * _tolerance)
            {
                npc.velocity *= 0.5f;
                return AgentActionResult.Success($"Arrived at {_targetPixels}");
            }

            npc.direction = delta.X >= 0 ? 1 : -1;
            npc.directionY = delta.Y >= 0 ? 1 : -1;

            return AgentActionResult.Pending($"Moving to {_targetPixels}");
        }
    }
}
