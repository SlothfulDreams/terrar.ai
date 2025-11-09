using System;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Actions;
using TerrarAI.Content.NPCs;
using Terraria;

namespace TerrarAI.Content.Systems
{
    internal static class MovementHelper
    {
        internal struct MovementSettings
        {
            public float Speed;
            public float BaseTolerance;
            public int MaxFrames;
            public int MaxStagnantFrames;
            public float ProgressThreshold;
            public int ToleranceStepInterval;
            public float ToleranceStep;

            public static MovementSettings Create(float speed, float tolerance)
            {
                return new MovementSettings
                {
                    Speed = speed,
                    BaseTolerance = tolerance,
                    MaxFrames = 120, // ~2 seconds at 60 FPS before timing out
                    MaxStagnantFrames = 60, // ~1 second of no progress before declaring stall
                    ProgressThreshold = 0.5f,
                    ToleranceStepInterval = 60,
                    ToleranceStep = 8f
                };
            }
        }

        internal struct MovementState
        {
            public int FrameCounter;
            public Vector2 LastPosition;
            public int StagnantFrames;
            public float CurrentTolerance;

            public static MovementState Create(float baseTolerance)
            {
                return new MovementState
                {
                    FrameCounter = 0,
                    LastPosition = Vector2.Zero,
                    StagnantFrames = 0,
                    CurrentTolerance = baseTolerance
                };
            }

            public void Reset(float baseTolerance)
            {
                FrameCounter = 0;
                LastPosition = Vector2.Zero;
                StagnantFrames = 0;
                CurrentTolerance = baseTolerance;
            }
        }

