using System;
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

            // Check if arrived at target
            if (distanceSq <= _tolerance * _tolerance)
            {
                npc.velocity.X = 0;  // Stop horizontal movement completely
                npc.velocity.Y = 0;  // Stop any vertical movement
                return AgentActionResult.Success($"Arrived near {_targetPixels}");
            }

            // Horizontal movement (player-like walking)
            float desiredVelocityX = Math.Clamp(delta.X / 10f, -_speed, _speed);
            npc.velocity.X = MathHelper.Lerp(npc.velocity.X, desiredVelocityX, 0.35f);
            npc.direction = desiredVelocityX >= 0 ? 1 : -1;

            // Jump logic - detect if agent needs to jump over obstacles
            bool onGround = Math.Abs(npc.velocity.Y) < 0.1f;
            bool movingHorizontally = Math.Abs(desiredVelocityX) > 0.5f;
            bool stuck = movingHorizontally && Math.Abs(npc.velocity.X) < 0.3f;

            if (stuck && onGround)
            {
                // Check for blocking tile ahead in direction of movement
                int tileX = (int)((npc.Center.X + Math.Sign(desiredVelocityX) * 20) / 16f);
                int tileY = (int)((npc.Bottom.Y - 8) / 16f);

                if (Framing.GetTileSafely(tileX, tileY).HasTile ||
                    Framing.GetTileSafely(tileX, tileY - 1).HasTile)
                {
                    npc.velocity.Y = -6f;  // Jump over obstacle
                }
            }

            // Jump if target is significantly above
            if (delta.Y < -32f && onGround && movingHorizontally)
            {
                npc.velocity.Y = -6f;
            }

            return AgentActionResult.Pending($"Moving to {_targetPixels}");
        }
    }
}
