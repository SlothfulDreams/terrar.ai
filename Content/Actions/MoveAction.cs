using Microsoft.Xna.Framework;
using TerrarAI.Content.Systems;
using Terraria;

namespace TerrarAI.Content.Actions
{
    public sealed class MoveAction : AgentAction
    {
        private readonly Vector2 _targetPixels;
        private readonly float _speed;
        private readonly float _tolerance;

        public MoveAction(Vector2 targetPixels, float speed = 3.5f, float tolerance = 12f)
        {
            _targetPixels = targetPixels;
            _speed = speed;
            _tolerance = tolerance;
        }

        public override string Name => "move";

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
                npc.Center = _targetPixels;
                return AgentActionResult.Success($"Arrived near {_targetPixels}");
            }

            var desiredVelocity = SafeNormalize(delta) * _speed;
            npc.velocity = Vector2.Lerp(npc.velocity, desiredVelocity, 0.35f);
            npc.direction = desiredVelocity.X >= 0 ? 1 : -1;

            return AgentActionResult.Pending($"Moving to {_targetPixels}");
        }

        private static Vector2 SafeNormalize(Vector2 vector)
        {
            if (vector == Vector2.Zero)
            {
                return Vector2.Zero;
            }

            vector.Normalize();
            return vector;
        }
    }
}
