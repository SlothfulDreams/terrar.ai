using Microsoft.Xna.Framework;
using TerrarAI.Content.NPCs;
using TerrarAI.Content.Systems;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrarAI.Content.Actions
{
    public sealed class MineAction : AgentAction
    {
        private readonly Point _tile;
        private int _damageAccumulated;
        private Item? _currentPickaxe;
        private bool _initialized;

        public MineAction(Point tile)
        {
            _tile = tile;
            _damageAccumulated = 0;
            _initialized = false;
        }

        public override string Name => "mine";

        public override float GetRequiredRange() => 80f;  // 5 tiles = 80 pixels (standard player reach)

        public override Point? GetTargetTile() => _tile;

        public override void Reset()
        {
            _damageAccumulated = 0;
            _currentPickaxe = null;
            _initialized = false;
        }

        public override AgentActionResult Execute(AgentActionContext context)
        {
            if (!ServerAuthority.IsServer)
            {
                return AgentActionResult.Failure("MineAction must run on the server.");
            }

            // STABILITY CHECK: Wait for agent to stop moving before starting mining
            if (!_initialized)
            {
                var velocityX = System.Math.Abs(context.Agent.velocity.X);
                var velocityY = System.Math.Abs(context.Agent.velocity.Y);

                if (velocityX > 0.5f || velocityY > 0.5f)
                {
                    // Agent still has too much velocity - apply brakes and wait
                    context.Agent.velocity.X *= 0.3f;
                    context.Agent.velocity.Y *= 0.3f;
                    return AgentActionResult.Pending($"Stabilizing position before mining... (velocity: {velocityX:F1}, {velocityY:F1})");
                }
            }

            // Initialize on first execution (after stability check passes)
            if (!_initialized)
            {
                _initialized = true;

                // ZERO VELOCITY COMPLETELY to prevent any drift during mining
                context.Agent.velocity.X = 0f;
                context.Agent.velocity.Y = 0f;

                // Check if tile exists
                var tile = Framing.GetTileSafely(_tile.X, _tile.Y);
                if (!tile.HasTile)
                {
                    return AgentActionResult.Success($"Tile {_tile.X},{_tile.Y} already empty.");
                }

                // Find best pickaxe from commander's inventory
                _currentPickaxe = ToolSelector.FindBestTool(context.Commander, ToolType.Pickaxe);
                if (_currentPickaxe == null || _currentPickaxe.IsAir)
                {
                    return AgentActionResult.Failure("No pickaxe available in commander's inventory.");
                }

                // Check if pickaxe can mine this tile
                int tileType = tile.TileType;
                if (!ToolSelector.CanMineTile(_currentPickaxe, tileType))
                {
                    int requiredPower = ToolSelector.GetTileStrength(tileType);
                    string tileName = TileID.Search.GetName(tileType);
                    return AgentActionResult.Failure($"Pickaxe too weak to mine {tileName} (need {requiredPower}% power, have {_currentPickaxe.pick}%)");
                }

                // Trigger initial swing animation (longer duration for visibility)
                if (context.Agent.ModNPC is AIAgentNPC aiAgent)
                {
                    aiAgent.TriggerItemAnimation(_currentPickaxe, 30);
                }
            }

            // POSITION VALIDATION: Verify agent hasn't drifted out of range
            var tileCenterX = _tile.X * 16f + 8f;
            var tileCenterY = _tile.Y * 16f + 8f;
            var targetPos = new Vector2(tileCenterX, tileCenterY);
            var currentDistance = Vector2.Distance(context.Agent.Center, targetPos);

            if (currentDistance > GetRequiredRange())
            {
                return AgentActionResult.Failure(
                    $"Drifted out of range while mining (now {currentDistance:F0}px away, max {GetRequiredRange()}px). Position unstable.");
            }

            // RE-ZERO VELOCITY each tick to combat any drift
            context.Agent.velocity.X *= 0.5f;
            context.Agent.velocity.Y *= 0.5f;

            // Check if tile still exists
            var currentTile = Framing.GetTileSafely(_tile.X, _tile.Y);
            if (!currentTile.HasTile)
            {
                return AgentActionResult.Success($"Mined tile at {_tile.X},{_tile.Y}");
            }

            // Calculate mining damage based on pickaxe power
            // Higher pickaxe power = faster mining
            int damagePerTick = CalculateMiningDamage(_currentPickaxe.pick, currentTile.TileType);
            _damageAccumulated += damagePerTick;

            // Trigger swing animation periodically (every 30 ticks for slower, visible swings)
            if (_damageAccumulated % 30 == 0 && context.Agent.ModNPC is AIAgentNPC agent)
            {
                agent.TriggerItemAnimation(_currentPickaxe, 30);
            }

            // Destroy tile when damage reaches 100
            if (_damageAccumulated >= 100)
            {
                WorldGen.KillTile(_tile.X, _tile.Y, false, false, true);

                // Sync tile change in multiplayer
                if (Main.netMode == NetmodeID.Server)
                {
                    NetMessage.SendTileSquare(-1, _tile.X, _tile.Y, 1);
                }

                // Verify tile was destroyed
                string tileNameBeforeDestroy = TileID.Search.GetName(currentTile.TileType);
                currentTile = Framing.GetTileSafely(_tile.X, _tile.Y);
                if (!currentTile.HasTile)
                {
                    return AgentActionResult.Success($"Successfully mined {tileNameBeforeDestroy} at tile({_tile.X},{_tile.Y}) using {_currentPickaxe.Name}");
                }
                else
                {
                    return AgentActionResult.Failure($"Failed to destroy {tileNameBeforeDestroy} at tile({_tile.X},{_tile.Y}). Tile may be protected or require different tool.");
                }
            }

            // Still mining - show progress with tile name
            int progressPercent = _damageAccumulated;
            string currentTileName = TileID.Search.GetName(currentTile.TileType);
            return AgentActionResult.Pending($"Mining {currentTileName} at tile({_tile.X},{_tile.Y})... ({progressPercent}%)");
        }

        /// <summary>
        /// Calculates mining damage per tick based on pickaxe power and tile type.
        /// SLOWED DOWN for realistic Terraria mining speed (1-3 seconds instead of 0.3-0.5 seconds).
        /// </summary>
        private int CalculateMiningDamage(int pickaxePower, int tileType)
        {
            int tileStrength = ToolSelector.GetTileStrength(tileType);

            // Reduced damage values for realistic mining time (1-3 seconds)
            int baseDamage = 1;

            if (pickaxePower >= tileStrength * 2)
            {
                // Pickaxe is 2x stronger than needed: 2 damage/tick (50 ticks = 0.83 seconds)
                baseDamage = 2;
            }
            else if (pickaxePower >= tileStrength * 1.5f)
            {
                // Pickaxe is 1.5x stronger: 1 damage/tick (100 ticks = 1.67 seconds)
                baseDamage = 1;
            }
            else if (pickaxePower >= tileStrength * 1.2f)
            {
                // Pickaxe is moderately stronger: 1 damage/tick (100 ticks = 1.67 seconds)
                baseDamage = 1;
            }
            else
            {
                // Pickaxe barely strong enough: Very slow mining
                // Use counter-based approach for fractional damage (0.5 damage/tick = 200 ticks = 3.33 seconds)
                // Every other tick adds damage
                if (_damageAccumulated % 2 == 0)
                {
                    baseDamage = 1;
                }
                else
                {
                    baseDamage = 0;
                }
            }

            return baseDamage;
        }
    }
}
