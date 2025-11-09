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
        private enum Phase { Initializing, Centering, ClearingLayer, CheckingDepth }

        private Phase _phase = Phase.Initializing;
        private int _leftColumnX;
        private int _middleColumnX;
        private int _rightColumnX;
        private float _centerPixelX;
        private MoveAction? _centeringMoveAction;
        private bool _initialized;
        private bool _positionInitialized;
        private int _blocksMinedSinceLastReport;
        private int _agentWhoAmI; // Store agent ID for claim release
        private int _lastJumpTick; // Track last jump time
        private const float CENTER_TOLERANCE = 8f; // pixels
        private const int BLOCKS_PER_REPLAN_CHECK = 20; // Report status every 20 blocks mined
        private const int JUMP_COOLDOWN_TICKS = 90; // 1.5 seconds at 60 FPS
        private const float JUMP_CHANCE = 0.3f; // 30% chance to jump when nearby agent detected
        private const int TILES_PER_TICK = 6; // Faster mining throughput (approx 2.5x speed)
        private const int LAYER_HEIGHT = 3; // Dig 3 tiles vertically at a time

        private int _nextLayerBaseY;
        private int _tilesClearedThisLayer;

        public HellevatorAction(int startTileX = 0)
        {
            // If startTileX is 0, will be initialized from agent position
            if (startTileX != 0)
            {
                InitializePosition(startTileX);
            }
            _initialized = false;
            _positionInitialized = startTileX != 0;
            _agentWhoAmI = -1;
            _lastJumpTick = 0;
            _nextLayerBaseY = -1;
            _tilesClearedThisLayer = 0;
        }

        private void InitializePosition(int startTileX)
        {
            // Check if hellevator is already active - use shared center
            var sharedCenter = MultiAgentCoordinator.GetHellevatorCenter();
            if (sharedCenter.HasValue)
            {
                // Use shared center from coordinator
                startTileX = sharedCenter.Value;
            }
            else
            {
                // First agent - claim hellevator with current position
                // Note: agentWhoAmI will be set in OnTick when we have context
            }

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
            return null;
        }

        public override void Reset()
        {
            base.Reset();
            _phase = Phase.Initializing;
            _centeringMoveAction = null;
            _initialized = false;
            _positionInitialized = false;
            _blocksMinedSinceLastReport = 0;
            _agentWhoAmI = -1;
            _lastJumpTick = 0;
            _nextLayerBaseY = -1;
            _tilesClearedThisLayer = 0;
        }

        protected override void OnCancel()
        {
            base.OnCancel();
            _centeringMoveAction?.Cancel();
            
            // Release hellevator claim if this agent claimed it
            if (_agentWhoAmI >= 0)
            {
                MultiAgentCoordinator.ReleaseHellevator(_agentWhoAmI);
            }
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

            // Store agent ID for claim management
            if (_agentWhoAmI < 0)
            {
                _agentWhoAmI = npc.whoAmI;
            }

            // Initialize position from agent if not set
            if (!_positionInitialized)
            {
                // Check if hellevator is already active - use shared center
                var sharedCenter = MultiAgentCoordinator.GetHellevatorCenter();
                if (sharedCenter.HasValue)
                {
                    // Use shared center from coordinator
                    InitializePosition(sharedCenter.Value);
                }
                else
                {
                    // First agent - claim hellevator with current position
                    if (MultiAgentCoordinator.ClaimHellevator(currentTileX, _agentWhoAmI))
                    {
                        InitializePosition(currentTileX);
                    }
                    else
                    {
                        // Another agent claimed it first - use their center
                        var center = MultiAgentCoordinator.GetHellevatorCenter();
                        if (center.HasValue)
                        {
                            InitializePosition(center.Value);
                        }
                        else
                        {
                            InitializePosition(currentTileX);
                        }
                    }
                }
            }

            // Check if NPC is touching ash blocks (indicates underworld reached)
            if (IsTouchingAsh(npc))
            {
                // Release hellevator claim when complete
                if (_agentWhoAmI >= 0)
                {
                    MultiAgentCoordinator.ReleaseHellevator(_agentWhoAmI);
                }
                return AgentActionResult.Success($"Reached underworld - touching ash blocks at depth Y={currentTileY}");
            }

            // Random jumping to prevent stacking with other agents
            if (HasNearbyAgents(npc) && MovementHelper.IsOnGround(npc))
            {
                int currentTick = (int)Main.GameUpdateCount;
                if (currentTick - _lastJumpTick >= JUMP_COOLDOWN_TICKS)
                {
                    var random = new Random(npc.whoAmI + currentTick);
                    if (random.NextDouble() < JUMP_CHANCE)
                    {
                        npc.velocity.Y = -6f; // Small jump
                        _lastJumpTick = currentTick;
                    }
                }
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
                _centeringMoveAction = null;
                _phase = Phase.Centering;
                return AgentActionResult.Pending("Off-center, re-centering on middle column...");
            }

            // Initialize layer tracking once centered
            if (_nextLayerBaseY < 0)
            {
                _nextLayerBaseY = GetCurrentTargetY(npc);
                _tilesClearedThisLayer = 0;
            }

            // Handle phase-based execution
            switch (_phase)
            {
                case Phase.Initializing:
                    return HandleInitializing(context);

                case Phase.Centering:
                    return HandleCentering(context);

                case Phase.ClearingLayer:
                    return HandleClearingLayer(context);

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
            _nextLayerBaseY = GetCurrentTargetY(context.Agent);
            _tilesClearedThisLayer = 0;
            return AgentActionResult.Pending("Centering on middle column before starting hellevator...");
        }

        private AgentActionResult HandleCentering(AgentActionContext context)
        {
            // Check if already centered
            if (IsCentered(context.Agent))
            {
                _phase = Phase.ClearingLayer;
                return AgentActionResult.Pending("Centered, starting to clear shaft...");
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
                _phase = Phase.ClearingLayer;
                return AgentActionResult.Pending("Centered, starting to clear shaft...");
            }

            if (result.Status == AgentActionStatus.Failure)
            {
                _centeringMoveAction = null;
                return AgentActionResult.Failure($"Could not center: {result.Message}");
            }

            return AgentActionResult.Pending($"Centering... ({result.Message})");
        }

        private AgentActionResult HandleClearingLayer(AgentActionContext context)
        {
            // Ensure we're still centered before clearing
            if (!IsCentered(context.Agent))
            {
                _centeringMoveAction = null;
                _phase = Phase.Centering;
                return AgentActionResult.Pending("Off-center, re-centering...");
            }

            int tilesClearedThisTick = 0;
            bool remainingSolidTiles = false;
            int baseY = _nextLayerBaseY;

            for (int y = baseY; y < baseY + LAYER_HEIGHT; y++)
            {
                if (y < 0 || y >= Main.maxTilesY)
                {
                    continue;
                }

                for (int x = _leftColumnX; x <= _rightColumnX; x++)
                {
                    if (x < 0 || x >= Main.maxTilesX)
                    {
                        continue;
                    }

                    var tile = Framing.GetTileSafely(x, y);
                    if (!tile.HasTile)
                    {
                        continue;
                    }

                    remainingSolidTiles = true;

                    if (!CanMineTile(tile))
                    {
                        string tileName = TileID.Search.GetName(tile.TileType);
                        return AgentActionResult.Failure($"Encountered unbreakable tile {tileName} at tile({x},{y}).");
                    }

                    if (tilesClearedThisTick >= TILES_PER_TICK)
                    {
                        continue;
                    }

                    WorldGen.KillTile(x, y, false, false, false);
                    if (Main.netMode == NetmodeID.Server)
                    {
                        NetMessage.SendTileSquare(-1, x, y, 1);
                    }

                    tilesClearedThisTick++;
                    _tilesClearedThisLayer++;
                    _blocksMinedSinceLastReport++;
                }
            }

            if (_blocksMinedSinceLastReport >= BLOCKS_PER_REPLAN_CHECK)
            {
                int reported = _blocksMinedSinceLastReport;
                _blocksMinedSinceLastReport = 0;
                return AgentActionResult.Pending($"Hellevator: Cleared {reported} tiles, continuing to depth Y={baseY}...");
            }

            if (remainingSolidTiles)
            {
                return AgentActionResult.Pending($"Clearing hellevator shaft at depth Y={baseY}...");
            }

            // Layer fully cleared; move to next layer and allow NPC to descend
            _nextLayerBaseY++;
            _tilesClearedThisLayer = 0;
            _phase = Phase.CheckingDepth;
            return AgentActionResult.Pending("Layer cleared, descending...");
        }

        private AgentActionResult HandleCheckingDepth(AgentActionContext context)
        {
            // Check if hellevator reached underworld
            if (IsTouchingAsh(context.Agent))
            {
                if (_agentWhoAmI >= 0)
                {
                    MultiAgentCoordinator.ReleaseHellevator(_agentWhoAmI);
                }
                return AgentActionResult.Success($"Reached underworld - touching ash blocks at depth Y={GetCurrentTargetY(context.Agent)}");
            }

            int currentBottomTile = (int)(context.Agent.Bottom.Y / 16f);
            if (currentBottomTile <= _nextLayerBaseY)
            {
                return AgentActionResult.Pending("Descending to next layer...");
            }

            _phase = Phase.ClearingLayer;
            _tilesClearedThisLayer = 0;
            return AgentActionResult.Pending(null);
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

        private bool HasNearbyAgents(NPC agent)
        {
            if (agent == null)
            {
                return false;
            }

            int agentTileX = (int)(agent.Center.X / 16f);
            int agentTileY = (int)(agent.Center.Y / 16f);
            int agentWhoAmI = agent.whoAmI;

            // Check all active AIAgentNPCs
            var agentType = ModContent.NPCType<AIAgentNPC>();
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                var npc = Main.npc[i];
                if (npc == null || !npc.active || npc.type != agentType || npc.whoAmI == agentWhoAmI)
                {
                    continue;
                }

                int otherTileX = (int)(npc.Center.X / 16f);
                int otherTileY = (int)(npc.Center.Y / 16f);

                // Check if within 2 tiles horizontally (same hellevator column)
                int horizontalDistance = Math.Abs(otherTileX - agentTileX);
                if (horizontalDistance > 2)
                {
                    continue;
                }

                // Check if within 3 tiles vertically (could stack)
                int verticalDistance = Math.Abs(otherTileY - agentTileY);
                if (verticalDistance <= 3)
                {
                    return true; // Found nearby agent
                }
            }

            return false;
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

