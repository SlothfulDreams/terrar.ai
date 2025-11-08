using System;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Actions;
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
                    MaxFrames = 600,
                    MaxStagnantFrames = 120,
                    ProgressThreshold = 0.5f,
                    ToleranceStepInterval = 120,
                    ToleranceStep = 16f
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
                return AgentActionResult.Failure($"Movement timed out after {(settings.MaxFrames / 60f):F1}s. Could not reach {targetPixels}.");
            }

            if (state.LastPosition != Vector2.Zero)
            {
                float distanceMoved = Vector2.Distance(state.LastPosition, npc.Center);
                if (distanceMoved < settings.ProgressThreshold)
                {
                    state.StagnantFrames++;
                    if (state.StagnantFrames > settings.MaxStagnantFrames)
                    {
                        return AgentActionResult.Failure($"Movement stalled near {npc.Center}. Obstacle likely blocking path to {targetPixels}.");
                    }
                }
                else
                {
                    state.StagnantFrames = 0;
                }
            }
            state.LastPosition = npc.Center;

            if (state.FrameCounter % settings.ToleranceStepInterval == 0)
            {
                state.CurrentTolerance += settings.ToleranceStep;
            }

            var delta = targetPixels - npc.Center;
            var distanceSq = delta.LengthSquared();

            if (distanceSq <= state.CurrentTolerance * state.CurrentTolerance)
            {
                npc.velocity.X = 0f;
                npc.velocity.Y = 0f;
                return AgentActionResult.Success($"Arrived near {targetPixels}");
            }

            float desiredVelocityX = MathHelper.Clamp(delta.X / 10f, -settings.Speed, settings.Speed);
            npc.velocity.X = MathHelper.Lerp(npc.velocity.X, desiredVelocityX, 0.35f);
            npc.direction = desiredVelocityX >= 0 ? 1 : -1;

            bool movingHorizontally = Math.Abs(desiredVelocityX) > 0.35f;
            bool stuck = movingHorizontally && Math.Abs(npc.velocity.X) < 0.2f;

            if ((stuck || delta.Y < -24f) && movingHorizontally)
            {
                TryJump(npc, desiredVelocityX, delta.Y);
            }

            return AgentActionResult.Pending($"Moving to {targetPixels}");
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

            return !floorTile.HasTile || !Main.tileSolid[floorTile.TileType];
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
