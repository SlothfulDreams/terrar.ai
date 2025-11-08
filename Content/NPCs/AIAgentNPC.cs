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
        private const float MaxLeashDistance = 600f;

        // Planning timeout tracking
        private long _planningStartTick;
        private long _maxPlanningTicks;

        // Streaming thoughts
        private readonly ConcurrentQueue<string> _thoughtChunks = new();
        private readonly StringBuilder _accumulatedResponse = new();

        // Batch network updates
        private int _ticksSinceLastNetUpdate;
        private const int MinTicksBetweenNetUpdates = 15;

        // Rendering
        private readonly AIAgentRenderer _renderer = new();

        public override void SetStaticDefaults()
        {
            Main.npcFrameCount[Type] = 1;
        }

        public override string Texture => "Terraria/Images/NPC_0";

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

            if (source is EntitySource_Parent parent && parent.Entity is Player player)
            {
                _renderer.ClonePlayerAppearance(player);
            }
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
                NPC.velocity *= IdleFriction;
                UpdateFacing();
                return;
            }

            switch (State)
            {
                case AgentState.Idle:
                    break;
                case AgentState.Planning:
                case AgentState.Replanning:
                    TickPlanning();
                    break;
                case AgentState.Executing:
                    TickExecuting();
                    break;
                case AgentState.Completed:
                    NPC.velocity.X *= IdleFriction;
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
            if (_renderer.HasAppearance)
            {
                _renderer.DrawAsPlayer(NPC, spriteBatch, screenPos, drawColor);
                return false;
            }

            return true;
        }

        public override void PostDraw(SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
        {
            _renderer.DrawStatusText(NPC, State, _statusMessage, spriteBatch, screenPos);
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
                
                // State changes always force network update
                NPC.netUpdate = true;
                _ticksSinceLastNetUpdate = 0;
            }
        }

        private int _stateBacking;

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

            while (_thoughtChunks.TryDequeue(out var chunk))
            {
                var config = TerrarAI_Config.Get();
                if (config.ShowAgentThoughts)
                {
                    SendChatMessage(chunk, Color.LightBlue);
                }
            }

            if (!_plannerTask.IsCompleted)
            {
                long elapsedTicks = Main.GameUpdateCount - _planningStartTick;
                if (elapsedTicks > _maxPlanningTicks)
                {
                    var config = TerrarAI_Config.Get();
                    HandlePlannerFailure($"Planning timed out after {config.MaxPlanningSeconds} seconds. The AI did not respond in time.");
                    return;
                }

                long elapsedSeconds = elapsedTicks / 60;
                UpdateStatus($"Planning with xAI... ({elapsedSeconds}s)");
                NPC.velocity.X *= IdleFriction;
                return;
            }

            if (_plannerTask.IsFaulted)
            {
                var error = _plannerTask.Exception?.GetBaseException().Message ?? "Unknown planning error.";
                HandlePlannerFailure(error);
                return;
            }

            var response = _plannerTask.Result;
            _plannerTask = null;

            try
            {
                var actions = ActionParser.Parse(response, NPC, _validator, _commander);
                QueueActions(actions);
                State = AgentState.Executing;
                UpdateStatus("Executing plan...", forceNetUpdate: true);
                SendChatMessage($"Planning complete! Executing {actions.Count} action(s).", Color.LightGreen);
            }
            catch (ActionParserException ex)
            {
                HandlePlannerFailure($"Parser error: {ex.Message}");
            }
            catch (Exception ex)
            {
                HandlePlannerFailure($"Unexpected error: {ex.Message}");
            }
        }

        private void TickExecuting()
        {
            if (_currentAction == null)
            {
                if (_actionQueue.Count == 0)
                {
                    State = AgentState.Completed;
                    UpdateStatus("Plan complete.", forceNetUpdate: true);
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
                        UpdateStatus("Plan complete.", forceNetUpdate: true);
                    }
                    break;
                case AgentActionStatus.Failure:
                    var failureReason = result.Message ?? "Action failed.";
                    var actionName = _currentAction.Name;
                    _currentAction.Reset();
                    _currentAction = null;

                    // Try partial replanning: skip failed action and continue if failure is recoverable
                    if (IsRecoverableFailure(failureReason) && _actionQueue.Count > 0)
                    {
                        SendChatMessage($"{actionName} failed: {failureReason}. Skipping and continuing...", Color.Yellow);
                        UpdateStatus($"Skipped failed action, continuing...");
                        break;
                    }

                    // Critical failure: full replan
                    _actionQueue.Clear();
                    _replanContext = $"{actionName} failed: {failureReason}";
                    State = AgentState.Replanning;
                    UpdateStatus("Replanning due to failure...", forceNetUpdate: true);
                    SendChatMessage($"Critical failure, replanning...", Color.OrangeRed);
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

        private bool IsRecoverableFailure(string failureReason)
        {
            if (string.IsNullOrWhiteSpace(failureReason))
            {
                return false;
            }

            var reason = failureReason.ToLowerInvariant();

            // Recoverable: minor issues that don't affect subsequent actions
            if (reason.Contains("already") || 
                reason.Contains("not found") ||
                reason.Contains("no tile") ||
                reason.Contains("tile already") ||
                reason.Contains("cannot place"))
            {
                return true;
            }

            // Critical: issues that likely affect the entire plan
            if (reason.Contains("out of range") ||
                reason.Contains("too far") ||
                reason.Contains("unreachable") ||
                reason.Contains("blocked") ||
                reason.Contains("timeout"))
            {
                return false;
            }

            // Default: treat unknown failures as recoverable to avoid excessive replanning
            return true;
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
                var systemPrompt = BuildSystemPrompt();
                var userPrompt = BuildUserPrompt(_currentCommand!, _replanContext);

                await foreach (var chunk in TerrarAI.RequireClient().SendChatCompletionStreamAsync(systemPrompt, userPrompt, CancellationToken.None).ConfigureAwait(false))
                {
                    _thoughtChunks.Enqueue(chunk);
                    _accumulatedResponse.Append(chunk);
                }

                return _accumulatedResponse.ToString();
            }
            catch (Exception ex)
            {
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

        private void UpdateStatus(string message, bool forceNetUpdate = false)
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
                _ticksSinceLastNetUpdate++;
                if (forceNetUpdate || _ticksSinceLastNetUpdate >= MinTicksBetweenNetUpdates)
                {
                    NPC.netUpdate = true;
                    _ticksSinceLastNetUpdate = 0;
                }
            }
        }
    }
}
