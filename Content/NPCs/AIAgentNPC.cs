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
        private const float MaxLeashDistance = 600f;

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

        public override void SetDefaults()
        {
            // Use player-like dimensions
            NPC.width = 20;
            NPC.height = 42;
            NPC.friendly = true;
            NPC.dontTakeDamage = true;
            NPC.dontTakeDamageFromHostiles = true;
            NPC.lifeMax = 250;
            NPC.aiStyle = NPCAIStyleID.Fighter;
            AIType = NPCID.UndeadViking;
            AnimationType = NPCID.UndeadViking;
            NPC.noGravity = false;
            NPC.noTileCollide = false;
            NPC.knockBackResist = 0f;
            NPC.damage = 0;
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

            // Set male/female
            _appearanceClone.Male = sourcePlayer.Male;
        }

        // Public method to set player appearance (can be called externally)
        public void SetPlayerAppearance(Player player)
        {
            ClonePlayerAppearance(player);
        }

        public override bool PreAI()
        {
            if (State == AgentState.Idle)
            {
                NPC.aiStyle = NPCAIStyleID.Fighter;
                AssignFollowTarget();
            }
            else
            {
                NPC.aiStyle = -1;
            }

            return true;
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

            EnforceLeash();
            UpdateFacing();
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

        private void AssignFollowTarget()
        {
            if (!ServerAuthority.IsServer)
            {
                return;
            }

            var target = FindFollowTarget();
            if (target != null)
            {
                NPC.target = target.whoAmI;
            }
        }

        private Player? FindFollowTarget()
        {
            if (_commander?.active == true && !_commander.dead)
            {
                return _commander;
            }

            NPC.TargetClosest(false);
            var candidate = Main.player[NPC.target];
            if (candidate.active && !candidate.dead)
            {
                return candidate;
            }

            return null;
        }


        private void EnforceLeash()
        {
            if (!ServerAuthority.IsServer)
            {
                return;
            }

            var target = FindFollowTarget();
            if (target == null)
            {
                return;
            }

            var maxDistanceSquared = MaxLeashDistance * MaxLeashDistance;
            if (Vector2.DistanceSquared(NPC.Center, target.Center) <= maxDistanceSquared)
            {
                return;
            }

            NPC.position = target.Center - NPC.Size * 0.5f;
            NPC.velocity = Vector2.Zero;
            NPC.netUpdate = true;
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

            var context = AgentActionContext.From(NPC, _commander);
            var result = _currentAction.Execute(context);

            switch (result.Status)
            {
                case AgentActionStatus.Pending:
                    if (!string.IsNullOrWhiteSpace(result.Message))
                    {
                        UpdateStatus(result.Message);
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

            sb.AppendLine("You are an autonomous AI agent inside Terraria responsible for carrying out short sequences of actions.");
            sb.AppendLine("AVAILABLE ACTIONS:");
            sb.AppendLine("- move(x, y): Move toward absolute pixel coordinates.");
            sb.AppendLine("- say(text): Broadcast a chat message.");
            sb.AppendLine("- mine(tileX, tileY): Break the tile at integer grid coordinates.");
            sb.AppendLine("- place(tileX, tileY, blockType): Place a tile (1=dirt, 2=stone, 9=wood).");
            sb.AppendLine();
            sb.AppendLine("CURRENT STATE:");
            sb.AppendLine($"- Position: pixels ({pixelPos.X:F0}, {pixelPos.Y:F0}) | tiles ({tilePos.X:F0}, {tilePos.Y:F0})");
            sb.AppendLine($"- Health: {NPC.life}/{NPC.lifeMax}");
            sb.AppendLine($"- Nearby tiles: {DescribeNearbyTiles()}");
            sb.AppendLine($"- Nearby players: {DescribeNearbyPlayers()}");
            sb.AppendLine();
            sb.AppendLine("IMPORTANT RULES:");
            sb.AppendLine("- Tile coordinates = pixel coordinates / 16.");
            sb.AppendLine("- Respond ONLY with valid JSON in the format {\"actions\": [{\"type\": \"move\", \"params\": {...}}]}.");
            sb.AppendLine("- Plan 1-5 actions at a time. Keep them deterministic and achievable.");
            sb.AppendLine("- Never reference capabilities other than move/say/mine/place.");

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
            var builder = new StringBuilder();
            var tileX = (int)(NPC.Center.X / 16f);
            var tileY = (int)(NPC.Center.Y / 16f);

            for (int y = -1; y <= 1; y++)
            {
                for (int x = -1; x <= 1; x++)
                {
                    var checkX = tileX + x;
                    var checkY = tileY + y;
                    var tile = Framing.GetTileSafely(checkX, checkY);
                    var tileName = tile.HasTile ? TileID.Search.GetName(tile.TileType) : "Air";
                    builder.Append($"({x:+#;-#;0},{y:+#;-#;0})={tileName}; ");
                }
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
