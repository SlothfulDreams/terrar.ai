using Microsoft.Xna.Framework;
using TerrarAI.Content.Systems;
using Terraria;
using Terraria.ID;

namespace TerrarAI.Content.Actions
{
    public sealed class PlaceBlockAction : AgentAction
    {
        private readonly Point _tile;
        private readonly int _blockType;

        public PlaceBlockAction(Point tile, int blockType)
        {
            _tile = tile;
            _blockType = blockType;
        }

        public override string Name => "place";

        public override float GetRequiredRange() => 80f;  // 5 tiles = 80 pixels (standard player reach)

        public override Point? GetTargetTile() => _tile;

        public override AgentActionResult Execute(AgentActionContext context)
        {
            if (!ServerAuthority.IsServer)
            {
                return AgentActionResult.Failure("PlaceBlockAction must run on the server.");
            }

            var tile = Framing.GetTileSafely(_tile.X, _tile.Y);
            if (tile.HasTile)
            {
                if (tile.TileType == _blockType)
                {
                    return AgentActionResult.Success($"Block already placed at {_tile.X},{_tile.Y}");
                }

                return AgentActionResult.Failure($"Tile {_tile.X},{_tile.Y} is occupied.");
            }

            var placed = WorldGen.PlaceTile(_tile.X, _tile.Y, _blockType, mute: true, forced: false);
            if (!placed)
            {
                // Get block type name for better error message
                string blockName = _blockType switch
                {
                    TileID.Dirt => "Dirt",
                    TileID.Stone => "Stone",
                    TileID.WoodBlock => "Wood",
                    _ => $"Type {_blockType}"
                };

                return AgentActionResult.Failure($"Cannot place {blockName} at tile({_tile.X},{_tile.Y}). May need adjacent support tiles or commander lacks blocks in inventory.");
            }

            WorldGen.SquareTileFrame(_tile.X, _tile.Y);

            if (Main.netMode == NetmodeID.Server)
            {
                NetMessage.SendTileSquare(-1, _tile.X, _tile.Y, 1);
            }

            return AgentActionResult.Success($"Placed block {_blockType} at {_tile.X},{_tile.Y}");
        }
    }
}