        public static AgentActionResult MoveTowards(NPC npc, Vector2 targetPixels, ref MovementState state, MovementSettings settings)
        {
            if (!ServerAuthority.IsServer)
            {
                return AgentActionResult.Failure("Movement must run on the server.");
            }

            state.FrameCounter++;
            if (state.FrameCounter > settings.MaxFrames)
            {
                int targetTileX = (int)(targetPixels.X / 16f);
                int targetTileY = (int)(targetPixels.Y / 16f);
                return AgentActionResult.Failure($"Movement timed out after {(settings.MaxFrames / 60f):F1}s. Could not reach tile({targetTileX},{targetTileY}).");
            }

            if (state.LastPosition != Vector2.Zero)
            {
                float distanceMoved = Vector2.Distance(state.LastPosition, npc.Center);
                if (distanceMoved < settings.ProgressThreshold)
                {
                    state.StagnantFrames++;
                    if (state.StagnantFrames > settings.MaxStagnantFrames)
                    {
                        int currentTileX = (int)(npc.Center.X / 16f);
                        int currentTileY = (int)(npc.Center.Y / 16f);
                        int targetTileX = (int)(targetPixels.X / 16f);
                        int targetTileY = (int)(targetPixels.Y / 16f);
                        return AgentActionResult.Failure($"Movement stalled near tile({currentTileX},{currentTileY}). Obstacle likely blocking path to tile({targetTileX},{targetTileY}).");
                    }
                }
                else
                {
                    state.StagnantFrames = 0;
                }
            }
            state.LastPosition = npc.Center;

            var delta = targetPixels - npc.Center;
            var distanceSq = delta.LengthSquared();

            if (distanceSq <= state.CurrentTolerance * state.CurrentTolerance)
            {
                // Smooth deceleration to stop
                ApplyFriction(npc, 0.5f);

                // Only declare success when nearly stopped
                if (Math.Abs(npc.velocity.X) < 0.1f && Math.Abs(npc.velocity.Y) < 0.1f)
                {
                    npc.velocity.X = 0f;
                    npc.velocity.Y = 0f;
                    int targetTileX = (int)(targetPixels.X / 16f);
                    int targetTileY = (int)(targetPixels.Y / 16f);
                    return AgentActionResult.Success($"Arrived near tile({targetTileX},{targetTileY})");
                }

                int pendingTileX = (int)(targetPixels.X / 16f);
                int pendingTileY = (int)(targetPixels.Y / 16f);
                return AgentActionResult.Pending($"Stopping at tile({pendingTileX},{pendingTileY})");
            }

            if (state.FrameCounter % settings.ToleranceStepInterval == 0)
            {
                state.CurrentTolerance += settings.ToleranceStep;
            }

            bool onGround = IsOnGround(npc) || IsStandingOnPlatform(npc);

            // Calculate target direction (move toward target or stop)
            int moveDirection = Math.Abs(delta.X) > 1f ? Math.Sign(delta.X) : 0;

            // Apply direct acceleration (Terraria-style - linear and responsive)
            if (moveDirection != 0)
            {
                float acceleration = onGround ? 0.08f : 0.04f;  // Half acceleration in air
                npc.velocity.X += moveDirection * acceleration * settings.Speed;

                // Clamp to max speed
                npc.velocity.X = MathHelper.Clamp(npc.velocity.X, -settings.Speed, settings.Speed);
            }
            else
            {
                // Apply friction when we should stop or are very close to target
                ApplyFriction(npc, 0.85f);
            }

            // Apply friction when changing direction (smoother direction changes)
            if (moveDirection != 0 && Math.Sign(npc.velocity.X) != 0 && Math.Sign(npc.velocity.X) != moveDirection)
            {
                ApplyFriction(npc, 0.7f);
            }

            // Update facing direction
            if (moveDirection != 0)
            {
                npc.direction = moveDirection;
            }

            // Proactive obstacle and gap detection
            bool movingHorizontally = Math.Abs(npc.velocity.X) > 0.5f;

            if (onGround && movingHorizontally && moveDirection != 0)
            {
                bool obstacleAhead = HasObstacleAhead(npc, moveDirection, 20);
                bool gapAhead = HasGapAhead(npc, moveDirection, 24);

                // Get jump multiplier from agent if available (for varied jump heights)
                float jumpMultiplier = 1f;
                if (npc.ModNPC is AIAgentNPC agent)
                {
                    jumpMultiplier = agent.GetJumpMultiplier();
                }

                if (obstacleAhead)
                {
                    // Jump over obstacle
                    TryJump(npc, moveDirection, -24f, jumpMultiplier);
                }
                else if (gapAhead && Math.Abs(delta.X) > 16f)
                {
                    // Jump across gap
                    TryJump(npc, moveDirection, 0f, jumpMultiplier);
                }
            }

            // Fallback: jump when stuck (backup safety)
            int stuckThreshold = Math.Max(20, settings.MaxStagnantFrames / 3);
            bool stuck = movingHorizontally && onGround && state.StagnantFrames > stuckThreshold;

            if (stuck)
            {
                float jumpMultiplier = 1f;
                if (npc.ModNPC is AIAgentNPC agent)
                {
                    jumpMultiplier = agent.GetJumpMultiplier();
                }
                TryJump(npc, moveDirection, 0f, jumpMultiplier);
            }

            return AgentActionResult.Pending($"Moving to tile({(int)(targetPixels.X / 16f)},{(int)(targetPixels.Y / 16f)})");
        }

        /// <summary>
        /// Applies unified friction to NPC velocity.
        /// </summary>
        /// <param name="npc">The NPC to apply friction to</param>
        /// <param name="amount">Friction amount (0-1, where 1 = full friction)</param>
        public static void ApplyFriction(NPC npc, float amount = 1.0f)
        {
            const float FRICTION = 0.7f;
            npc.velocity.X *= MathHelper.Lerp(1.0f, FRICTION, amount);
        }

        public static bool TryJump(NPC npc, float desiredVelocityX, float requiredHeight = 0f, float jumpMultiplier = 1f)
        {
            if (npc == null)
            {
                return false;
            }

            if (!IsOnGround(npc))
            {
                return false;
            }

            int direction = desiredVelocityX >= 0 ? 1 : -1;
            bool obstacleAhead = HasObstacleAhead(npc, direction);
            bool wantsElevation = requiredHeight < -24f;

            if (!obstacleAhead && !wantsElevation)
            {
                return false;
            }

            float jumpVelocity = wantsElevation ? -8f : -6.5f;
            npc.velocity.Y = jumpVelocity * jumpMultiplier;
            return true;
        }

