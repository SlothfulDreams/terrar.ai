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
    }
}
