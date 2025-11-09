using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Xna.Framework;
using TerrarAI.Content.NPCs;
using TerrarAI.Content.Systems;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrarAI.Content.Actions
{
    public sealed class MineAction : TileTargetAction
    {
        private enum Phase { CheckingRange, MovingToTarget, Mining }

        private Phase _phase = Phase.CheckingRange;
        private MoveAction? _moveAction;
        private int _damageAccumulated;
        private int _lastDamageAmount;
        private int _noProgressTicks;
        private Item? _currentPickaxe;
        private bool _initialized;
        private bool _slowMiningToggle;
        private readonly List<Point> _clusterTiles;
        private int _currentTileIndex;

        public MineAction(Point tile) : base(tile)
        {
            _damageAccumulated = 0;
            _initialized = false;
            _slowMiningToggle = false;
            _lastDamageAmount = 0;
            _noProgressTicks = 0;
            _lastDamageAmount = 0;
            _noProgressTicks = 0;
            _clusterTiles = BuildClusterTiles(tile);
            _currentTileIndex = 0;
        }

        public override string Name => "mine";

        // Override timeout for mining actions - allow up to 15 seconds
        protected override int MaxExecutionTicks => 900;

        public override float GetRequiredRange() => 80f;  // 5 tiles = 80 pixels (standard player reach)

        public override void Reset()
        {
            base.Reset();
            _phase = Phase.CheckingRange;
            _moveAction = null;
            _damageAccumulated = 0;
            _currentPickaxe = null;
            _initialized = false;
            _slowMiningToggle = false;
            _currentTileIndex = 0;
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
                return AgentActionResult.Failure("MineAction must run on the server.");
            }

            // Handle multi-phase execution
            switch (_phase)
            {
                case Phase.CheckingRange:
                    return HandleCheckingRange(context);

                case Phase.MovingToTarget:
                    return HandleMovingToTarget(context);

                case Phase.Mining:
                    return HandleMining(context);

                default:
                    return AgentActionResult.Failure("Invalid phase");
            }
        }

        private AgentActionResult HandleCheckingRange(AgentActionContext context)
        {
            var targetPos = GetCurrentTileWorldCenter();
            var distance = Vector2.Distance(context.Agent.Center, targetPos);

            if (distance <= GetRequiredRange())
            {
                // Already in range, proceed to mining
                _phase = Phase.Mining;
                return AgentActionResult.Pending($"In range ({distance:F0}px), starting mining...");
            }
            else
            {
                // Need to move closer first
                var targetTile = CurrentTile;
                _moveAction = new MoveAction(targetTile);
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
                // Movement succeeded, proceed to mining
                _phase = Phase.Mining;
                _moveAction = null;
                return AgentActionResult.Pending("Arrived at target, starting mining...");
            }
            else if (moveResult.Status == AgentActionStatus.Failure)
            {
                // Movement failed, can't mine
                return AgentActionResult.Failure($"Could not reach target: {moveResult.Message}");
            }

            // Still moving...
            return moveResult;
        }

        private AgentActionResult HandleMining(AgentActionContext context)
        {
            // STABILITY CHECK: Wait for agent to stop moving before starting mining
            if (!_initialized)
            {
                var velocityX = System.Math.Abs(context.Agent.velocity.X);
                var velocityY = System.Math.Abs(context.Agent.velocity.Y);

                if (velocityX > 0.5f || velocityY > 0.5f)
                {
                    // Agent still has too much velocity - apply brakes and wait
                    MovementHelper.ApplyFriction(context.Agent, 1.0f);
                    context.Agent.velocity.Y *= 0.3f;
                    return AgentActionResult.Pending($"Stabilizing position before mining... (velocity: {velocityX:F1}, {velocityY:F1})");
                }
            }

            // Initialize on first execution (after stability check passes)
            if (!_initialized)
            {
                _initialized = true;

                // Maintain minimal velocity for stepSpeed to work (allows stepping over 1-tile obstacles)
                const float MIN_STEP_VELOCITY = 0.15f;
                if (Math.Abs(context.Agent.velocity.X) < MIN_STEP_VELOCITY)
                {
                    context.Agent.velocity.X = context.Agent.direction * MIN_STEP_VELOCITY;
                }
                context.Agent.velocity.X *= 0.9f;  // Gentle friction
                context.Agent.velocity.Y *= 0.9f;

                // Check if tile exists
                var tile = Framing.GetTileSafely(CurrentTile.X, CurrentTile.Y);
                if (!tile.HasTile)
                {
                    return CompleteCurrentTileSuccess($"Tile {CurrentTile.X},{CurrentTile.Y} already empty.");
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

                // Animation removed - using default fairy sprite rendering
            }

            // POSITION VALIDATION: Verify agent hasn't drifted out of range
            var targetPos = GetCurrentTileWorldCenter();
            var currentDistance = Vector2.Distance(context.Agent.Center, targetPos);

            if (currentDistance > GetRequiredRange())
            {
                return AgentActionResult.Failure(
                    $"Drifted out of range while mining (now {currentDistance:F0}px away, max {GetRequiredRange()}px). Position unstable.");
            }

            // Apply gentle friction while maintaining minimal velocity for stepSpeed
            MovementHelper.ApplyFriction(context.Agent, 0.3f);  // Lighter friction
            context.Agent.velocity.Y *= 0.95f;  // Gentle Y dampening

            // Check if tile still exists
            var currentTile = Framing.GetTileSafely(CurrentTile.X, CurrentTile.Y);
            if (!currentTile.HasTile)
            {
                return CompleteCurrentTileSuccess($"Tile {CurrentTile.X},{CurrentTile.Y} already empty.");
            }

            // Calculate mining damage based on pickaxe power
            // Higher pickaxe power = faster mining
            if (_currentPickaxe == null)
            {
                return AgentActionResult.Failure("Pickaxe became unavailable during mining.");
            }

            int damagePerTick = CalculateMiningDamage(_currentPickaxe.pick, currentTile.TileType);
            _damageAccumulated += damagePerTick;

            # Stall detection: if no progress in ~1.5s, fail fast
            if (_damageAccumulated == _lastDamageAmount)
            {
                _noProgressTicks++;
                if (_noProgressTicks >= 90)
                {
                    string tileName = TileID.Search.GetName(currentTile.TileType);
                    return AgentActionResult.Failure($"Mining stalled on {tileName} at tile({CurrentTile.X},{CurrentTile.Y}). No progress for 1.5s.");
                }
            }
            else
            {
                _noProgressTicks = 0;
                _lastDamageAmount = _damageAccumulated;
            }

            // Destroy tile when damage reaches 100
            if (_damageAccumulated >= 100)
            {
                WorldGen.KillTile(CurrentTile.X, CurrentTile.Y, false, false, false);

                // Sync tile change in multiplayer
                if (Main.netMode == NetmodeID.Server)
                {
                    NetMessage.SendTileSquare(-1, CurrentTile.X, CurrentTile.Y, 1);
                }

                // Verify tile was destroyed
                string tileNameBeforeDestroy = TileID.Search.GetName(currentTile.TileType);
                currentTile = Framing.GetTileSafely(CurrentTile.X, CurrentTile.Y);
                if (!currentTile.HasTile)
                {
                    return CompleteCurrentTileSuccess($"Successfully mined {tileNameBeforeDestroy} at tile({CurrentTile.X},{CurrentTile.Y}) using {_currentPickaxe.Name}");
                }
                else
                {
                    return AgentActionResult.Failure($"Failed to destroy {tileNameBeforeDestroy} at tile({CurrentTile.X},{CurrentTile.Y}). Tile may be protected or require different tool.");
                }
            }

            // Still mining - show progress with tile name
            int progressPercent = _damageAccumulated;
            string currentTileName = TileID.Search.GetName(currentTile.TileType);
            return AgentActionResult.Pending($"Mining {currentTileName} at tile({CurrentTile.X},{CurrentTile.Y})... ({progressPercent}%) (3x3 cluster)");
        }

        /// <summary>
        /// Calculates mining damage per tick based on pickaxe power and tile type.
        /// SLOWED DOWN for realistic Terraria mining speed (1-3 seconds instead of 0.3-0.5 seconds).
        /// </summary>
        private int CalculateMiningDamage(int pickaxePower, int tileType)
        {
            int tileStrength = ToolSelector.GetTileStrength(tileType);

            if (pickaxePower >= tileStrength * 2)
            {
                // Pickaxe is 2x stronger than needed: 2 damage/tick (50 ticks = 0.83 seconds)
                return 2;
            }

            if (pickaxePower >= tileStrength * 1.5f)
            {
                // Pickaxe is 1.5x stronger: 1 damage/tick (100 ticks = 1.67 seconds)
                return 1;
            }

            if (pickaxePower >= tileStrength * 1.2f)
            {
                // Pickaxe is moderately stronger: 1 damage/tick (100 ticks = 1.67 seconds)
                return 1;
            }

            // Pickaxe barely strong enough: simulate 0.5 damage/tick by alternating between 1 and 0
            _slowMiningToggle = !_slowMiningToggle;
            return _slowMiningToggle ? 1 : 0;
        }

        public static AgentAction CreateFromParameters(JsonElement parameters, ActionValidator validator, WorldContext? worldContext = null, NPC? agent = null)
        {
            // Reject legacy natural-language "target" parameter outright
            if (parameters.TryGetProperty("target", out var targetElement) && targetElement.ValueKind == JsonValueKind.String)
            {
                return new FailureAction("Mine actions now require explicit tileX/tileY coordinates. Natural-language targets like \"nearest_trees\" are no longer supported.");
            }

            // Standard tile coordinate parsing
            var tileX = ActionParameterReader.ReadInt(parameters, "tileX");
            var tileY = ActionParameterReader.ReadInt(parameters, "tileY");
            var clamped = validator.ClampTilePosition(tileX, tileY);
            return new MineAction(clamped);
        }

        public override Point? GetTargetTile() => CurrentTile;

        private static List<Point> BuildClusterTiles(Point center)
        {
            var tiles = new List<Point>(9);
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    tiles.Add(new Point(center.X + dx, center.Y + dy));
                }
            }

            tiles.Sort((a, b) =>
            {
                int priA = Math.Abs(a.X - center.X) + Math.Abs(a.Y - center.Y);
                int priB = Math.Abs(b.X - center.X) + Math.Abs(b.Y - center.Y);
                if (priA != priB)
                {
                    return priA.CompareTo(priB);
                }

                // Tie-breaker: prefer lower Y (higher on screen), then lower X
                if (a.Y != b.Y)
                {
                    return a.Y.CompareTo(b.Y);
                }
                return a.X.CompareTo(b.X);
            });

            return tiles;
        }

        private Point CurrentTile => _clusterTiles[_currentTileIndex];

        private Vector2 GetCurrentTileWorldCenter()
        {
            return new Vector2(CurrentTile.X * 16f + 8f, CurrentTile.Y * 16f + 8f);
        }

        private AgentActionResult CompleteCurrentTileSuccess(string message)
        {
            if (AdvanceToNextTile())
            {
                return AgentActionResult.Pending($"{message} Continuing 3x3 cluster at tile({CurrentTile.X},{CurrentTile.Y}).");
            }

            return AgentActionResult.Success(message);
        }

        private bool AdvanceToNextTile()
        {
            if (_currentTileIndex >= _clusterTiles.Count - 1)
            {
                return false;
            }

            _currentTileIndex++;
            _phase = Phase.CheckingRange;
            _moveAction = null;
            _damageAccumulated = 0;
            _currentPickaxe = null;
            _initialized = false;
            _slowMiningToggle = false;
            return true;
        }
    }
}
