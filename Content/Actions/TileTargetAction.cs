using Microsoft.Xna.Framework;
using Terraria;

namespace TerrarAI.Content.Actions
{
    /// <summary>
    /// Base class for tile-centric actions that automatically exposes tile targets and helpers.
    /// </summary>
    public abstract class TileTargetAction : AgentAction
    {
        protected TileTargetAction(Point tile)
        {
            Tile = tile;
        }

        protected Point Tile { get; }

        public override Point? GetTargetTile() => Tile;

        protected Vector2 GetTileWorldCenter()
        {
            return new Vector2(Tile.X * 16f + 8f, Tile.Y * 16f + 8f);
        }

        protected float GetDistanceToAgent(NPC agent)
        {
            return Vector2.Distance(agent.Center, GetTileWorldCenter());
        }
    }
}
