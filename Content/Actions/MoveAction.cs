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
        private readonly float _speed;

        // Timeout tracking
        private int _frameCounter = 0;
        private const int MAX_MOVEMENT_FRAMES = 600; // 10 seconds at 60 FPS

        // Stagnation detection
        private Vector2 _lastPosition;
        private int _stagnantFrames = 0;
        private const float PROGRESS_THRESHOLD = 0.5f; // pixels per frame
        private const int MAX_STAGNANT_FRAMES = 120; // 2 seconds at 60 FPS

        // Adaptive tolerance
        private float _currentTolerance;

        public MoveAction(Vector2 targetPixels, float tolerance = 32f, float speed = 4f)
        {
            _targetPixels = targetPixels;
            _tolerance = tolerance;
            _speed = speed;
            _currentTolerance = tolerance;
            _lastPosition = Vector2.Zero;
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

            // Layer 1: Frame counter timeout (primary protection)
            _frameCounter++;
            if (_frameCounter > MAX_MOVEMENT_FRAMES)
            {
                return AgentActionResult.Failure($"Movement timed out after 10 seconds. Could not reach {_targetPixels}.");
            }

            // Layer 2: Stagnation detection (obstacle detection)
            if (_lastPosition != Vector2.Zero) // Skip first frame
            {
                float distanceMoved = Vector2.Distance(_lastPosition, npc.Center);
                if (distanceMoved < PROGRESS_THRESHOLD)
                {
                    _stagnantFrames++;
                    if (_stagnantFrames > MAX_STAGNANT_FRAMES)
                    {
                        return AgentActionResult.Failure(
                            $"Movement stalled near {npc.Center}. Obstacle likely blocking path to {_targetPixels}.");
                    }
                }
                else
                {
                    _stagnantFrames = 0; // Reset stagnation counter if making progress
                }
            }
            _lastPosition = npc.Center;

            // Layer 3: Adaptive tolerance (progressive relaxation)
            if (_frameCounter % 120 == 0) // Every 2 seconds
            {
                _currentTolerance += 16f; // Increase tolerance by 1 tile
            }

            var delta = _targetPixels - npc.Center;
            var distanceSq = delta.LengthSquared();

            // Check if arrived at target (using adaptive tolerance)
            if (distanceSq <= _currentTolerance * _currentTolerance)
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

        public override void Reset()
        {
            _frameCounter = 0;
            _stagnantFrames = 0;
            _lastPosition = Vector2.Zero;
            _currentTolerance = _tolerance;
        }
    }
}
