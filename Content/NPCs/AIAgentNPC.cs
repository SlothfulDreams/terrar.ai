using System;
using System.Collections.Concurrent;
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
        private string _statusMessage = "Idle";
        private string? _lastPlannerError;
        private Player? _commander;

        private const float IdleFriction = 0.85f;

        // Planning timeout tracking
        private long _planningStartTick;
        private long _maxPlanningTicks;

        // Streaming thoughts
        private readonly ConcurrentQueue<string> _thoughtChunks = new();
        private readonly StringBuilder _accumulatedResponse = new();

        // Player appearance clone (stored for rendering as player)
        private Player? _appearanceClone;

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
            NPC.aiStyle = -1;
            NPC.noGravity = false;
            NPC.noTileCollide = false;
            NPC.knockBackResist = 0f;
        }

        public override void OnSpawn(IEntitySource source)
        {
            State = AgentState.Idle;
            _statusMessage = "Awaiting command";

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
            for (int i = 0; i < sourcePlayer.armor.Length; i++)
            {
                _appearanceClone.armor[i] = sourcePlayer.armor[i].Clone();
            }
            for (int i = 0; i < sourcePlayer.dye.Length; i++)
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
                NPC.velocity *= IdleFriction;
                UpdateFacing();
                return;
            }

            // Diagnostic logging - only log when Planning to reduce spam
            if (State == AgentState.Planning)
            {
                Mod.Logger.Info($"[AI] Called. State={State}, _stateBacking={_stateBacking}, NPC.ai[0]={NPC.ai[0]}, IsServer={ServerAuthority.IsServer}");
            }

            switch (State)
            {
                case AgentState.Idle:
                    ApplyIdlePhysics();
                    break;
                case AgentState.Planning:
                    TickPlanning();
                    break;
                case AgentState.Executing:
                    TickExecuting();
                    break;
                case AgentState.Replanning:
                    TickReplanning();
                    break;
                case AgentState.Completed:
                    ApplyIdlePhysics();
                    if (_actionQueue.Count == 0 && _currentAction == null && _plannerTask == null)
                    {
                        State = AgentState.Idle;
                        UpdateStatus("Idle");
                    }
                    break;
            }

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
            _lastPlannerError = null;

            _actionQueue.Clear();
            _currentAction = null;

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
            NPC.velocity.X *= IdleFriction;
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

            // Process streaming thought chunks
            while (_thoughtChunks.TryDequeue(out var chunk))
            {
                var config = TerrarAI_Config.Get();
                if (config.ShowAgentThoughts)
                {
                    SendChatMessage(chunk, Color.LightBlue);
                }
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
            if (_currentAction == null)
            {
                if (_actionQueue.Count == 0)
                {
                    State = AgentState.Completed;
                    UpdateStatus("Plan complete.");
                    return;
                }

                _currentAction = _actionQueue.Dequeue();
                _currentAction.Reset();
                UpdateStatus($"Executing {_currentAction.Name}...");
            }

            // Range validation: Check if agent is close enough to target
            // Don't check range for MoveAction - it handles its own distance logic
            if (_currentAction is not MoveAction)
            {
                var (distance, targetPos) = GetTargetInfo(_currentAction);
                float requiredRange = _currentAction.GetRequiredRange();

                if (requiredRange > 0f && distance > requiredRange)
                {
                    UpdateStatus($"Approaching target... ({distance:F0}px away)");

                    if (targetPos.HasValue)
                    {
                        // Create temporary move action to approach target
                        var moveAction = new MoveAction(targetPos.Value);
                        var moveContext = AgentActionContext.From(NPC, _commander);
                        moveAction.Execute(moveContext);  // Execute movement this tick
                    }

                    return;  // Continue approaching next tick
                }
            }

            var context = AgentActionContext.From(NPC, _commander);
            var result = _currentAction.Execute(context);

            switch (result.Status)
            {
                case AgentActionStatus.Pending:
                    if (!string.IsNullOrWhiteSpace(result.Message))
                    {
                        UpdateStatus(result.Message);
                    }

                    // Apply STRONG friction to slow down agent when executing stationary actions
                    // Note: MineAction also applies its own velocity dampening for stability
                    if (_currentAction is not MoveAction)
                    {
                        NPC.velocity.X *= 0.5f;  // Strong friction to prevent drifting (was 0.92f - too weak)
                        NPC.velocity.Y *= 0.85f; // Keep some Y velocity for gravity
                    }
                    break;
                case AgentActionStatus.Success:
                    if (!string.IsNullOrWhiteSpace(result.Message))
                    {
                        UpdateStatus(result.Message);
                    }

                    _currentAction.Reset();
                    _currentAction = null;

                    if (_actionQueue.Count == 0)
                    {
                        State = AgentState.Completed;
                        UpdateStatus("Plan complete.");
                    }
                    break;
                case AgentActionStatus.Failure:
                    var failureReason = result.Message ?? "Action failed.";
                    _currentAction.Reset();
                    _currentAction = null;
                    _actionQueue.Clear();

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

            // Clear previous streaming data
            while (_thoughtChunks.TryDequeue(out _)) { }
            _accumulatedResponse.Clear();

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

                // Stream the response and accumulate it
                await foreach (var chunk in TerrarAI.RequireClient().SendChatCompletionStreamAsync(systemPrompt, userPrompt, CancellationToken.None).ConfigureAwait(false))
                {
                    _thoughtChunks.Enqueue(chunk);
                    _accumulatedResponse.Append(chunk);
                }

                var result = _accumulatedResponse.ToString();
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

            sb.AppendLine("You are an autonomous AI agent inside Terraria responsible for carrying out short sequences of actions.");

            // Directional context
            sb.AppendLine();
            sb.AppendLine("COORDINATE SYSTEM:");
            sb.AppendLine($"- You are facing: {(NPC.direction > 0 ? "RIGHT" : "LEFT")} (direction={NPC.direction})");
            sb.AppendLine("- X axis: increases rightward (→), decreases leftward (←)");
            sb.AppendLine("- Y axis: increases downward (↓), decreases upward (↑)");
            sb.AppendLine("- Tile coordinates = pixel coordinates ÷ 16 (truncate decimals)");
            sb.AppendLine("- Pixel coordinates = tile coordinates × 16 + 8 (centers on tile)");
            sb.AppendLine($"- Your position: tile({agentTileX},{agentTileY}) = pixels({pixelPos.X:F0},{pixelPos.Y:F0})");
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
            sb.AppendLine("- move(x, y): Move toward absolute pixel coordinates. Agent will automatically approach targets before mining/placing.");
            sb.AppendLine("- say(text): Broadcast a chat message to all players.");
            sb.AppendLine("- mine(tileX, tileY): Mine/chop the tile at absolute grid coordinates. Auto-selects correct tool (axe for trees, pickaxe for stone/ore).");
            sb.AppendLine("- place(tileX, tileY, blockType): Place a tile at absolute grid coordinates (1=dirt, 2=stone, 9=wood).");
            sb.AppendLine("- All actions use ABSOLUTE coordinates (not relative to your position).");
            sb.AppendLine("- Agent reach: 5 tiles (80 pixels). Actions fail if target is too far.");

            sb.AppendLine();
            sb.AppendLine("CURRENT STATE:");
            sb.AppendLine($"- Position: tile({agentTileX},{agentTileY}) = pixels({pixelPos.X:F0},{pixelPos.Y:F0})");
            sb.AppendLine($"- Facing: {(NPC.direction > 0 ? "RIGHT (→)" : "LEFT (←)")}");
            sb.AppendLine($"- Health: {NPC.life}/{NPC.lifeMax}");
            sb.AppendLine();
            sb.AppendLine(DescribeInventory());
            sb.AppendLine();
            sb.AppendLine(DescribeNearbyResources());
            sb.AppendLine();
            sb.AppendLine(DescribeNearbyTiles());
            sb.AppendLine();
            sb.AppendLine($"Nearby players: {DescribeNearbyPlayers()}");

            sb.AppendLine();
            sb.AppendLine("IMPORTANT RULES:");
            sb.AppendLine("- Use ABSOLUTE tile/pixel coordinates (not offsets like +5 or -3)");
            sb.AppendLine("- Convert tile to pixel: pixel = tile × 16 + 8");
            sb.AppendLine("- Convert pixel to tile: tile = pixel ÷ 16 (truncate)");
            sb.AppendLine("- Check NEARBY RESOURCES section for available resources with coordinates");
            sb.AppendLine("- Check tool requirements - mining ore/stone needs appropriate pickaxe power");
            sb.AppendLine("- Plan movement BEFORE mining/placing if target is not marked REACHABLE");
            sb.AppendLine("- Respond ONLY with valid JSON - no explanations or markdown");
            sb.AppendLine("- Plan 1-5 actions at a time. Keep them achievable.");

            sb.AppendLine();
            sb.AppendLine("CONCRETE EXAMPLES:");
            sb.AppendLine($"Example 1: Agent at tile({agentTileX},{agentTileY}), Copper ore at tile({agentTileX + 5},{agentTileY})");
            sb.AppendLine($"  Calculate pixel position: ({agentTileX + 5}) × 16 + 8 = {(agentTileX + 5) * 16 + 8}, {agentTileY} × 16 + 8 = {agentTileY * 16 + 8}");
            sb.AppendLine($"  Actions: [");
            sb.AppendLine($"    {{\"type\":\"move\",\"params\":{{\"x\":{(agentTileX + 5) * 16 + 8},\"y\":{agentTileY * 16 + 8}}}}},");
            sb.AppendLine($"    {{\"type\":\"mine\",\"params\":{{\"tileX\":{agentTileX + 5},\"tileY\":{agentTileY}}}}}");
            sb.AppendLine($"  ]");
            sb.AppendLine();
            sb.AppendLine($"Example 2: \"Say hello\" (no coordinates needed)");
            sb.AppendLine($"  Actions: [{{\"type\":\"say\",\"params\":{{\"text\":\"Hello!\"}}}}]");
            sb.AppendLine();
            sb.AppendLine($"Example 3: \"Build a dirt platform\" at tile({agentTileX + 2},{agentTileY - 1})");
            sb.AppendLine($"  Actions: [");
            sb.AppendLine($"    {{\"type\":\"place\",\"params\":{{\"tileX\":{agentTileX + 2},\"tileY\":{agentTileY - 1},\"blockType\":1}}}},");
            sb.AppendLine($"    {{\"type\":\"place\",\"params\":{{\"tileX\":{agentTileX + 3},\"tileY\":{agentTileY - 1},\"blockType\":1}}}},");
            sb.AppendLine($"    {{\"type\":\"place\",\"params\":{{\"tileX\":{agentTileX + 4},\"tileY\":{agentTileY - 1},\"blockType\":1}}}}");
            sb.AppendLine($"  ]");

            return sb.ToString();
        }

        private string BuildUserPrompt(string command, string? failureContext)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(failureContext))
            {
                sb.AppendLine("Previous attempt failed:");
                sb.AppendLine(failureContext);
                sb.AppendLine("Generate a new list of actions that avoids this problem.");
                sb.AppendLine();
            }

            sb.AppendLine("Player command:");
            sb.AppendLine(command);
            sb.AppendLine();
            sb.AppendLine("Return JSON only. Example:");
            sb.AppendLine("{\"actions\": [{\"type\": \"move\", \"params\": {\"x\": 1200, \"y\": 600}}]}");

            return sb.ToString();
        }

        private string DescribeNearbyTiles()
        {
            const int scanRadius = 10; // Scan 21x21 grid (10 tiles in each direction)
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
                    var direction = GetDirectionString(relX, relY);
                    var reachableStr = reachable ? "REACHABLE" : $"{distance:F0}px";

                    builder.Append($"tile({tileX},{tileY}) {direction} [{reachableStr}]; ");
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

        private string DescribeNearbyResources()
        {
            const int scanRadius = 15; // Scan 31x31 grid for resources
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

                    var reachStr = reachable ? "REACHABLE" : $"{distance:F0}px";
                    builder.Append($"tile({tileX},{tileY}) {direction} [{reachStr}]{toolStatus}; ");
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
            var players = Main.player.Where(p => p?.active == true && !p.dead);
            var closePlayers = players
                .Select(p => new { Player = p, Distance = Vector2.Distance(p.Center, NPC.Center) })
                .Where(info => info.Distance <= 600f)
                .OrderBy(info => info.Distance)
                .Take(3)
                .Select(info => $"{info.Player.name} ({info.Distance:F0}px)");

            return closePlayers.Any() ? string.Join(", ", closePlayers) : "No nearby players";
        }

        private string DescribeInventory()
        {
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