        public static bool IsOnGround(NPC npc)
        {
            if (npc == null)
            {
                return false;
            }

            if (npc.collideY)
            {
                return true;
            }

            var point = npc.BottomLeft + new Vector2(0f, 2f);
            return Collision.SolidCollision(point, npc.width, 2);
        }

        public static bool HasObstacleAhead(NPC npc, int direction, int aheadPixels = 20)
        {
            if (npc == null || direction == 0)
            {
                return false;
            }

            int checkX = (int)((npc.Center.X + direction * aheadPixels) / 16f);
            int footY = (int)((npc.Bottom.Y - 8f) / 16f);

            var tile = Framing.GetTileSafely(checkX, footY);
            var tileAbove = Framing.GetTileSafely(checkX, footY - 1);

            return (tile.HasTile && Main.tileSolid[tile.TileType]) ||
                   (tileAbove.HasTile && Main.tileSolid[tileAbove.TileType]);
        }

        public static bool HasGapAhead(NPC npc, int direction, int aheadPixels = 24)
        {
            if (npc == null || direction == 0)
            {
                return false;
            }

            int checkX = (int)((npc.Center.X + direction * aheadPixels) / 16f);
            int footY = (int)((npc.Bottom.Y) / 16f);

            var floorTile = Framing.GetTileSafely(checkX, footY);
            if (floorTile.HasTile && Main.tileSolid[floorTile.TileType])
            {
                return false;
            }

            for (int i = 1; i <= 3; i++)
            {
                var below = Framing.GetTileSafely(checkX, footY + i);
                if (below.HasTile && Main.tileSolid[below.TileType])
                {
                    return false; // gentle downward slope
                }
            }

            return true;
        }

        private static float CalculateJumpVelocity(float requiredHeight, float gapDistance)
        {
            float baseVelocity = -6.2f;

            if (gapDistance >= 40f)
            {
                baseVelocity -= 2.0f;
            }
            else if (gapDistance >= 28f)
            {
                baseVelocity -= 1.0f;
            }

            if (requiredHeight < -16f)
            {
                float tilesAbove = Math.Clamp((-requiredHeight) / 16f, 1f, 8f);
                baseVelocity -= tilesAbove * 0.35f;
            }

            return MathHelper.Clamp(baseVelocity, -12f, -5f);
        }

        public static bool IsStandingOnPlatform(NPC npc)
        {
            if (npc == null)
            {
                return false;
            }

            int centerX = (int)(npc.Center.X / 16f);
            int footY = (int)((npc.Bottom.Y + 4f) / 16f);

            var tile = Framing.GetTileSafely(centerX, footY);

            return tile.HasTile && Main.tileSolidTop[tile.TileType];
        }

        public static Vector2? FindValidTeleportPosition(Player player, int offsetDistance = 64)
        {
            if (player == null)
            {
                return null;
            }

            int[] offsets = { -offsetDistance, offsetDistance, -offsetDistance * 2, offsetDistance * 2 };

            foreach (int xOffset in offsets)
            {
                Vector2 testPosition = player.Center + new Vector2(xOffset, 0);
                int tileX = (int)(testPosition.X / 16f);
                int tileY = (int)(testPosition.Y / 16f);

                bool validPosition = true;
                for (int y = tileY - 1; y <= tileY + 2; y++)
                {
                    var tile = Framing.GetTileSafely(tileX, y);
                    if (tile.HasTile && Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType])
                    {
                        validPosition = false;
                        break;
                    }
                }

                if (validPosition)
                {
                    return new Vector2(testPosition.X, player.Center.Y);
                }
            }

            return player.Center + new Vector2(-offsetDistance, 0);
        }
    }
}
