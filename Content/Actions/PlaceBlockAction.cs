using System.Text.Json;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Systems;
using Terraria;
using Terraria.ID;

namespace TerrarAI.Content.Actions
{
    public sealed class PlaceBlockAction : TileTargetAction
    {
        private enum Phase { CheckingRange, MovingToTarget, Placing }
        
        private Phase _phase = Phase.CheckingRange;
        private MoveAction? _moveAction;
        private readonly int _blockType;

        public PlaceBlockAction(Point tile, int blockType) : base(tile)
        {
            _blockType = blockType;
        }

        public override string Name => "place";

        public override float GetRequiredRange() => 80f;  // 5 tiles = 80 pixels (standard player reach)

        public override void Reset()
        {
            base.Reset();
            _phase = Phase.CheckingRange;
            _moveAction = null;
        }
        
        protected override void OnCancel()
        {
            base.OnCancel();
            _moveAction?.Cancel();
        }

        protected override AgentActionResult OnTick(AgentActionContext context)
        {
            if (!ServerAuthority.IsServer)
            {
                return AgentActionResult.Failure("PlaceBlockAction must run on the server.");
            }

            // Handle multi-phase execution
            switch (_phase)
            {
                case Phase.CheckingRange:
                    return HandleCheckingRange(context);
                    
                case Phase.MovingToTarget:
                    return HandleMovingToTarget(context);
                    
                case Phase.Placing:
                    return HandlePlacing(context);
                    
                default:
                    return AgentActionResult.Failure("Invalid phase");
            }
        }

        private AgentActionResult HandleCheckingRange(AgentActionContext context)
        {
            var targetPos = GetTileWorldCenter();
            var distance = Vector2.Distance(context.Agent.Center, targetPos);

            if (distance <= GetRequiredRange())
            {
                // Already in range, proceed to placing
                _phase = Phase.Placing;
                return AgentActionResult.Pending($"In range ({distance:F0}px), starting placement...");
            }
            else
            {
                // Need to move closer first
                _moveAction = new MoveAction(targetPos);
                _phase = Phase.MovingToTarget;
                return AgentActionResult.Pending($"Target {distance:F0}px away, moving closer first...");
            }
        }

        private AgentActionResult HandleMovingToTarget(AgentActionContext context)
        {
            if (_moveAction == null)
            {
                return AgentActionResult.Failure("MoveAction is null");
            }

            var moveResult = _moveAction.Tick(context);

            if (moveResult.Status == AgentActionStatus.Success)
            {
                // Movement succeeded, proceed to placing
                _phase = Phase.Placing;
                _moveAction = null;
                return AgentActionResult.Pending("Arrived at target, starting placement...");
            }
            else if (moveResult.Status == AgentActionStatus.Failure)
            {
                // Movement failed, can't place
                return AgentActionResult.Failure($"Could not reach target: {moveResult.Message}");
            }

            // Still moving...
            return moveResult;
        }

        private AgentActionResult HandlePlacing(AgentActionContext context)
        {

            var tile = Framing.GetTileSafely(Tile.X, Tile.Y);
            if (tile.HasTile)
            {
                if (tile.TileType == _blockType)
                {
                    return AgentActionResult.Success($"Block already placed at {Tile.X},{Tile.Y}");
                }

                return AgentActionResult.Failure($"Tile {Tile.X},{Tile.Y} is occupied.");
            }

            var placed = WorldGen.PlaceTile(Tile.X, Tile.Y, _blockType, mute: true, forced: false);
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

                return AgentActionResult.Failure($"Cannot place {blockName} at tile({Tile.X},{Tile.Y}). May need adjacent support tiles or commander lacks blocks in inventory.");
            }

            WorldGen.SquareTileFrame(Tile.X, Tile.Y);

            if (Main.netMode == NetmodeID.Server)
            {
                NetMessage.SendTileSquare(-1, Tile.X, Tile.Y, 1);
            }

            return AgentActionResult.Success($"Placed block {_blockType} at {Tile.X},{Tile.Y}");
        }

        public static AgentAction CreateFromParameters(JsonElement parameters, ActionValidator validator)
        {
            var tileX = ActionParameterReader.ReadInt(parameters, "tileX");
            var tileY = ActionParameterReader.ReadInt(parameters, "tileY");
            var blockType = ActionParameterReader.ReadInt(parameters, "blockType");

            var clamped = validator.ClampTilePosition(tileX, tileY);
            var validatedBlock = validator.ValidateBlockType(blockType);
            return new PlaceBlockAction(clamped, validatedBlock);
        }
    }
}
