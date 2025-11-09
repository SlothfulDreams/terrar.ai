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
    public sealed class ChopAction : TileTargetAction
    {
        private enum Phase { CheckingRange, MovingToTarget, Chopping }

        private Phase _phase = Phase.CheckingRange;
        private MoveAction? _moveAction;
        private int _damageAccumulated;
        private Item? _currentAxe;
        private bool _initialized;
        private bool _slowChoppingToggle;

        public ChopAction(Point tile) : base(tile)
        {
            _damageAccumulated = 0;
            _initialized = false;
            _slowChoppingToggle = false;
        }

        public override string Name => "chop";

        public override float GetRequiredRange() => 32f;  // 2 tiles = 32 pixels

        public override void Reset()
        {
            base.Reset();
            _phase = Phase.CheckingRange;
            _moveAction = null;
            _damageAccumulated = 0;
            _currentAxe = null;
            _initialized = false;
            _slowChoppingToggle = false;
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
                return AgentActionResult.Failure("ChopAction must run on the server.");
            }

            switch (_phase)
            {
                case Phase.CheckingRange:
                    return HandleCheckingRange(context);

                case Phase.MovingToTarget:
                    return HandleMovingToTarget(context);

                case Phase.Chopping:
                    return HandleChopping(context);

                default:
                    return AgentActionResult.Failure("Invalid phase");
            }
        }

        private AgentActionResult HandleCheckingRange(AgentActionContext context)
        {
            var targetPos = GetTileWorldCenter();
            var distance = Vector2.Distance(context.Agent.Center, targetPos);

            // Validate it's actually a tree
            var tile = Framing.GetTileSafely(Tile.X, Tile.Y);
            if (!tile.HasTile || !TileID.Sets.IsATreeTrunk[tile.TileType])
            {
                return AgentActionResult.Failure($"Tile({Tile.X},{Tile.Y}) is not a tree trunk.");
            }

            if (distance <= GetRequiredRange())
            {
                // Already in range, proceed to chopping
                _phase = Phase.Chopping;
                return AgentActionResult.Pending($"In range ({distance:F0}px), starting chopping...");
            }
            else
            {
                // Need to move closer first
                var targetTile = new Point((int)(targetPos.X / 16f), (int)(targetPos.Y / 16f));
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
                _phase = Phase.Chopping;
                _moveAction = null;
                return AgentActionResult.Pending("Moved to tree, starting chopping...");
            }
            else if (moveResult.Status == AgentActionStatus.Failure)
            {
                return AgentActionResult.Failure($"Could not reach tree: {moveResult.Message}");
            }

            return AgentActionResult.Pending($"Moving to tree... ({moveResult.Message})");
        }

        private AgentActionResult HandleChopping(AgentActionContext context)
        {
            // STABILITY CHECK: Wait for agent to stop moving before starting chopping
            if (!_initialized)
            {
                var velocityX = Math.Abs(context.Agent.velocity.X);
                var velocityY = Math.Abs(context.Agent.velocity.Y);

                if (velocityX > 0.5f || velocityY > 0.5f)
                {
                    MovementHelper.ApplyFriction(context.Agent, 1.0f);
                    context.Agent.velocity.Y *= 0.3f;
                    return AgentActionResult.Pending($"Stabilizing position before chopping... (velocity: {velocityX:F1}, {velocityY:F1})");
                }
            }

            // Initialize on first execution (after stability check passes)
            if (!_initialized)
            {
                _initialized = true;

                context.Agent.velocity.X = 0f;
                context.Agent.velocity.Y = 0f;

                // Check if tile exists
                var tile = Framing.GetTileSafely(Tile.X, Tile.Y);
                if (!tile.HasTile)
                {
                    return AgentActionResult.Success($"Tree at tile({Tile.X},{Tile.Y}) already removed.");
                }

                if (!TileID.Sets.IsATreeTrunk[tile.TileType])
                {
                    return AgentActionResult.Failure($"Tile({Tile.X},{Tile.Y}) is not a tree trunk.");
                }

                // Find best axe from commander's inventory
                _currentAxe = ToolSelector.FindBestTool(context.Commander, ToolType.Axe);
                if (_currentAxe == null || _currentAxe.IsAir)
                {
                    return AgentActionResult.Failure("No axe available in commander's inventory.");
                }

                // Animation removed - using default fairy sprite rendering
            }

            // POSITION VALIDATION: Verify agent hasn't drifted out of range
            var targetPos = GetTileWorldCenter();
            var currentDistance = Vector2.Distance(context.Agent.Center, targetPos);

            if (currentDistance > GetRequiredRange())
            {
                return AgentActionResult.Failure(
                    $"Drifted out of range while chopping (now {currentDistance:F0}px away, max {GetRequiredRange()}px). Position unstable.");
            }

            // RE-ZERO VELOCITY each tick to combat any drift
            MovementHelper.ApplyFriction(context.Agent, 0.8f);
            context.Agent.velocity.Y *= 0.5f;

            // Check if tile still exists
            var currentTile = Framing.GetTileSafely(Tile.X, Tile.Y);
            if (!currentTile.HasTile)
            {
                return AgentActionResult.Success($"Chopped tree at tile({Tile.X},{Tile.Y})");
            }

            if (!TileID.Sets.IsATreeTrunk[currentTile.TileType])
            {
                return AgentActionResult.Success($"Tree trunk removed at tile({Tile.X},{Tile.Y})");
            }

            // Calculate chopping damage based on axe power
            int damagePerTick = CalculateChoppingDamage(_currentAxe.axe);
            _damageAccumulated += damagePerTick;

            // Animation removed - using default fairy sprite rendering

            // Destroy tile when damage reaches 100
            if (_damageAccumulated >= 100)
            {
                WorldGen.KillTile(Tile.X, Tile.Y, false, false, false);

                // Sync tile change in multiplayer
                if (Main.netMode == NetmodeID.Server)
                {
                    NetMessage.SendTileSquare(-1, Tile.X, Tile.Y, 1);
                }

                // Verify tile was destroyed
                currentTile = Framing.GetTileSafely(Tile.X, Tile.Y);
                if (!currentTile.HasTile || !TileID.Sets.IsATreeTrunk[currentTile.TileType])
                {
                    return AgentActionResult.Success($"Successfully chopped tree at tile({Tile.X},{Tile.Y}) using {_currentAxe.Name}");
                }
                else
                {
                    return AgentActionResult.Failure($"Failed to destroy tree at tile({Tile.X},{Tile.Y}). Tree may be protected.");
                }
            }

            // Still chopping
            int progressPercent = _damageAccumulated;
            return AgentActionResult.Pending($"Chopping tree at tile({Tile.X},{Tile.Y})... ({progressPercent}%)");
        }

        private int CalculateChoppingDamage(int axePower)
        {
            if (axePower >= 50)
            {
                return 2;
            }

            if (axePower >= 30)
            {
                return 1;
            }

            _slowChoppingToggle = !_slowChoppingToggle;
            return _slowChoppingToggle ? 1 : 0;
        }

        public static AgentAction CreateFromParameters(JsonElement parameters, ActionValidator validator)
        {
            if (parameters.TryGetProperty("target", out var targetElement) && targetElement.ValueKind == JsonValueKind.String)
            {
                return new FailureAction("Chop actions now require explicit tileX/tileY coordinates. Natural-language targets are no longer supported.");
            }

            var tileX = ActionParameterReader.ReadInt(parameters, "tileX");
            var tileY = ActionParameterReader.ReadInt(parameters, "tileY");
            var clamped = validator.ClampTilePosition(tileX, tileY);
            return new ChopAction(clamped);
        }
    }
}

