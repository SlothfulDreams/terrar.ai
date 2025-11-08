using Microsoft.Xna.Framework;
using Terraria;

namespace TerrarAI.Content.Systems
{
    internal static class MovementHelper
    {
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

        public static bool TryJump(NPC npc, float desiredVelocityX, float requiredHeight = 0f)
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
            bool wantsElevation = requiredHeight < -24f; // target is above agent

            if (!obstacleAhead && !wantsElevation)
            {
                return false;
            }

            float jumpVelocity = wantsElevation ? -8f : -6.5f;
            npc.velocity.Y = jumpVelocity;
            return true;
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
