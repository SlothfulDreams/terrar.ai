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
    [AutoloadHead]
    public sealed class AIAgentNPC : ModNPC
    {
        public override string Texture => "Terraria/Images/NPC_17";

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

        public override void SetStaticDefaults()
        {
            Main.npcFrameCount[Type] = 1;
        }

        public override void SetDefaults()
        {
            NPC.width = 18;
            NPC.height = 40;
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
        }

        public override void AI()
        {
            if (!ServerAuthority.IsServer)
            {
                // Client copies state via net sync; keep visuals simple.
                NPC.velocity *= IdleFriction;
                return;
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
                if (Main.netMode != NetmodeID.Server)
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

        private void TickPlanning()
        {
            if (_plannerTask == null)
            {
                BeginPlanning();
                return;
            }

            if (!_plannerTask.IsCompleted)
            {
                UpdateStatus("Planning with xAI...");
                ApplyIdlePhysics();
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
                UpdateStatus("Executing plan...");
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

            _plannerTask = Task.Run(async () =>
            {
                try
                {
                    var systemPrompt = BuildSystemPrompt();
                    var userPrompt = BuildUserPrompt(_currentCommand!, _replanContext);
                    return await TerrarAI.RequireClient().SendChatCompletionAsync(systemPrompt, userPrompt, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"xAI request failed: {ex.Message}", ex);
                }
            });
        }

        private void HandlePlannerFailure(string error)
        {
            _plannerTask = null;
            _lastPlannerError = error;
            State = AgentState.Completed;
            UpdateStatus($"Planner error: {error}");
            Mod.Logger.Warn($"TerrarAI planner failed: {error}");
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
