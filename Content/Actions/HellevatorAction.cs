using System;
using System.Text.Json;
using Microsoft.Xna.Framework;
using TerrarAI.Content.NPCs;
using TerrarAI.Content.Systems;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrarAI.Content.Actions
{
    /// <summary>
    /// Action that digs a 3x3 hellevator straight down to the underworld.
    /// Keeps the NPC centered on the middle column so it falls through, and re-centers if knocked off-center.
    /// </summary>
    public sealed class HellevatorAction : AgentAction
    {
        private enum Phase { Initializing, Centering, MiningLeft, MiningMiddle, MiningRight, CheckingDepth }

        private Phase _phase = Phase.Initializing;
        private int _leftColumnX;
        private int _middleColumnX;
        private int _rightColumnX;
        private float _centerPixelX;
        private MineAction? _currentMineAction;
        private MoveAction? _centeringMoveAction;
        private bool _initialized;
        private bool _positionInitialized;
        private int _blocksMinedSinceLastReport;
        private const float CENTER_TOLERANCE = 8f; // pixels
        private const int BLOCKS_PER_REPLAN_CHECK = 20; // Report status every 20 blocks mined

        public HellevatorAction(int startTileX = 0)
        {
            // If startTileX is 0, will be initialized from agent position
            if (startTileX != 0)
            {
                InitializePosition(startTileX);
            }
            _initialized = false;
            _positionInitialized = startTileX != 0;
        }

        private void InitializePosition(int startTileX)
        {
            // Set up 3x3 shaft: center on the startTileX, with one column on each side
            _middleColumnX = startTileX;
            _leftColumnX = startTileX - 1;
            _rightColumnX = startTileX + 1;
            _centerPixelX = _middleColumnX * 16f + 8f; // Center of middle column
            _positionInitialized = true;
        }

        public override string Name => "hellevator";

        public override Point? GetTargetTile()
        {
            // Return current mining target
            return _currentMineAction?.GetTargetTile();
        }

        public override void Reset()
        {
            base.Reset();
            _phase = Phase.Initializing;
            _currentMineAction = null;
            _centeringMoveAction = null;
            _initialized = false;
            _positionInitialized = false;
            _blocksMinedSinceLastReport = 0;
        }

        protected override void OnCancel()
        {
            base.OnCancel();
            _currentMineAction?.Cancel();
            _centeringMoveAction?.Cancel();
        }

        protected override AgentActionResult OnTick(AgentActionContext context)
        {
            if (!ServerAuthority.IsServer)
            {
                return AgentActionResult.Failure("HellevatorAction must run on the server.");
            }

            var npc = context.Agent;
            // Use Bottom.Y to get the NPC's feet position for accurate tile calculation
            int currentTileY = (int)(npc.Bottom.Y / 16f);
            int currentTileX = (int)(npc.Center.X / 16f);

            // Initialize position from agent if not set
            if (!_positionInitialized)
            {
                InitializePosition(currentTileX);
            }

            // Check if NPC is touching ash blocks (indicates underworld reached)
            if (IsTouchingAsh(npc))
            {
                return AgentActionResult.Success($"Reached underworld - touching ash blocks at depth Y={currentTileY}");
            }

            // Mark as initialized
            if (!_initialized)
            {
                _initialized = true;
            }

            // Check centering on every iteration (except when already centering or initializing)
            // Ensure we're centered around the blocks we're breaking
            if (_phase != Phase.Initializing && _phase != Phase.Centering && !IsCentered(npc))
            {
                _currentMineAction = null;
                _centeringMoveAction = null;
                _phase = Phase.Centering;
                return AgentActionResult.Pending("Off-center, re-centering on middle column...");
            }

            // Handle phase-based execution
            switch (_phase)
            {
                case Phase.Initializing:
                    return HandleInitializing(context);

                case Phase.Centering:
                    return HandleCentering(context);

                case Phase.MiningLeft:
                    return HandleMining(context, _leftColumnX, Phase.MiningMiddle);

                case Phase.MiningMiddle:
                    return HandleMining(context, _middleColumnX, Phase.MiningRight);

                case Phase.MiningRight:
                    return HandleMining(context, _rightColumnX, Phase.CheckingDepth);

                case Phase.CheckingDepth:
                    return HandleCheckingDepth(context);

                default:
                    return AgentActionResult.Failure("Invalid phase");
            }
        }

        private AgentActionResult HandleInitializing(AgentActionContext context)
        {
            // Always center first before breaking any blocks
            _phase = Phase.Centering;
            return AgentActionResult.Pending("Centering on middle column before starting hellevator...");
        }

        private AgentActionResult HandleCentering(AgentActionContext context)
        {
            // Check if already centered
            if (IsCentered(context.Agent))
            {
                _phase = Phase.MiningLeft;
                return AgentActionResult.Pending("Centered, starting to mine...");
            }

            // Create or continue move action to center
            if (_centeringMoveAction == null)
            {
                // Center horizontally at NPC's current Y position, not at mining target depth
                int npcCurrentTileY = (int)(context.Agent.Bottom.Y / 16f);
                var centerTile = new Point((int)(_centerPixelX / 16f), npcCurrentTileY);
                _centeringMoveAction = new MoveAction(centerTile, tolerance: 0.5f);
            }

            var result = _centeringMoveAction.Tick(context);

            if (result.Status == AgentActionStatus.Success)
            {
                _centeringMoveAction = null;
                _phase = Phase.MiningLeft;
                return AgentActionResult.Pending("Centered, starting to mine...");
            }

            if (result.Status == AgentActionStatus.Failure)
            {
                _centeringMoveAction = null;
                return AgentActionResult.Failure($"Could not center: {result.Message}");
            }

            return AgentActionResult.Pending($"Centering... ({result.Message})");
        }

        private AgentActionResult HandleMining(AgentActionContext context, int tileX, Phase nextPhase)
        {
            // Ensure we're still centered before mining
            if (!IsCentered(context.Agent))
            {
                _currentMineAction = null;
                _phase = Phase.Centering;
                return AgentActionResult.Pending("Off-center, re-centering...");
            }

            // Always calculate target Y from NPC's current position + 1 tile
            int currentTargetY = GetCurrentTargetY(context.Agent);

            // Create or continue mine action
            if (_currentMineAction == null)
            {
                var targetTile = new Point(tileX, currentTargetY);
                _currentMineAction = new MineAction(targetTile);
            }

            var result = _currentMineAction.Tick(context);

            // Always recalculate target Y in case NPC moved (for logging and validation)
            currentTargetY = GetCurrentTargetY(context.Agent);

            if (result.Status == AgentActionStatus.Success)
            {
                _currentMineAction = null;
                _blocksMinedSinceLastReport++;
                _phase = nextPhase;

                // Only report status after mining BLOCKS_PER_REPLAN_CHECK blocks
                if (_blocksMinedSinceLastReport >= BLOCKS_PER_REPLAN_CHECK)
                {
                    int blocksReported = _blocksMinedSinceLastReport;
                    _blocksMinedSinceLastReport = 0;
                    return AgentActionResult.Pending($"Hellevator: Mined {blocksReported} blocks, continuing to depth Y={currentTargetY}...");
                }

                // Continue silently without triggering replan
                return AgentActionResult.Pending(null); // Null message means don't trigger replan
            }

            if (result.Status == AgentActionStatus.Failure)
            {
                _currentMineAction = null;
                // Check if tile is unmineable - skip it and continue
                var tile = Framing.GetTileSafely(tileX, currentTargetY);
                if (!tile.HasTile || CanMineTile(tile))
                {
                    // Try next phase anyway (might be air or already mined)
                    _phase = nextPhase;
                    return AgentActionResult.Pending($"Skipping tile({tileX},{currentTargetY}), continuing...");
                }
                // For unmineable tiles, try to recover by re-centering and continuing
                _phase = Phase.Centering;
                return AgentActionResult.Pending($"Cannot mine tile({tileX},{currentTargetY}), re-centering and continuing...");
            }

            return AgentActionResult.Pending($"Mining tile({tileX},{currentTargetY})... ({result.Message})");
        }

        private AgentActionResult HandleCheckingDepth(AgentActionContext context)
        {
            // Both tiles mined for this row, check if we've reached underworld
            int currentTargetY = GetCurrentTargetY(context.Agent);

            // Check if NPC is touching ash blocks (indicates underworld reached)
            if (IsTouchingAsh(context.Agent))
            {
                return AgentActionResult.Success($"Reached underworld - touching ash blocks at depth Y={currentTargetY}");
            }

            // Check if we need to re-center (gravity may have shifted us)
            if (!IsCentered(context.Agent))
            {
                _phase = Phase.Centering;
                return AgentActionResult.Pending("Off-center, re-centering before next row...");
            }

            // Start mining next row (left column)
            // Don't check counter here - only check in HandleMining to avoid reporting after every row
            _phase = Phase.MiningLeft;
            return AgentActionResult.Pending(null); // Continue silently to next row
        }

        private int GetCurrentTargetY(NPC npc)
        {
            // Always calculate target Y from NPC's current position + 1 tile
            // Use pixel-level precision: calculate target Y from NPC's bottom position + small offset
            // Add 2 pixels below feet to target the tile immediately below
            float targetPixelY = npc.Bottom.Y + 2f;
            return (int)(targetPixelY / 16f); // Convert to tile coordinate
        }

        private bool IsCentered(NPC npc)
        {
            float delta = Math.Abs(_centerPixelX - npc.Center.X);
            return delta <= CENTER_TOLERANCE;
        }

        private bool IsTouchingAsh(NPC npc)
        {
            // Check if NPC is standing on or touching ash blocks
            // Ash blocks are TileID 58
            int feetTileX = (int)(npc.Center.X / 16f);
            int feetTileY = (int)(npc.Bottom.Y / 16f);
            
            // Check tile directly below feet (where NPC is standing)
            var tileBelow = Framing.GetTileSafely(feetTileX, feetTileY);
            if (tileBelow.HasTile && tileBelow.TileType == TileID.Ash)
            {
                return true;
            }
            
            // Check tiles adjacent to NPC (left and right)
            var tileLeft = Framing.GetTileSafely(feetTileX - 1, feetTileY);
            if (tileLeft.HasTile && tileLeft.TileType == TileID.Ash)
            {
                return true;
            }
            
            var tileRight = Framing.GetTileSafely(feetTileX + 1, feetTileY);
            if (tileRight.HasTile && tileRight.TileType == TileID.Ash)
            {
                return true;
            }
            
            return false;
        }

        private bool CanMineTile(Terraria.Tile tile)
        {
            if (!tile.HasTile)
            {
                return true; // Air is "mineable" (already clear)
            }

            // Check if tile is solid and can be mined
            return Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType];
        }

        public static AgentAction CreateFromParameters(JsonElement parameters, ActionValidator validator)
        {
            // Optional: allow specifying start X, otherwise use current position
            int startX = 0;
            if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("startX", out var xElement) && xElement.ValueKind == JsonValueKind.Number)
            {
                startX = xElement.GetInt32();
                var clamped = validator.ClampTilePosition(startX, 0);
                return new HellevatorAction(clamped.X);
            }

            // Use current agent position - will be set during execution
            return new HellevatorAction(0);
        }
    }
}

