using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TerrarAI.Content.Actions;
using TerrarAI.Content.Systems;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrarAI.Content.NPCs
{
    public enum AgentState
    {
        Idle,
        Planning,
        Executing,
        Replanning,
        Completed
    }

    public sealed class AIAgentNPC : ModNPC
    {
        private readonly Queue<AgentAction> _actionQueue = new();
        private AgentAction? _currentAction;
        private Task<string>? _plannerTask;
        private readonly ActionValidator _validator = new();

        private string? _currentCommand;
        private string? _replanContext;
        private string? _previousActionResult;  // Tracks result of last completed action for prompt chaining
        private int _replanCycleCount;  // Tracks number of replan cycles to prevent infinite loops
        private string _statusMessage = "Idle";
        private string? _lastPlannerError;
        private Player? _commander;
        private bool _hellevatorMode;
        private int? _hellevatorColumnLeft;
        private float? _hellevatorCenterPixelX;
        private int _autoCollectTicksRemaining;

        // Target locking to prevent switching between resources mid-task
        private Point? _lockedMineTarget;  // For single-tile resources or tree BASE position
        private string? _lockReason;
        private bool _isLockedTargetTree;  // True if locked target is a tree structure


        private const int MAX_REPLAN_CYCLES = 25;  // Maximum replanning attempts before forcing completion

        // Planning timeout tracking
        private long _planningStartTick;
        private long _maxPlanningTicks;

        // Execution timeout tracking
        private long _executionStartTick;
        private long _maxExecutionTicks;
        private const int MAX_EXECUTION_SECONDS = 120; // 2 minutes per command

        // Follower AI - Teleportation and stuck detection
        private int _stuckTimer = 0;
        private Vector2 _lastPosition = Vector2.Zero;
        private int _teleportCooldown = 0;

        // Follower AI - Distance zone constants
        private const float IDLE_DISTANCE = 50f;
        private const float FOLLOW_DISTANCE = 200f;
        private const float CATCHUP_DISTANCE = 500f;
        private const float TELEPORT_DISTANCE = 500f;

        // Follower AI - Speed settings
        private const float IDLE_SPEED = 1f;
        private const float FOLLOW_SPEED = 4f;
        private const float CATCHUP_SPEED = 7f;

        // Follower AI - Acceleration
        private const float ACCELERATION = 0.3f;
        private const float DECELERATION = 0.7f;

        // Follower AI - Teleport settings
        private const int STUCK_FRAMES_TO_TELEPORT = 300; // 5 seconds at 60 FPS
        private const int TELEPORT_COOLDOWN_FRAMES = 60; // 1 second

        // Follower AI - Vertical movement
        private const float CLIMB_HEIGHT_THRESHOLD = 48f; // 3 tiles
        private const float FALL_HEIGHT_THRESHOLD = 32f; // 2 tiles

        // Conversation history for ReAct pattern memory
        private readonly List<(string role, string content)> _conversationHistory = new();
        private const int MAX_HISTORY_MESSAGES = 20; // Last 10 exchanges (user+assistant pairs)

        // Player appearance clone (stored for rendering as player)
        private Player? _appearanceClone;

        // Per-agent randomized movement traits
        private float _baseSpeedMultiplier = 1f;
        private float _baseJumpMultiplier = 1f;
        private float _currentSpeedMultiplier = 1f;
        private float _currentJumpMultiplier = 1f;
        private int _randomizationTimer = 0;
        private const int RANDOMIZATION_INTERVAL = 180; // 3 seconds at 60 FPS

        public override void SetStaticDefaults()
        {
            Main.npcFrameCount[Type] = 1;
        }

        public override string Texture => "Terraria/Images/NPC_0";  // Use vanilla fallback texture since we render as player

        public override void SetDefaults()
        {
            // Use player-like dimensions
            NPC.width = 20;
            NPC.height = 42;
            NPC.friendly = true;
            NPC.dontTakeDamage = true;
            NPC.dontTakeDamageFromHostiles = true;
            NPC.lifeMax = 250;
            NPC.aiStyle = -1; // Custom AI
            NPC.noGravity = false;
            NPC.noTileCollide = false;
            NPC.knockBackResist = 0f;
            NPC.damage = 0;
            NPC.stepSpeed = 0.6f; // Enable auto-step over 1-tile obstacles (like players)
        }

        public override void OnSpawn(IEntitySource source)
        {
            State = AgentState.Idle;
            _statusMessage = "Awaiting command";

            // Set base movement traits for each agent
            var random = new Random(NPC.whoAmI + (int)Main.GameUpdateCount);
            _baseSpeedMultiplier = 0.7f + (float)(random.NextDouble() * 0.6);
            _baseJumpMultiplier = 0.8f + (float)(random.NextDouble() * 0.4);
            _currentSpeedMultiplier = _baseSpeedMultiplier;
            _currentJumpMultiplier = _baseJumpMultiplier;

            // Clone player appearance if spawned by a player
            if (source is EntitySource_Parent parent && parent.Entity is Player player)
            {
                ClonePlayerAppearance(player);
            }
        }

        private void ClonePlayerAppearance(Player sourcePlayer)
        {
            // Create a dummy player object for rendering
            _appearanceClone = new Player();

            // Copy visual appearance
            _appearanceClone.skinVariant = sourcePlayer.skinVariant;
            _appearanceClone.hair = sourcePlayer.hair;
            _appearanceClone.hairDye = sourcePlayer.hairDye;
            _appearanceClone.hairColor = sourcePlayer.hairColor;
            _appearanceClone.skinColor = sourcePlayer.skinColor;
            _appearanceClone.eyeColor = sourcePlayer.eyeColor;
            _appearanceClone.shirtColor = sourcePlayer.shirtColor;
            _appearanceClone.underShirtColor = sourcePlayer.underShirtColor;
            _appearanceClone.pantsColor = sourcePlayer.pantsColor;
            _appearanceClone.shoeColor = sourcePlayer.shoeColor;

            // Copy equipment/armor for visuals
            var armorLength = Math.Min(_appearanceClone.armor.Length, sourcePlayer.armor.Length);
            for (int i = 0; i < armorLength; i++)
            {
                _appearanceClone.armor[i] = sourcePlayer.armor[i].Clone();
            }

            var dyeLength = Math.Min(_appearanceClone.dye.Length, sourcePlayer.dye.Length);
            for (int i = 0; i < dyeLength; i++)
            {
                _appearanceClone.dye[i] = sourcePlayer.dye[i].Clone();
            }

            // Copy hotbar items for visual display (inventory slots 0-9)
            for (int i = 0; i < 10 && i < sourcePlayer.inventory.Length; i++)
            {
                _appearanceClone.inventory[i] = sourcePlayer.inventory[i].Clone();
            }

            // Set male/female
            _appearanceClone.Male = sourcePlayer.Male;
        }

        // Public method to set player appearance (can be called externally)
        public void SetPlayerAppearance(Player player)
        {
            ClonePlayerAppearance(player);
        }

        /// <summary>
        /// Triggers item use animation on the agent's appearance clone.
        /// </summary>
        public void TriggerItemAnimation(Item tool, int duration)
        {
            if (_appearanceClone == null || tool == null || tool.IsAir)
            {
                return;
            }

            // Set the tool as the held item
            _appearanceClone.inventory[_appearanceClone.selectedItem] = tool.Clone();

            // Start animation counters
            _appearanceClone.itemAnimation = duration;
            _appearanceClone.itemTime = duration;
            _appearanceClone.itemAnimationMax = duration;
        }

        /// <summary>
        /// Updates item rotation based on animation progress (called each frame during animation).
        /// </summary>
        private void UpdateItemRotation()
        {
            if (_appearanceClone == null || _appearanceClone.itemAnimation <= 0)
            {
                return;
            }

            // Decrement animation counter
            _appearanceClone.itemAnimation--;

            // Calculate rotation based on animation progress (creates swing effect)
            float progress = 1f - (_appearanceClone.itemAnimation / (float)_appearanceClone.itemAnimationMax);
            _appearanceClone.itemRotation = MathHelper.Lerp(-MathHelper.PiOver4, MathHelper.PiOver4, progress);
        }

        public override void AI()
        {
            if (!ServerAuthority.IsServer)
            {
                // Client copies state via net sync; keep visuals simple.
                MovementHelper.ApplyFriction(NPC, 1.0f);
                UpdateFacing();
                return;
            }

            // Diagnostic logging - log Planning and Executing states
            if (State == AgentState.Planning || State == AgentState.Executing)
            {
                Mod.Logger.Info($"[AI] Called. State={State}, _stateBacking={_stateBacking}, NPC.ai[0]={NPC.ai[0]}, IsServer={ServerAuthority.IsServer}");
            }

            switch (State)
            {
                case AgentState.Idle:
                    PerformFollowPlayerAI();
                    break;
                case AgentState.Planning:
                    TickPlanning();
                    break;
                case AgentState.Executing:
                    Mod.Logger.Info("[AI] Dispatching to TickExecuting()");
                    TickExecuting();
                    Mod.Logger.Info("[AI] Returned from TickExecuting()");
                    break;
                case AgentState.Replanning:
                    TickReplanning();
                    break;
                case AgentState.Completed:
                    ApplyIdlePhysics();
                    _plannerTask = null;  // Safety: ensure planner task is cleared
                    if (_actionQueue.Count == 0 && _currentAction == null)
                    {
                        State = AgentState.Idle;
                        UpdateStatus("Idle");
                    }
                    break;
            }

            TickAutoCollect();
            UpdateFacing();
            UpdateItemRotation();
        }

        public override bool PreDraw(SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
        {
            // If we have a player appearance clone, draw as player
            if (_appearanceClone != null)
            {
                DrawAsPlayer(spriteBatch, screenPos, drawColor);
                return false; // Prevent default NPC drawing
            }

            return true; // Use default drawing if no appearance clone
        }

        private void DrawAsPlayer(SpriteBatch spriteBatch, Vector2 screenPos, Color lightColor)
        {
            if (_appearanceClone == null) return;

            // Update the clone's position and direction to match NPC
            _appearanceClone.position = NPC.position;
            _appearanceClone.direction = NPC.direction;
            _appearanceClone.velocity = NPC.velocity;
            _appearanceClone.fullRotation = 0f;
            _appearanceClone.fullRotationOrigin = Vector2.Zero;

            // Set animation frame based on movement
            if (Math.Abs(NPC.velocity.X) > 0.1f)
            {
                _appearanceClone.legFrame.Y = (int)((Main.GameUpdateCount / 7) % 20) * 56; // Walking animation
            }
            else
            {
                _appearanceClone.legFrame.Y = 0; // Standing
            }
            _appearanceClone.bodyFrame.Y = _appearanceClone.legFrame.Y;
            _appearanceClone.headFrame.Y = 0;

            // Use official tModLoader player renderer
            Main.PlayerRenderer.DrawPlayer(Main.Camera, _appearanceClone, NPC.position, 0f, Vector2.Zero, 0f);
        }

        public override void PostDraw(SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
        {
            var stateText = State.ToString();
            var messageText = string.IsNullOrWhiteSpace(_statusMessage) ? "Ready" : _statusMessage;

            var combined = $"{stateText}: {messageText}";
            var font = FontAssets.MouseText.Value;
            var measurement = font.MeasureString(combined);
            var drawPosition = NPC.Top - screenPos - new Vector2(measurement.X * 0.5f, 24f);

            var color = State switch
            {
                AgentState.Planning => Color.CornflowerBlue,
                AgentState.Executing => Color.LimeGreen,
                AgentState.Replanning => Color.Orange,
                AgentState.Completed => Color.LightGray,
                _ => Color.White
            };

            Utils.DrawBorderString(spriteBatch, combined, drawPosition, color, 0.9f);
        }

        public override void SendExtraAI(BinaryWriter writer)
        {
            writer.Write((int)State);
            writer.Write(_statusMessage ?? string.Empty);
            writer.Write(_lastPlannerError ?? string.Empty);
        }

        public override void ReceiveExtraAI(BinaryReader reader)
        {
            var state = (AgentState)reader.ReadInt32();
            NPC.ai[0] = (int)state;
            _statusMessage = reader.ReadString();
            _lastPlannerError = reader.ReadString();
        }

        public void ReceiveCommand(Player? commander, string command)
        {
            if (!ServerAuthority.IsServer)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            _commander = commander;
            _currentCommand = command.Trim();
            _replanContext = null;
            _previousActionResult = null;
            _replanCycleCount = 0;  // Reset replan counter for new task
            _conversationHistory.Clear();  // Clear conversation history for new task
            _lastPlannerError = null;

            _actionQueue.Clear();
            _currentAction = null;
            _hellevatorMode = DetectHellevatorCommand(_currentCommand);
            _hellevatorColumnLeft = null;
            _hellevatorCenterPixelX = null;
            ClearTargetLock();  // Clear target lock for new command

            // Reset execution timeout for new command
            _executionStartTick = 0;
            _maxExecutionTicks = 0;

            State = AgentState.Planning;
            UpdateStatus("Planning...");

            NPC.TargetClosest();
            BeginPlanning();
        }

        private AgentState State
        {
            get
            {
                if (Main.netMode == NetmodeID.Server)
                {
                    return (AgentState)(int)_stateBacking;
                }

                return (AgentState)(int)NPC.ai[0];
            }
            set
            {
                // Skip state changes on multiplayer clients only (allow single-player and server)
                if (Main.netMode == NetmodeID.MultiplayerClient)
                {
                    return;
                }

                var current = (AgentState)(int)_stateBacking;
                if (current == value)
                {
                    return;
                }

                _stateBacking = (int)value;
                NPC.ai[0] = _stateBacking;
                NPC.netUpdate = true;
            }
        }

        private int _stateBacking;

        private void ApplyIdlePhysics()
        {
            MovementHelper.ApplyFriction(NPC, 1.0f);
        }

        private void PerformFollowPlayerAI()
        {
            // Randomize movement traits every 3 seconds
            _randomizationTimer++;
            if (_randomizationTimer >= RANDOMIZATION_INTERVAL)
            {
                _randomizationTimer = 0;
                var random = new Random(NPC.whoAmI + (int)Main.GameUpdateCount);
                _currentSpeedMultiplier = _baseSpeedMultiplier * (0.5f + (float)(random.NextDouble() * 0.7f));
                _currentJumpMultiplier = _baseJumpMultiplier * (0.5f + (float)(random.NextDouble() * 1.5f));
            }

            Player? target = null;
            if (_commander?.active == true && !_commander.dead)
            {
                target = _commander;
            }
            else
            {
                NPC.TargetClosest(false);
                var candidate = Main.player[NPC.target];
                if (candidate.active && !candidate.dead)
                {
                    target = candidate;
                }
            }

            if (target == null)
            {
                ApplyIdlePhysics();
                ApplyGravityAndCollision();
                return;
            }

            if (_teleportCooldown > 0)
            {
                _teleportCooldown--;
            }

            float distance = Vector2.Distance(NPC.Center, target.Center);

            if (distance > TELEPORT_DISTANCE && _teleportCooldown == 0)
            {
                TeleportToPlayer(target);
                ApplyGravityAndCollision();
                return;
            }

            if (distance > IDLE_DISTANCE)
            {
                Vector2 currentPos = NPC.Center;
                float movementThisFrame = Vector2.Distance(currentPos, _lastPosition);

                if (movementThisFrame < 0.5f && distance > IDLE_DISTANCE)
                {
                    _stuckTimer++;
                }
                else
                {
                    _stuckTimer = 0;
                }

                _lastPosition = currentPos;

                if (_stuckTimer >= STUCK_FRAMES_TO_TELEPORT && _teleportCooldown == 0)
                {
                    TeleportToPlayer(target);
                    ApplyGravityAndCollision();
                    return;
                }
            }
            else
            {
                _stuckTimer = 0;
                _lastPosition = NPC.Center;
            }

            if (distance < IDLE_DISTANCE)
            {
                NPC.velocity.X *= DECELERATION;
                ApplyGravityAndCollision();
            }
            else if (distance < FOLLOW_DISTANCE)
            {
                SmoothMoveToward(target.Center, FOLLOW_SPEED * _currentSpeedMultiplier, ACCELERATION);
                ApplyGravityAndCollision();
            }
            else if (distance < CATCHUP_DISTANCE)
            {
                float catchupSpeed = Math.Min(CATCHUP_SPEED * _currentSpeedMultiplier, target.velocity.Length() + 2f);
                SmoothMoveToward(target.Center, catchupSpeed, ACCELERATION * 1.5f);
                ApplyGravityAndCollision();
            }
            else
            {
                SmoothMoveToward(target.Center, CATCHUP_SPEED * _currentSpeedMultiplier, ACCELERATION * 2f);
                ApplyGravityAndCollision();
            }
        }

        private void ApplyGravityAndCollision()
        {
            NPC.velocity.Y += 0.4f;
            if (NPC.velocity.Y > 10f)
            {
                NPC.velocity.Y = 10f;
            }
        }

        private void CheckAndJump(float moveDirection)
        {
            MovementHelper.TryJump(NPC, moveDirection, 0f);
        }

        private void TeleportToPlayer(Player target)
        {
            if (target == null)
            {
                return;
            }

            Vector2? teleportPos = MovementHelper.FindValidTeleportPosition(target);
            if (teleportPos.HasValue)
            {
                NPC.position = teleportPos.Value - new Vector2(NPC.width / 2f, NPC.height / 2f);
                NPC.velocity = Vector2.Zero;
                _stuckTimer = 0;
                _teleportCooldown = TELEPORT_COOLDOWN_FRAMES;

                if (Main.netMode != NetmodeID.MultiplayerClient)
                {
                    SoundEngine.PlaySound(SoundID.Item6, NPC.position);
                }
            }
        }

        private void SmoothMoveToward(Vector2 targetPosition, float maxSpeed, float accelRate)
        {
            float distanceX = targetPosition.X - NPC.Center.X;
            float distanceY = targetPosition.Y - NPC.Center.Y;

            float desiredVelocityX = 0f;
            if (Math.Abs(distanceX) > 10f)
            {
                desiredVelocityX = Math.Sign(distanceX) * maxSpeed;
            }

            NPC.velocity.X = MathHelper.Lerp(NPC.velocity.X, desiredVelocityX, accelRate);

            bool onGround = MovementHelper.IsOnGround(NPC);

            if (distanceY < -CLIMB_HEIGHT_THRESHOLD && onGround)
            {
                MovementHelper.TryJump(NPC, NPC.velocity.X, distanceY, _currentJumpMultiplier);
            }

            if (distanceY > FALL_HEIGHT_THRESHOLD && onGround && ShouldFallThroughPlatform())
            {
                NPC.position.Y += 1f;
                NPC.velocity.Y = 1f;
            }
        }

        public float GetJumpMultiplier()
        {
            return _currentJumpMultiplier;
        }

        private bool ShouldFallThroughPlatform()
        {
            return MovementHelper.IsStandingOnPlatform(NPC);
        }

        private void UpdateFacing()
        {
            if (NPC.velocity.X > 0.05f)
            {
                NPC.direction = 1;
            }
            else if (NPC.velocity.X < -0.05f)
            {
                NPC.direction = -1;
            }

            NPC.spriteDirection = NPC.direction;
        }

        private void SendChatMessage(string message, Color color)
        {
            if (!ServerAuthority.IsServer)
            {
                return;
            }

            string prefix = string.IsNullOrWhiteSpace(NPC.GivenName) ? "[Agent]" : $"[{NPC.GivenName}]";
            string fullMessage = $"{prefix} {message}";

            if (Main.netMode == NetmodeID.SinglePlayer)
            {
                Main.NewText(fullMessage, color);
            }
            else if (Main.netMode == NetmodeID.Server)
            {
                Terraria.Chat.ChatHelper.BroadcastChatMessage(Terraria.Localization.NetworkText.FromLiteral(fullMessage), color);
            }
        }

        private void TickPlanning()
        {
            if (_plannerTask == null)
            {
                BeginPlanning();
                return;
            }

            // Diagnostic logging
            Mod.Logger.Info($"[TickPlanning] Task status: IsCompleted={_plannerTask.IsCompleted}, IsFaulted={_plannerTask.IsFaulted}, IsCanceled={_plannerTask.IsCanceled}, Status={_plannerTask.Status}");

            if (!_plannerTask.IsCompleted)
            {
                // Check for planning timeout
                long elapsedTicks = Main.GameUpdateCount - _planningStartTick;
                if (elapsedTicks > _maxPlanningTicks)
                {
                    var config = TerrarAI_Config.Get();
                    HandlePlannerFailure($"Planning timed out after {config.MaxPlanningSeconds} seconds. The AI did not respond in time.");
                    return;
                }

                long elapsedSeconds = elapsedTicks / 60;
                UpdateStatus($"Planning with xAI... ({elapsedSeconds}s)");
                ApplyIdlePhysics();
                Mod.Logger.Info("[TickPlanning] Still waiting for task completion...");
                return;
            }

            Mod.Logger.Info("[TickPlanning] Task completed! Parsing response...");

            if (_plannerTask.IsFaulted)
            {
                var error = _plannerTask.Exception?.GetBaseException().Message ?? "Unknown planning error.";
                Mod.Logger.Error($"[TickPlanning] Task faulted: {error}");
                HandlePlannerFailure(error);
                return;
            }

            var response = _plannerTask.Result;
            Mod.Logger.Info($"[TickPlanning] Got response, length={response.Length}");
            _plannerTask = null;

            try
            {
                Mod.Logger.Info($"[TickPlanning] Parsing response: {response}");
                var actions = ActionParser.Parse(response, NPC, _validator, _commander);
                QueueActions(actions);
                State = AgentState.Executing;
                UpdateStatus("Executing plan...");
                SendChatMessage($"Planning complete! Executing {actions.Count} action(s).", Color.LightGreen);
                Mod.Logger.Info($"[TickPlanning] Successfully transitioned to Executing state with {actions.Count} actions");
            }
            catch (ActionParserException ex)
            {
                Mod.Logger.Error($"[TickPlanning] ActionParserException: {ex.Message}");
                HandlePlannerFailure($"Parser error: {ex.Message}");
            }
            catch (Exception ex)
            {
                Mod.Logger.Error($"[TickPlanning] Unexpected exception: {ex}");
                HandlePlannerFailure($"Unexpected error: {ex.Message}");
            }
        }

        private void TickReplanning()
        {
            if (_plannerTask == null)
            {
                BeginPlanning();
                return;
            }

            TickPlanning();
        }

        private void TickExecuting()
        {
            // Initialize execution timeout on first tick
            if (_executionStartTick == 0)
            {
                _executionStartTick = Main.GameUpdateCount;
                _maxExecutionTicks = MAX_EXECUTION_SECONDS * 60;
            }

            // Check for execution timeout (global safety net)
            long elapsedTicks = Main.GameUpdateCount - _executionStartTick;
            if (elapsedTicks > _maxExecutionTicks)
            {
                // Force failure and transition to replanning
                Mod.Logger.Warn($"[TickExecuting] Execution timed out after {MAX_EXECUTION_SECONDS}s");

                _currentAction?.Reset();
                _currentAction = null;
                _actionQueue.Clear();

                _replanContext = $"Action execution timed out after {MAX_EXECUTION_SECONDS}s. Task may be impossible or stuck.";
                State = AgentState.Replanning;
                UpdateStatus("Execution timeout, replanning...");
                SendChatMessage($"Action took too long ({MAX_EXECUTION_SECONDS}s), trying different approach.", Color.Orange);
                return;
            }

            Mod.Logger.Info($"[TickExecuting] Entry - _currentAction={((_currentAction == null) ? "null" : _currentAction.Name)}, queueCount={_actionQueue.Count}");

            if (_currentAction == null)
            {
                if (_actionQueue.Count == 0)
                {
                    Mod.Logger.Info("[TickExecuting] No actions in queue, transitioning to Completed");
                    State = AgentState.Completed;
                    UpdateStatus("Plan complete.");
                    return;
                }

                _currentAction = _actionQueue.Dequeue();
                Mod.Logger.Info($"[TickExecuting] Dequeued action: {_currentAction.Name}");
                _currentAction.Reset();
                UpdateStatus($"Executing {_currentAction.Name}...");

                // TARGET LOCKING: Lock onto first ChopAction or MineAction target to prevent switching
                if ((_currentAction is ChopAction chopAction || _currentAction is MineAction mineAction) && !_lockedMineTarget.HasValue)
                {
                    var targetTile = _currentAction.GetTargetTile();
                    if (targetTile.HasValue)
                    {
                        var tile = Framing.GetTileSafely(targetTile.Value.X, targetTile.Value.Y);

                        // Check if this is a tree - if so, lock onto tree base instead of individual tile
                        if (TreeHelper.IsTreeTile(targetTile.Value))
                        {
                            var treeBase = TreeHelper.FindTreeBase(targetTile.Value);
                            if (treeBase.HasValue)
                            {
                                _lockedMineTarget = treeBase.Value;
                                _lockReason = "tree";
                                _isLockedTargetTree = true;

                                if (TerrarAI_Config.Get().EnableVerboseLogging)
                                {
                                    Mod.Logger.Info($"[Target Lock] Locked onto TREE at base tile({_lockedMineTarget.Value.X},{_lockedMineTarget.Value.Y})");
                                }
                            }
                        }
                        else
                        {
                            // Regular single-tile resource
                            _lockedMineTarget = targetTile.Value;
                            _lockReason = tile.HasTile ? TileID.Search.GetName(tile.TileType) : "resource";
                            _isLockedTargetTree = false;

                            if (TerrarAI_Config.Get().EnableVerboseLogging)
                            {
                                Mod.Logger.Info($"[Target Lock] Locked onto {_lockReason} at tile({_lockedMineTarget.Value.X},{_lockedMineTarget.Value.Y})");
                            }
                        }
                    }
                }
            }

            // Range validation: Check if agent is close enough to target
            // Don't check range for MoveAction - it handles its own distance logic
            if (_hellevatorMode && _currentAction is MineAction hellevatorMine)
            {
                var targetTile = hellevatorMine.GetTargetTile();
                if (targetTile.HasValue)
                {
                    EnsureHellevatorColumnInitialized(targetTile.Value.X);
                    var clamped = ClampToHellevatorColumn(targetTile.Value);
                    if (clamped != targetTile.Value)
                    {
                        _currentAction.Reset();
                        _currentAction = new MineAction(clamped);
                        hellevatorMine = (MineAction)_currentAction;
                        targetTile = clamped;
                        UpdateStatus($"Aligning hellevator shaft to tile({clamped.X},{clamped.Y})");
                    }
                }

                if (!EnsureHellevatorCenter())
                {
                    return;
                }
            }

            if (_currentAction is not MoveAction)
            {
                if (!MovementHelper.IsOnGround(NPC) && !MovementHelper.IsStandingOnPlatform(NPC))
                {
                    UpdateStatus("Waiting to land...");
                    ApplyGravityAndCollision();
                    return;
                }

                var (distance, targetPos) = GetTargetInfo(_currentAction);
                float requiredRange = _currentAction.GetRequiredRange();

                if (requiredRange > 0f && distance > requiredRange)
                {
                    UpdateStatus($"Approaching target... ({distance:F0}px away)");

                    if (targetPos.HasValue)
                    {
                        // Create temporary move action to approach target
                        var targetTile = new Point((int)(targetPos.Value.X / 16f), (int)(targetPos.Value.Y / 16f));
                        var moveAction = new MoveAction(targetTile);
                        var moveContext = AgentActionContext.From(NPC, _commander);
                        moveAction.Tick(moveContext);  // Execute movement this tick
                    }

                    return;  // Continue approaching next tick
                }
            }

            var context = AgentActionContext.From(NPC, _commander);
            Mod.Logger.Info($"[TickExecuting] About to execute action: {_currentAction.Name}");
            var result = _currentAction.Tick(context);
            Mod.Logger.Info($"[TickExecuting] Action {_currentAction.Name} returned status: {result.Status}, message: {result.Message}");

            switch (result.Status)
            {
                case AgentActionStatus.Pending:
                    if (!string.IsNullOrWhiteSpace(result.Message))
                    {
                        UpdateStatus(result.Message);
                    }

                    // Apply gentle friction to allow stepSpeed to work during stationary actions
                    // Note: MineAction and ChopAction apply their own minimal velocity maintenance
                    if (_currentAction is not MoveAction)
                    {
                        MovementHelper.ApplyFriction(NPC, 0.3f);  // Lighter friction allows stepSpeed
                        // Don't dampen Y velocity - let gravity work naturally for step-up
                    }
                    break;
                case AgentActionStatus.Success:
                    if (!string.IsNullOrWhiteSpace(result.Message))
                    {
                        UpdateStatus(result.Message);
                    }

                    // Auto-collect any nearby items after successful action
                    CollectNearbyItems();
                    ScheduleAutoCollectBurst();

                    // Store the result for the next planning cycle (prompt chaining)
                    var actionName = _currentAction.Name;
                    var isCompleteAction = _currentAction is CompleteAction;
                    _previousActionResult = !string.IsNullOrWhiteSpace(result.Message)
                        ? $"{actionName}: {result.Message}"
                        : $"{actionName} completed successfully";

                    _currentAction.Reset();
                    _currentAction = null;

                    // Auto-clear lock if target is fully destroyed (tree fully chopped, ore vein depleted, etc.)
                    if (_lockedMineTarget.HasValue && !ShouldMaintainLock())
                    {
                        ClearTargetLock();
                    }

                    // PROMPT CHAINING: Check if this was a CompleteAction
                    if (isCompleteAction)
                    {
                        // Task is complete, transition to Completed state
                        State = AgentState.Completed;
                        UpdateStatus("Task complete.");
                        ClearHellevatorState();  // Clear hellevator state on completion
                        ClearTargetLock();  // Clear target lock on completion
                        _previousActionResult = null;  // Clear for next command
                        _plannerTask = null;  // Clear planner task to allow transition to Idle
                    }
                    else
                    {
                        // Increment replan cycle counter
                        _replanCycleCount++;

                        // Check if we've exceeded max replan cycles (prevents infinite loops)
                        if (_replanCycleCount >= MAX_REPLAN_CYCLES)
                        {
                            State = AgentState.Completed;
                            UpdateStatus($"Task incomplete after {MAX_REPLAN_CYCLES} attempts.");
                            SendChatMessage($"I've tried {MAX_REPLAN_CYCLES} times but can't complete this task. Giving up.", Color.Orange);
                            ClearHellevatorState();  // Clear hellevator state on max replan cycles
                            _replanCycleCount = 0;
                            _plannerTask = null;
                            _previousActionResult = null;
                        }
                        else
                        {
                            // Clear remaining queue and replan after this action
                            _actionQueue.Clear();
                            _replanContext = _previousActionResult;
                            State = AgentState.Replanning;
                            UpdateStatus($"Replanning next action... (cycle {_replanCycleCount}/{MAX_REPLAN_CYCLES})");
                            BeginPlanning();
                        }
                    }
                    break;
                case AgentActionStatus.Failure:
                    var failureReason = result.Message ?? "Action failed.";

                    // Check if failure is due to unreachable target
                    bool isUnreachableFailure = failureReason.Contains("out of range", StringComparison.OrdinalIgnoreCase) ||
                                                failureReason.Contains("drifted", StringComparison.OrdinalIgnoreCase) ||
                                                failureReason.Contains("could not reach", StringComparison.OrdinalIgnoreCase);

                    if (isUnreachableFailure && _lockedMineTarget.HasValue)
                    {
                        SendChatMessage($"Original target ({_lockReason ?? "resource"}) at tile({_lockedMineTarget.Value.X},{_lockedMineTarget.Value.Y}) became unreachable. Adapting to find new target.", Color.Orange);
                        ClearTargetLock();
                    }

                    _currentAction.Reset();
                    _currentAction = null;
                    _actionQueue.Clear();
                    ClearHellevatorState();  // Clear hellevator state on failure

                    _replanContext = failureReason;
                    State = AgentState.Replanning;
                    UpdateStatus("Replanning due to failure...");
                    BeginPlanning();
                    break;
            }
        }

        private bool IsInRange(AgentAction action)
        {
            float requiredRange = action.GetRequiredRange();
            if (requiredRange <= 0f)
            {
                return true;  // No range requirement
            }

            var (distance, _) = GetTargetInfo(action);
            return distance <= requiredRange;
        }

        private (float distance, Vector2? position) GetTargetInfo(AgentAction action)
        {
            Vector2? targetPos = null;

            // Convert tile-based targets to world coordinates
            if (action.GetTargetTile() is Point tile)
            {
                targetPos = new Vector2(tile.X * 16f + 8f, tile.Y * 16f + 8f);
            }
            // Return position-based targets directly
            else if (action.GetTargetPosition() is Vector2 pos)
            {
                targetPos = pos;
            }

            if (targetPos.HasValue)
            {
                float distance = Vector2.Distance(NPC.Center, targetPos.Value);
                return (distance, targetPos);
            }

            return (0f, null);  // No target
        }

        private void QueueActions(IReadOnlyList<AgentAction> actions)
        {
            _actionQueue.Clear();
            foreach (var action in actions)
            {
                _actionQueue.Enqueue(action);
            }

            UpdateStatus($"Queued {_actionQueue.Count} actions.");
        }

        private void BeginPlanning()
        {
            if (string.IsNullOrWhiteSpace(_currentCommand))
            {
                HandlePlannerFailure("No command provided.");
                return;
            }

            // Initialize timeout tracking
            var config = TerrarAI_Config.Get();
            _planningStartTick = Main.GameUpdateCount;
            _maxPlanningTicks = config.MaxPlanningSeconds * 60; // Convert seconds to ticks (60 FPS)

            // Notify player that planning has started
            string commandPreview = _currentCommand!.Length > 50
                ? _currentCommand.Substring(0, 47) + "..."
                : _currentCommand;
            SendChatMessage($"Planning: \"{commandPreview}\"", Color.CornflowerBlue);

            _plannerTask = ExecutePlanningAsync();
        }

        private async Task<string> ExecutePlanningAsync()
        {
            try
            {
                Mod.Logger.Info("[ExecutePlanningAsync] Starting...");
                var systemPrompt = BuildSystemPrompt();
                var userPrompt = BuildUserPrompt(_currentCommand!, _replanContext);

                // Use non-streaming API call (simpler)
                var result = await TerrarAI.RequireClient()
                    .SendChatCompletionAsync(systemPrompt, userPrompt, _conversationHistory, CancellationToken.None)
                    .ConfigureAwait(false);

                // Store assistant's response in conversation history for context continuity
                _conversationHistory.Add(("assistant", result));

                // Send LLM response to chat if ShowAgentThoughts is enabled
                var config = TerrarAI_Config.Get();
                if (config.ShowAgentThoughts)
                {
                    // Queue the message to be sent on the main thread
                    Main.QueueMainThreadAction(() =>
                    {
                        SendChatMessage(result, Color.LightBlue);
                    });
                }

                Mod.Logger.Info($"[ExecutePlanningAsync] Completed! Result length={result.Length}, content={result}");
                return result;
            }
            catch (Exception ex)
            {
                Mod.Logger.Error($"[ExecutePlanningAsync] Exception: {ex}");
                throw new InvalidOperationException($"xAI request failed: {ex.Message}", ex);
            }
        }

        private void HandlePlannerFailure(string error)
        {
            _plannerTask = null;
            _lastPlannerError = error;
            State = AgentState.Completed;
            UpdateStatus($"Planner error: {error}");
            Mod.Logger.Warn($"TerrarAI planner failed: {error}");

            // Send detailed error to chat
            SendChatMessage($"Planning failed: {error}", Color.OrangeRed);

            // Provide helpful troubleshooting hints based on error type
            var config = TerrarAI_Config.Get();
            if (error.Contains("timed out") || error.Contains("timeout"))
            {
                SendChatMessage($"Tip: Increase timeout in config (currently {config.RequestTimeoutSeconds}s) or check your internet connection.", Color.Yellow);
            }
            else if (error.Contains("API key") || error.Contains("Unauthorized") || error.Contains("401"))
            {
                string apiKeyStatus = string.IsNullOrWhiteSpace(config.GetEffectiveApiKey()) ? "not set" : "set but may be invalid";
                SendChatMessage($"Tip: Check your xAI API key in TerrarAI config (currently {apiKeyStatus}).", Color.Yellow);
            }
            else if (error.Contains("Parser error"))
            {
                SendChatMessage("Tip: The AI's response was malformed. Try rephrasing your command or enable verbose logging to see the raw response.", Color.Yellow);
            }
            else if (error.Contains("network") || error.Contains("connection") || error.Contains("endpoint"))
            {
                SendChatMessage($"Tip: Check your network connection and API endpoint ({config.BaseEndpoint}).", Color.Yellow);
            }

            // If verbose logging is enabled, mention it
            if (config.EnableVerboseLogging)
            {
                SendChatMessage("Verbose logging is enabled. Check the log file for detailed API request/response information.", Color.Gray);
            }
            else
            {
                SendChatMessage("Enable 'Verbose Logging' in TerrarAI config to see detailed API information in logs.", Color.Gray);
            }
        }

        private string BuildSystemPrompt()
        {
            var sb = new StringBuilder();
            var pixelPos = NPC.Center;
            var tilePos = NPC.Center / 16f;
            var agentTileX = (int)tilePos.X;
            var agentTileY = (int)tilePos.Y;

            sb.AppendLine("You are an autonomous AI agent inside Terraria using the ReAct (Reasoning and Acting) pattern.");
            sb.AppendLine("You perform ONE action at a time, observe the results, then decide the next action.");
            sb.AppendLine("This allows you to adapt to changing conditions and unexpected outcomes.");

            // Directional context
            sb.AppendLine();
            sb.AppendLine("COORDINATE SYSTEM:");
            sb.AppendLine($"- You are facing: {(NPC.direction > 0 ? "RIGHT" : "LEFT")} (direction={NPC.direction})");
            sb.AppendLine("- X axis: increases rightward (→), decreases leftward (←)");
            sb.AppendLine("- Y axis: increases downward (↓), decreases upward (↑)");
            sb.AppendLine($"- Your position: tile({agentTileX},{agentTileY}) = pixels({pixelPos.X:F0},{pixelPos.Y:F0})");
            sb.AppendLine("- All tile descriptions include BOTH tile and pixel coordinates - use the pixel coordinates directly");
            sb.AppendLine("- \"In front of you\" = tiles with higher X if facing right, lower X if facing left");

            // List available tools from commander's inventory
            sb.AppendLine();
            sb.AppendLine("AVAILABLE TOOLS:");
            if (_commander != null)
            {
                var pickaxe = ToolSelector.FindBestTool(_commander, ToolType.Pickaxe);
                var axe = ToolSelector.FindBestTool(_commander, ToolType.Axe);
                var weapon = ToolSelector.FindBestTool(_commander, ToolType.Weapon);

                if (pickaxe != null && !pickaxe.IsAir)
                {
                    sb.AppendLine($"- Pickaxe: {ToolSelector.GetToolDescription(pickaxe, ToolType.Pickaxe)}");
                }
                else
                {
                    sb.AppendLine("- Pickaxe: None (cannot mine stone/ore)");
                }

                if (axe != null && !axe.IsAir)
                {
                    sb.AppendLine($"- Axe: {ToolSelector.GetToolDescription(axe, ToolType.Axe)}");
                }
                else
                {
                    sb.AppendLine("- Axe: None (cannot chop trees/wood)");
                }

                if (weapon != null && !weapon.IsAir)
                {
                    sb.AppendLine($"- Weapon: {ToolSelector.GetToolDescription(weapon, ToolType.Weapon)}");
                }
            }
            else
            {
                sb.AppendLine("- No tools available (no commander)");
            }

            sb.AppendLine();
            sb.AppendLine("AVAILABLE ACTIONS:");
            sb.AppendLine("- move(tileX, tileY): Move to a tile. Jumps over obstacles and gaps automatically.");
            sb.AppendLine("- chop(tileX, tileY): Chop a tree trunk. Auto-moves to target.");
            sb.AppendLine("- mine(tileX, tileY): Mine ore/stone. Auto-moves to target.");
            sb.AppendLine("- say(text): Broadcast a chat message to all players.");
            sb.AppendLine("- place(tileX, tileY, blockType): Place a tile at absolute grid coordinates (1=dirt, 2=stone, 9=wood). Auto-moves first.");
            sb.AppendLine("- complete(message): Signal that the task is finished.");
            sb.AppendLine("- All actions use ABSOLUTE coordinates (not relative).");
            sb.AppendLine("- chop/mine actions automatically move you close enough.");
            sb.AppendLine("- Items are AUTO-COLLECTED after every action! Mining/chopping drops items which are automatically added to inventory.");

            sb.AppendLine();
            sb.AppendLine("CURRENT STATE:");
            sb.AppendLine($"- Position: tile({agentTileX},{agentTileY})");
            sb.AppendLine($"- Facing: {(NPC.direction > 0 ? "RIGHT (→)" : "LEFT (←)")}");
            sb.AppendLine($"- Health: {NPC.life}/{NPC.lifeMax}");

            // Add hellevator state when active
            if (_hellevatorMode && _hellevatorColumnLeft.HasValue && _hellevatorCenterPixelX.HasValue)
            {
                int leftCol = _hellevatorColumnLeft.Value;
                int rightCol = leftCol + 1;
                float centerPx = _hellevatorCenterPixelX.Value;
                sb.AppendLine($"- Hellevator Shaft: Mining columns X=[{leftCol}, {rightCol}], centered at {centerPx:F0}px");
                sb.AppendLine($"- Current Depth: Y={agentTileY} (descending from surface)");
            }

            sb.AppendLine();
            sb.AppendLine(DescribeInventory());
            sb.AppendLine();

            // Use simplified WorldContext for environment info (with target lock info)
            var worldContext = new WorldContext(NPC, _commander, _lockedMineTarget, _lockReason);
            sb.Append(worldContext.GetContextSummary());

            sb.AppendLine();
            sb.AppendLine("IMPORTANT RULES:");
            sb.AppendLine("- Use tile coordinates from YOUR SITUATION (tileX, tileY).");
            sb.AppendLine("- Do NOT use the \"target\" field (e.g., nearest_trees). It is unsupported and will fail.");
            sb.AppendLine("- Check YOUR SITUATION for available resources and player positions before acting.");
            sb.AppendLine("- mine/chop actions automatically move you close enough.");
            sb.AppendLine("- move() jumps over gaps and obstacles automatically.");
            sb.AppendLine("- If movement fails, you'll see the failure reason and can replan.");
            sb.AppendLine("- Return ONLY ONE action per response using the ReAct format.");
            sb.AppendLine("- Respond ONLY with valid JSON - no explanations or markdown.");
            sb.AppendLine("- Use 'complete' action when the task is finished.");
            sb.AppendLine();
            sb.AppendLine("⚠️ TARGET LOCKING RULE:");
            sb.AppendLine("- If you see '⚠️ CURRENT TARGET' in Nearby Resources, you MUST continue mining that exact target.");
            sb.AppendLine("- DO NOT switch to different trees/ores of the same type - finish the current target first.");
            sb.AppendLine("- TREES ARE MULTI-TILE: When chopping a tree, you'll see 'next trunk tile' - mine that tile, then the system shows the next one.");
            sb.AppendLine("- Continue mining trunk tiles until you see 'Tree fully chopped' - only then can you select a new tree.");
            sb.AppendLine("- Only after the current target is fully mined should you select a new target.");
            sb.AppendLine("- Switching mid-task wastes time and leaves incomplete resources.");

            // Add hellevator-specific rules when in hellevator mode
            if (_hellevatorMode)
            {
                sb.AppendLine();
                sb.AppendLine("⚠️ HELLEVATOR MODE ACTIVE - Special Rules:");
                sb.AppendLine("- Mine BOTH tiles (left and right) at each Y level before descending to next row");
                sb.AppendLine("- Work top-to-bottom: Complete current row (Y=N) before moving to next row (Y=N+1)");
                sb.AppendLine("- Recommended pattern: mine(leftX,Y) → mine(rightX,Y) → mine(leftX,Y+1) → mine(rightX,Y+1) → ...");
                sb.AppendLine("- Gravity pulls you down automatically through cleared shaft - NO move actions needed!");
                sb.AppendLine("- System auto-centers you horizontally in the 2-tile shaft - focus only on Y progression");
                sb.AppendLine("- Plan in small batches: 4-6 mine actions (2-3 rows) before replanning to adapt to obstacles");
                sb.AppendLine("- Check NEARBY TILES - stop and use 'complete' if you encounter lava, unmineable blocks, or large caverns");
                sb.AppendLine("- Your X coordinates will be automatically clamped to maintain the 2-wide shaft alignment");
            }

            sb.AppendLine();
            sb.AppendLine("REACT FORMAT:");
            sb.AppendLine("{");
            sb.AppendLine("  \"observation\": \"What you see in the current state\",");
            sb.AppendLine("  \"thought\": \"Your reasoning about what to do next\",");
            sb.AppendLine("  \"action\": {\"type\": \"action_name\", \"params\": {...}}");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("CONCRETE EXAMPLES:");

            int exampleTargetTileX = agentTileX + 5;
            int exampleTargetTileY = agentTileY;

            sb.AppendLine($"Example 1: Task is \"Mine copper ore\", Ores shows: \"Copper: tile({exampleTargetTileX},{exampleTargetTileY})\"");
            sb.AppendLine("{");
            sb.AppendLine($"  \"observation\": \"Copper at tile({exampleTargetTileX},{exampleTargetTileY})\",");
            sb.AppendLine($"  \"thought\": \"Mine the copper\",");
            sb.AppendLine($"  \"action\": {{\"type\":\"mine\",\"params\":{{\"tileX\":{exampleTargetTileX},\"tileY\":{exampleTargetTileY}}}}}");
            sb.AppendLine("}");
            sb.AppendLine($"  (mine action auto-moves, no separate move needed)");
            sb.AppendLine($"  After mining:");
            sb.AppendLine("{");
            sb.AppendLine($"  \"observation\": \"Copper ore mined successfully\",");
            sb.AppendLine($"  \"thought\": \"Task complete\",");
            sb.AppendLine($"  \"action\": {{\"type\":\"complete\",\"params\":{{\"message\":\"Mined copper ore\"}}}}");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"Example 2: Task is \"Go to tile(100,50)\"");
            sb.AppendLine("{");
            sb.AppendLine($"  \"observation\": \"Need to move to tile(100,50)\",");
            sb.AppendLine($"  \"thought\": \"Move to target tile\",");
            sb.AppendLine($"  \"action\": {{\"type\":\"move\",\"params\":{{\"tileX\":100,\"tileY\":50}}}}");
            sb.AppendLine("}");
            sb.AppendLine($"  If movement fails:");
            sb.AppendLine("{");
            sb.AppendLine($"  \"observation\": \"Previous action failed: Movement stalled near tile(80,50). Obstacle blocking path.\",");
            sb.AppendLine($"  \"thought\": \"Try alternate path via tile(80,55)\",");
            sb.AppendLine($"  \"action\": {{\"type\":\"move\",\"params\":{{\"tileX\":80,\"tileY\":55}}}}");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"Example 3: Task is \"Chop trees\", Trees shows: \"Tree: tile({exampleTargetTileX},{exampleTargetTileY})\"");
            sb.AppendLine("{");
            sb.AppendLine($"  \"observation\": \"Tree at tile({exampleTargetTileX},{exampleTargetTileY})\",");
            sb.AppendLine($"  \"thought\": \"Chop the tree\",");
            sb.AppendLine($"  \"action\": {{\"type\":\"chop\",\"params\":{{\"tileX\":{exampleTargetTileX},\"tileY\":{exampleTargetTileY}}}}}");
            sb.AppendLine("}");
            sb.AppendLine($"  (chop action auto-moves, no separate move needed)");
            sb.AppendLine($"  After chopping:");
            sb.AppendLine("{");
            sb.AppendLine($"  \"observation\": \"Tree chopped successfully\",");
            sb.AppendLine($"  \"thought\": \"Task complete\",");
            sb.AppendLine($"  \"action\": {{\"type\":\"complete\",\"params\":{{\"message\":\"Chopped tree\"}}}}");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"Example 4: Task is \"Chop trees\" but no trees exist");
            sb.AppendLine("{");
            sb.AppendLine($"  \"observation\": \"Moved in multiple directions, scanned 81x81 tile area multiple times, no trees found anywhere\",");
            sb.AppendLine($"  \"thought\": \"After extensive searching across many cycles, no trees are available in this area. Task is impossible.\",");
            sb.AppendLine($"  \"action\": {{\"type\":\"complete\",\"params\":{{\"message\":\"No trees found in the area after extensive searching. Task cannot be completed.\"}}}}");
            sb.AppendLine("}");
            sb.AppendLine();

            // Example 4: Hellevator digging pattern
            int hellevatorStartX = agentTileX;
            int hellevatorStartY = agentTileY + 2;  // Start digging below current position
            sb.AppendLine($"Example 4: Task is \"dig a hellevator\" starting at current position");
            sb.AppendLine($"  First action - Mine left tile of first row:");
            sb.AppendLine("{");
            sb.AppendLine($"  \"observation\": \"At tile({agentTileX},{agentTileY}), need to dig vertical 2x2 shaft downward\",");
            sb.AppendLine($"  \"thought\": \"Start hellevator by mining left tile of first row. System will auto-align to create consistent 2-wide shaft.\",");
            sb.AppendLine($"  \"action\": {{\"type\":\"mine\",\"params\":{{\"tileX\":{hellevatorStartX},\"tileY\":{hellevatorStartY}}}}}");
            sb.AppendLine("}");
            sb.AppendLine($"  Second action - Mine right tile of same row:");
            sb.AppendLine("{");
            sb.AppendLine($"  \"observation\": \"Mined tile({hellevatorStartX},{hellevatorStartY}), system centered me in 2-tile shaft\",");
            sb.AppendLine($"  \"thought\": \"Complete this row by mining the adjacent tile to the right, creating full 2-wide opening\",");
            sb.AppendLine($"  \"action\": {{\"type\":\"mine\",\"params\":{{\"tileX\":{hellevatorStartX + 1},\"tileY\":{hellevatorStartY}}}}}");
            sb.AppendLine("}");
            sb.AppendLine($"  Third action - Mine left tile of next row down:");
            sb.AppendLine("{");
            sb.AppendLine($"  \"observation\": \"Completed row at Y={hellevatorStartY}, gravity is pulling me down into the cleared shaft\",");
            sb.AppendLine($"  \"thought\": \"Descend by mining left tile of next row. No move action needed - gravity handles vertical movement.\",");
            sb.AppendLine($"  \"action\": {{\"type\":\"mine\",\"params\":{{\"tileX\":{hellevatorStartX},\"tileY\":{hellevatorStartY + 1}}}}}");
            sb.AppendLine("}");
            sb.AppendLine($"  Fourth action - Mine right tile, continue pattern:");
            sb.AppendLine("{");
            sb.AppendLine($"  \"observation\": \"Mining efficiently in 2-wide shaft, descending steadily row by row\",");
            sb.AppendLine($"  \"thought\": \"Continue alternating left-right-left-right pattern. System keeps me centered, gravity pulls me down.\",");
            sb.AppendLine($"  \"action\": {{\"type\":\"mine\",\"params\":{{\"tileX\":{hellevatorStartX + 1},\"tileY\":{hellevatorStartY + 1}}}}}");
            sb.AppendLine("}");
            sb.AppendLine($"  Continue this pattern (mine both tiles per row, descend) until reaching desired depth or obstacle.");

            return sb.ToString();
        }

        private string BuildUserPrompt(string command, string? context)
        {
            var sb = new StringBuilder();

            // Manage conversation history
            if (_replanCycleCount == 0)
            {
                // First cycle: Initialize history with the task
                _conversationHistory.Clear();
                _conversationHistory.Add(("user", $"Task: {command}"));
            }
            else if (!string.IsNullOrWhiteSpace(context))
            {
                // Subsequent cycles: Add action feedback to history
                _conversationHistory.Add(("user", $"Action result: {context}"));
            }

            // Trim history to prevent token limit issues
            while (_conversationHistory.Count > MAX_HISTORY_MESSAGES)
            {
                _conversationHistory.RemoveAt(0);
            }

            // Original task
            sb.AppendLine("ORIGINAL TASK:");
            sb.AppendLine(command);
            sb.AppendLine();

            // Replan cycle tracking
            if (_replanCycleCount > 0)
            {
                sb.AppendLine($"REPLAN CYCLE: {_replanCycleCount}/{MAX_REPLAN_CYCLES}");
                if (_replanCycleCount >= 10)
                {
                    sb.AppendLine("⚠️ WARNING: Many replan cycles detected. If the task is impossible or resources are unavailable,");
                    sb.AppendLine("use the 'complete' action with a message explaining why the task cannot be completed.");
                }
                sb.AppendLine();
            }

            // Previous action result (for prompt chaining)
            if (!string.IsNullOrWhiteSpace(context))
            {
                // Check if this is a failure context (contains "failed" or "error")
                bool isFailure = context.Contains("failed", StringComparison.OrdinalIgnoreCase)
                              || context.Contains("error", StringComparison.OrdinalIgnoreCase)
                              || context.Contains("cannot", StringComparison.OrdinalIgnoreCase);

                if (isFailure)
                {
                    sb.AppendLine("PREVIOUS ACTION FAILED:");
                    sb.AppendLine(context);
                    sb.AppendLine("Analyze what went wrong and choose a different approach.");
                }
                else
                {
                    sb.AppendLine("PREVIOUS ACTION RESULT:");
                    sb.AppendLine(context);
                    sb.AppendLine("The above action completed successfully. Continue with the next step toward completing the original task.");
                }
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("This is the FIRST action for this task. Analyze the current state and decide the best first step.");
                sb.AppendLine();
            }

            sb.AppendLine("INSTRUCTIONS:");
            sb.AppendLine("1. Observe the current state (position, nearby tiles, resources, inventory)");
            sb.AppendLine("2. Think about what needs to be done next to complete the task");
            sb.AppendLine("3. Return ONE action using the ReAct JSON format");
            sb.AppendLine("4. Use 'complete' action when the task is fully accomplished");
            sb.AppendLine();
            sb.AppendLine("Return JSON only in this format:");
            sb.AppendLine("{");
            sb.AppendLine("  \"observation\": \"What you observe about the current situation\",");
            sb.AppendLine("  \"thought\": \"Your reasoning about the next action\",");
            sb.AppendLine("  \"action\": {\"type\": \"action_type\", \"params\": {...}}");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private string DescribeNearbyTiles()
        {
            const int scanRadius = 40; // Scan 81x81 grid (40 tiles in each direction - approximately screen size)
            const float maxReach = 80f; // 5 tiles * 16 pixels = standard player reach

            var agentTileX = (int)(NPC.Center.X / 16f);
            var agentTileY = (int)(NPC.Center.Y / 16f);

            // Group tiles by type for clarity
            var tileGroups = new Dictionary<string, List<(int x, int y, float distance, bool reachable)>>();

            for (int y = -scanRadius; y <= scanRadius; y++)
            {
                for (int x = -scanRadius; x <= scanRadius; x++)
                {
                    var checkX = agentTileX + x;
                    var checkY = agentTileY + y;
                    var tile = Framing.GetTileSafely(checkX, checkY);

                    if (!tile.HasTile)
                    {
                        continue; // Skip air tiles to reduce noise
                    }

                    var tileName = TileID.Search.GetName(tile.TileType);

                    // Calculate distance from agent center to tile center
                    var tileCenterX = checkX * 16f + 8f;
                    var tileCenterY = checkY * 16f + 8f;
                    var distance = Vector2.Distance(NPC.Center, new Vector2(tileCenterX, tileCenterY));
                    var reachable = distance <= maxReach;

                    if (!tileGroups.ContainsKey(tileName))
                    {
                        tileGroups[tileName] = new List<(int, int, float, bool)>();
                    }

                    tileGroups[tileName].Add((checkX, checkY, distance, reachable));
                }
            }

            // Build formatted output
            var builder = new StringBuilder();
            builder.AppendLine($"NEARBY TILES ({scanRadius * 2 + 1}x{scanRadius * 2 + 1} scan, agent at tile ({agentTileX},{agentTileY})):");

            // Sort tile groups by importance (reachable resources first, then common blocks)
            var sortedGroups = tileGroups
                .OrderBy(kvp => !IsResourceTile(kvp.Key))  // Resources first
                .ThenBy(kvp => !kvp.Value.Any(t => t.reachable))  // Reachable first
                .ThenBy(kvp => kvp.Value.Min(t => t.distance));  // Closest first

            foreach (var group in sortedGroups.Take(15))  // Limit to top 15 tile types to avoid spam
            {
                var tileName = group.Key;
                var tiles = group.Value.OrderBy(t => t.distance).ToList();

                // Show closest 3-5 tiles of each type
                var closestTiles = tiles.Take(5).ToList();
                var reachableCount = tiles.Count(t => t.reachable);

                builder.Append($"- {tileName}: ");

                foreach (var (tileX, tileY, distance, reachable) in closestTiles)
                {
                    var relX = tileX - agentTileX;
                    var relY = tileY - agentTileY;
                    var relPixelX = relX * 16f;
                    var relPixelY = relY * 16f;
                    builder.Append($"{FormatPositionDescriptor(tileX, tileY, relPixelX, relPixelY, distance, reachable)}; ");
                }

                if (tiles.Count > closestTiles.Count)
                {
                    builder.Append($"...+{tiles.Count - closestTiles.Count} more");
                }

                if (reachableCount > 0)
                {
                    builder.Append($" ({reachableCount} reachable)");
                }

                builder.AppendLine();
            }

            if (tileGroups.Count > 15)
            {
                builder.AppendLine($"...and {tileGroups.Count - 15} more tile types");
            }

            return builder.ToString();
        }

        private bool IsResourceTile(string tileName)
        {
            // Identify valuable resource tiles
            return tileName.Contains("Ore") ||
                   tileName.Contains("Tree") ||
                   tileName.Contains("Gem") ||
                   tileName.Contains("Crystal") ||
                   tileName.Contains("Wood") ||
                   tileName.Contains("Gold") ||
                   tileName.Contains("Silver") ||
                   tileName.Contains("Copper") ||
                   tileName.Contains("Iron") ||
                   tileName.Contains("Platinum") ||
                   tileName.Contains("Tungsten") ||
                   tileName.Contains("Lead") ||
                   tileName.Contains("Tin");
        }

        private string GetDirectionString(int relX, int relY)
        {
            var directions = new List<string>();

            if (relX > 0) directions.Add($"{relX}→");
            else if (relX < 0) directions.Add($"{-relX}←");

            if (relY > 0) directions.Add($"{relY}↓");
            else if (relY < 0) directions.Add($"{-relY}↑");

            return directions.Count > 0 ? string.Join(",", directions) : "here";
        }

        private string FormatPositionDescriptor(int tileX, int tileY, float relPixelX, float relPixelY, float distance, bool reachable)
        {
            var relTileX = (int)Math.Round(relPixelX / 16f);
            var relTileY = (int)Math.Round(relPixelY / 16f);
            var direction = GetDirectionString(relTileX, relTileY);
            var pixelX = tileX * 16 + 8;
            var pixelY = tileY * 16 + 8;
            var reachStr = reachable ? "REACHABLE" : $"{distance:F0}px";
            return $"tile({tileX},{tileY}) pixels({pixelX},{pixelY}) Δtile({relTileX},{relTileY}) Δpx({relPixelX:F0},{relPixelY:F0}) dir[{direction}] [{reachStr}]";
        }

        private bool DetectHellevatorCommand(string? command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            var text = command.ToLowerInvariant();
            string[] keywords =
            [
                "hellevator",
                "hellavator",
                "dig straight down",
                "dig straight",
                "dig down",
                "vertical shaft",
                "hole to hell"
            ];

            return keywords.Any(text.Contains);
        }

        private void EnsureHellevatorColumnInitialized(int tileX)
        {
            if (!_hellevatorMode || _hellevatorColumnLeft.HasValue)
            {
                return;
            }

            var column = tileX % 2 == 0 ? tileX : tileX - 1;
            _hellevatorColumnLeft = column;
            _hellevatorCenterPixelX = column * 16f + 16f;
        }

        private Point ClampToHellevatorColumn(Point tile)
        {
            if (!_hellevatorMode || !_hellevatorColumnLeft.HasValue)
            {
                return tile;
            }

            int left = _hellevatorColumnLeft.Value;
            int clampedX = Math.Clamp(tile.X, left, left + 1);
            return new Point(clampedX, tile.Y);
        }

        private bool EnsureHellevatorCenter()
        {
            if (!_hellevatorMode || !_hellevatorCenterPixelX.HasValue)
            {
                return true;
            }

            float centerX = _hellevatorCenterPixelX.Value;
            float delta = centerX - NPC.Center.X;
            if (Math.Abs(delta) <= 6f)
            {
                MovementHelper.ApplyFriction(NPC, 0.8f);
                return true;
            }

            float adjust = MathHelper.Clamp(delta / 18f, -2.2f, 2.2f);
            NPC.velocity.X = adjust;
            UpdateStatus("Centering in hellevator shaft...");
            return false;
        }

        private void ClearHellevatorState()
        {
            _hellevatorMode = false;
            _hellevatorColumnLeft = null;
            _hellevatorCenterPixelX = null;
        }

        private void ClearTargetLock()
        {
            if (_lockedMineTarget.HasValue && TerrarAI_Config.Get().EnableVerboseLogging)
            {
                Mod.Logger.Info($"[Target Lock] Cleared lock on {_lockReason ?? "resource"} at tile({_lockedMineTarget.Value.X},{_lockedMineTarget.Value.Y})");
            }

            _lockedMineTarget = null;
            _lockReason = null;
            _isLockedTargetTree = false;
        }

        /// <summary>
        /// Checks if the locked target should remain locked or be auto-cleared.
        /// For trees, only clears when entire tree is gone.
        /// For single tiles, clears when that tile is gone.
        /// </summary>
        private bool ShouldMaintainLock()
        {
            if (!_lockedMineTarget.HasValue)
            {
                return false;
            }

            if (_isLockedTargetTree)
            {
                // For trees, check if the tree still exists
                bool treeExists = TreeHelper.DoesTreeExist(_lockedMineTarget.Value);

                if (!treeExists && TerrarAI_Config.Get().EnableVerboseLogging)
                {
                    Mod.Logger.Info($"[Target Lock] Tree at base tile({_lockedMineTarget.Value.X},{_lockedMineTarget.Value.Y}) fully chopped - auto-clearing lock");
                }

                return treeExists;
            }
            else
            {
                // For single-tile resources, check if tile still exists
                var tile = Framing.GetTileSafely(_lockedMineTarget.Value.X, _lockedMineTarget.Value.Y);
                bool tileExists = tile.HasTile;

                if (!tileExists && TerrarAI_Config.Get().EnableVerboseLogging)
                {
                    Mod.Logger.Info($"[Target Lock] Resource tile({_lockedMineTarget.Value.X},{_lockedMineTarget.Value.Y}) destroyed - auto-clearing lock");
                }

                return tileExists;
            }
        }

        private void CollectNearbyItems()
        {
            if (_commander == null)
                return;

            const float collectionRadius = 64f; // ~4 tiles

            int itemsCollected = 0;
            var collectedItems = new List<string>();

            for (int i = 0; i < Main.maxItems; i++)
            {
                Item item = Main.item[i];
                if (item == null || !item.active || item.IsAir)
                    continue;

                float distance = Vector2.Distance(NPC.Center, item.position);
                if (distance <= collectionRadius)
                {
                    // Record what we're collecting
                    collectedItems.Add($"{item.Name} x{item.stack}");

                    // Add item to commander's inventory
                    _commander.GetItem(_commander.whoAmI, item, GetItemSettings.LootAllSettings);

                    // Remove item from world
                    item.active = false;

                    // Sync in multiplayer
                    if (Main.netMode == NetmodeID.Server)
                    {
                        NetMessage.SendData(MessageID.SyncItem, -1, -1, null, i);
                    }

                    itemsCollected++;
                }
            }

            // Log collection if verbose logging enabled
            if (itemsCollected > 0 && TerrarAI_Config.Get().EnableVerboseLogging)
            {
                var itemsList = string.Join(", ", collectedItems);
                TerrarAI.Instance.Logger.Info($"[Agent {NPC.whoAmI}] Auto-collected {itemsCollected} items: {itemsList}");
            }
        }

        private void ScheduleAutoCollectBurst()
        {
            _autoCollectTicksRemaining = Math.Max(_autoCollectTicksRemaining, 120);
        }

        private void TickAutoCollect()
        {
            if (_autoCollectTicksRemaining <= 0)
            {
                return;
            }

            _autoCollectTicksRemaining--;
            CollectNearbyItems();
        }

        private string DescribeNearbyResources()
        {
            const int scanRadius = 50; // Scan 101x101 grid for resources (full screen + beyond)
            const float maxReach = 80f; // 5 tiles = standard player reach

            var agentTileX = (int)(NPC.Center.X / 16f);
            var agentTileY = (int)(NPC.Center.Y / 16f);

            var resources = new List<(string name, int tileX, int tileY, float distance, bool reachable, int requiredPower)>();

            for (int y = -scanRadius; y <= scanRadius; y++)
            {
                for (int x = -scanRadius; x <= scanRadius; x++)
                {
                    var checkX = agentTileX + x;
                    var checkY = agentTileY + y;
                    var tile = Framing.GetTileSafely(checkX, checkY);

                    if (!tile.HasTile)
                    {
                        continue;
                    }

                    var tileName = TileID.Search.GetName(tile.TileType);

                    // Only include resource tiles
                    if (!IsResourceTile(tileName))
                    {
                        continue;
                    }

                    // Calculate distance
                    var tileCenterX = checkX * 16f + 8f;
                    var tileCenterY = checkY * 16f + 8f;
                    var distance = Vector2.Distance(NPC.Center, new Vector2(tileCenterX, tileCenterY));
                    var reachable = distance <= maxReach;

                    // Get required tool power
                    var requiredPower = ToolSelector.GetTileStrength(tile.TileType);

                    resources.Add((tileName, checkX, checkY, distance, reachable, requiredPower));
                }
            }

            if (resources.Count == 0)
            {
                return "No resources found in nearby area";
            }

            // Build output
            var builder = new StringBuilder();
            builder.AppendLine("NEARBY RESOURCES:");

            // Group by resource type
            var grouped = resources
                .GroupBy(r => r.name)
                .OrderBy(g => !g.Any(r => r.reachable))  // Reachable resources first
                .ThenBy(g => g.Min(r => r.distance));    // Closest first

            foreach (var group in grouped.Take(10))  // Limit to top 10 resource types
            {
                var resourceName = group.Key;
                var closest = group.OrderBy(r => r.distance).Take(3).ToList();
                var reachableCount = group.Count(r => r.reachable);

                builder.Append($"- {resourceName}: ");

                foreach (var (name, tileX, tileY, distance, reachable, requiredPower) in closest)
                {
                    var relX = tileX - agentTileX;
                    var relY = tileY - agentTileY;
                    var direction = GetDirectionString(relX, relY);

                    // Check if player has sufficient tool
                    string toolStatus = "";
                    if (requiredPower > 0 && _commander != null)
                    {
                        var pickaxe = ToolSelector.FindBestTool(_commander, ToolType.Pickaxe);
                        var tileType = Framing.GetTileSafely(tileX, tileY).TileType;
                        var canMine = pickaxe != null && ToolSelector.CanMineTile(pickaxe, tileType);

                        if (canMine)
                        {
                            toolStatus = reachable ? " ✓CAN_MINE" : " (have tool, move closer)";
                        }
                        else
                        {
                            toolStatus = $" ✗NEED_{requiredPower}%_PICKAXE";
                        }
                    }

                    var relPixelX = relX * 16f;
                    var relPixelY = relY * 16f;
                    builder.Append($"{FormatPositionDescriptor(tileX, tileY, relPixelX, relPixelY, distance, reachable)}{toolStatus}; ");
                }

                if (group.Count() > closest.Count)
                {
                    builder.Append($"...+{group.Count() - closest.Count} more");
                }

                builder.AppendLine($" (total: {group.Count()}, {reachableCount} reachable)");
            }

            return builder.ToString();
        }

        private string DescribeNearbyPlayers()
        {
            var agentTileX = (int)(NPC.Center.X / 16f);
            var agentTileY = (int)(NPC.Center.Y / 16f);

            var closePlayers = Main.player
                .Where(p => p?.active == true && !p.dead)
                .Select(p => new
                {
                    Player = p,
                    TileX = (int)(p.Center.X / 16f),
                    TileY = (int)(p.Center.Y / 16f),
                    Distance = Vector2.Distance(p.Center, NPC.Center),
                    RelPixelX = p.Center.X - NPC.Center.X,
                    RelPixelY = p.Center.Y - NPC.Center.Y
                })
                .Where(info => info.Distance <= 1000f)
                .OrderBy(info => info.Distance)
                .Take(5)
                .ToList();

            if (closePlayers.Count == 0)
            {
                return "No nearby players";
            }

            var builder = new StringBuilder();
            foreach (var info in closePlayers)
            {
                var relTileX = info.TileX - agentTileX;
                var relTileY = info.TileY - agentTileY;
                builder.Append($"- {info.Player.name}: {FormatPositionDescriptor(info.TileX, info.TileY, info.RelPixelX, info.RelPixelY, info.Distance, info.Distance <= 80f)}; ");
            }

            return builder.ToString();
        }

        private string DescribeInventory()
        {
            if (TerrarAI_Config.Get().EnableCreativeMode)
            {
                return "INVENTORY:\nCreative mode enabled – agent has unlimited tools and building materials (commander inventory ignored).";
            }

            if (_commander == null)
            {
                return "No inventory available (no commander)";
            }

            var builder = new StringBuilder();
            builder.AppendLine("INVENTORY:");

            // Count placeable blocks
            var placeableBlocks = new Dictionary<string, int>
            {
                ["Dirt"] = 0,
                ["Stone"] = 0,
                ["Wood"] = 0
            };

            foreach (var item in _commander.inventory)
            {
                if (item == null || item.IsAir)
                {
                    continue;
                }

                // Check if item can create specific tiles
                if (item.createTile == TileID.Dirt)
                {
                    placeableBlocks["Dirt"] += item.stack;
                }
                else if (item.createTile == TileID.Stone)
                {
                    placeableBlocks["Stone"] += item.stack;
                }
                else if (item.createTile == TileID.WoodBlock)
                {
                    placeableBlocks["Wood"] += item.stack;
                }
            }

            // Show placeable blocks
            builder.Append("Placeable blocks: ");
            var blockList = placeableBlocks
                .Where(kvp => kvp.Value > 0)
                .Select(kvp => $"{kvp.Key} ({kvp.Value})")
                .ToList();

            if (blockList.Any())
            {
                builder.AppendLine(string.Join(", ", blockList));
            }
            else
            {
                builder.AppendLine("None available");
            }

            // Count valuable resources collected
            var resources = new Dictionary<string, int>();
            foreach (var item in _commander.inventory)
            {
                if (item == null || item.IsAir)
                {
                    continue;
                }

                // Check for ores and valuable items
                if (item.Name.Contains("Ore") ||
                    item.Name.Contains("Bar") ||
                    item.Name.Contains("Gem") ||
                    item.Name.Contains("Crystal"))
                {
                    if (!resources.ContainsKey(item.Name))
                    {
                        resources[item.Name] = 0;
                    }
                    resources[item.Name] += item.stack;
                }
            }

            if (resources.Any())
            {
                builder.Append("Resources collected: ");
                var resourceList = resources
                    .OrderByDescending(kvp => kvp.Value)
                    .Take(5)
                    .Select(kvp => $"{kvp.Key} ({kvp.Value})")
                    .ToList();
                builder.AppendLine(string.Join(", ", resourceList));
            }
            else
            {
                builder.AppendLine("Resources collected: None");
            }

            return builder.ToString();
        }

        private void UpdateStatus(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                message = "Idle";
            }

            if (_statusMessage == message)
            {
                return;
            }

            _statusMessage = message;
            if (Main.netMode == NetmodeID.Server)
            {
                NPC.netUpdate = true;
            }
        }
    }
}
