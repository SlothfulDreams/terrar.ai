using System;
using System.Text.Json;
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

        protected override AgentActionResult OnTick(AgentActionContext context)
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

            // Jump logic - detect if agent needs to jump over obstacles or onto ledges
            bool movingHorizontally = Math.Abs(desiredVelocityX) > 0.35f;
            bool stuck = movingHorizontally && Math.Abs(npc.velocity.X) < 0.2f;

            if ((stuck || delta.Y < -24f) && movingHorizontally)
            {
                MovementHelper.TryJump(npc, desiredVelocityX, delta.Y);
            }

            return AgentActionResult.Pending($"Moving to {_targetPixels}");
        }

        public override void Reset()
        {
            base.Reset();
            _frameCounter = 0;
            _stagnantFrames = 0;
            _lastPosition = Vector2.Zero;
            _currentTolerance = _tolerance;
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
