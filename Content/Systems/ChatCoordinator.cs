using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using TerrarAI;
using TerrarAI.Content.NPCs;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;

namespace TerrarAI.Content.Systems
{
    public sealed class ChatCoordinator : ModSystem
    {
        private const string RouterSystemPrompt = @"You route Terraria commands for AI agent management and task assignment.
Respond with JSON in one of these formats:

1. Spawn agents: {""action"":""spawn"",""count"":N,""deleteExisting"":true}
2. Task with agent selection: {""action"":""command"",""command"":""<task>"",""agentCount"":N}
3. Task for all agents: {""action"":""command"",""command"":""<task>""}
4. Recall agents: {""action"":""recall""}

Examples:
- ""spawn 8 agents"" → {""action"":""spawn"",""count"":8,""deleteExisting"":true}
- ""create 5 agents"" → {""action"":""spawn"",""count"":5,""deleteExisting"":true}
- ""two agents chop 2 trees"" → {""action"":""command"",""command"":""chop 2 trees"",""agentCount"":2}
- ""one agent mine copper"" → {""action"":""command"",""command"":""mine copper"",""agentCount"":1}
- ""all agents dig down"" → {""action"":""command"",""command"":""dig down""}
- ""dismiss all agents"" → {""action"":""spawn"",""count"":0,""deleteExisting"":true}
- ""recall agents"" → {""action"":""recall""}

The system will select the N closest agents to the player for tasks with agentCount.";

        public override void OnWorldLoad()
        {
            if (ServerAuthority.IsServer)
            {
                SpawnInitialAgents();
            }
        }

        private static void SpawnInitialAgents()
        {
            if (CountAgents() > 0)
            {
                return;
            }

            Vector2 spawnPosition;
            Player? appearanceSource = null;

            if (Main.spawnTileX > 0 && Main.spawnTileY > 0)
            {
                int spawnTileX = Main.spawnTileX;
                int surfaceTileY = Main.spawnTileY;

                for (int y = Main.spawnTileY; y < Main.maxTilesY && y < Main.spawnTileY + 100; y++)
                {
                    var tile = Framing.GetTileSafely(spawnTileX, y);
                    if (tile.HasTile && Main.tileSolid[tile.TileType])
                    {
                        surfaceTileY = y;
                        break;
                    }
                }

                spawnPosition = new Vector2(spawnTileX * 16f + 8f, surfaceTileY * 16f);
            }
            else
            {
                var firstPlayer = GetFirstActivePlayer();
                if (firstPlayer == null)
                {
                    return;
                }
                spawnPosition = firstPlayer.Bottom;
                appearanceSource = firstPlayer;
            }

            if (appearanceSource == null)
            {
                appearanceSource = GetFirstActivePlayer();
            }

            for (int i = 0; i < 3; i++)
            {
                var offsetX = (i - 1) * 32f;
                var agentPosition = spawnPosition + new Vector2(offsetX, -42f);
                var agentName = $"Agent {i + 1}";
                SpawnAgentAtPosition(agentPosition, agentName, new EntitySource_WorldGen(), 73); // Goblin Scout
            }
        }

        private static Player? GetFirstActivePlayer()
        {
            for (int i = 0; i < Main.maxPlayers; i++)
            {
                var player = Main.player[i];
                if (player != null && player.active && !player.dead)
                {
                    return player;
                }
            }
            return null;
        }

        private static void SpawnAgentAtPosition(Vector2 position, string name, IEntitySource source, int npcSpriteId)
        {
            var index = NPC.NewNPC(source, (int)position.X, (int)position.Y, ModContent.NPCType<AIAgentNPC>());
            if (index < 0 || index >= Main.maxNPCs)
            {
                return;
            }

            var npc = Main.npc[index];
            if (npc.ModNPC is not AIAgentNPC agentNpc)
            {
                return;
            }

            npc.direction = 1;
            npc.GivenName = name;
            agentNpc.SetNpcSpriteId(npcSpriteId);
            npc.netUpdate = true;
        }

        internal static void HandleInput(CommandCaller caller, string text)
        {
            if (!ServerAuthority.EnsureServer(message => caller.Reply(message, Color.OrangeRed)))
            {
                return;
            }

            var player = caller.Player;
            if (player == null)
            {
                return;
            }

            // Check for recall command
            if (IsRecallCommand(text))
            {
                RecallAllAgents(player, caller);
                return;
            }

            var playerIndex = player.whoAmI;
            var prompt = BuildUserPrompt(player, text);

            _ = Task.Run(async () =>
            {
                var response = await TerrarAI.RequireClient().SendChatCompletionAsync(RouterSystemPrompt, prompt).ConfigureAwait(false);
                ServerAuthority.QueueMainThread(() =>
                {
                    var livePlayer = playerIndex >= 0 && playerIndex < Main.maxPlayers ? Main.player[playerIndex] : null;
                    if (livePlayer?.active != true)
                    {
                        return;
                    }

                    // Check again for recall in LLM response
                    if (IsRecallCommand(response))
                    {
                        RecallAllAgents(livePlayer, caller);
                        return;
                    }

                    ApplyCoordinatorResponse(caller, text, response);
                });
            });
        }

        internal static bool IsRecallCommand(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var lowerText = text.ToLowerInvariant();
            return lowerText == "recall" ||
                   lowerText.Contains("come back") ||
                   lowerText.Contains("return to me") ||
                   lowerText.Contains("come to me") ||
                   lowerText.Contains("come here") ||
                   lowerText.Contains("teleport to me");
        }

        internal static void RecallAllAgents(Player commander, CommandCaller caller)
        {
            int recalledCount = 0;
            foreach (var npc in EnumerateAgents())
            {
                if (npc.ModNPC is AIAgentNPC agent)
                {
                    agent.RecallToCommander(commander);
                    recalledCount++;
                }
            }

            if (recalledCount > 0)
            {
                caller.Reply($"Recalled {recalledCount} agent(s) to your position.", Color.LightGreen);
            }
            else
            {
                caller.Reply("No agents available to recall.", Color.OrangeRed);
            }
        }

        internal static void RouteTool(CommandCaller caller, string commandText)
        {
            if (!ServerAuthority.EnsureServer(message => caller.Reply(message, Color.OrangeRed)))
            {
                return;
            }

            var player = caller.Player;
            if (player == null)
            {
                return;
            }

            // Always broadcast to all agents simultaneously
            BroadcastToAllAgents(player, commandText, caller);
        }

        private static void BroadcastToAllAgents(Player commander, string commandText, CommandCaller caller)
        {
            int agentCount = 0;
            foreach (var npc in EnumerateAgents())
            {
                if (npc.ModNPC is AIAgentNPC agent)
                {
                    agent.ReceiveCommand(commander, commandText);
                    agentCount++;
                }
            }

            if (agentCount > 0)
            {
                // Single global message instead of per-agent messages
                string message = agentCount == 1
                    ? "Agent received command and is planning..."
                    : $"All {agentCount} agents received command and are planning...";
                caller.Reply(message, Color.LightBlue);
            }
            else
            {
                caller.Reply("No agents available.", Color.OrangeRed);
            }
        }

        private static void ApplyCoordinatorResponse(CommandCaller caller, string originalText, string responseText)
        {
            var coordinator = JsonSerializer.Deserialize<CoordinatorResponse>(responseText);
            if (coordinator == null)
            {
                RouteToNearestAgents(caller, originalText, null);
                return;
            }

            var commandText = string.IsNullOrWhiteSpace(coordinator.Command) ? originalText : coordinator.Command!;

            // Handle different action types
            switch (coordinator.Action?.ToLowerInvariant())
            {
                case "spawn":
                    SpawnAgents(
                        coordinator.Count ?? 1,
                        caller.Player,
                        caller,
                        coordinator.DeleteExisting
                    );
                    break;

                case "recall":
                    RecallAllAgents(caller.Player, caller);
                    break;

                case "command":
                default:
                    RouteToNearestAgents(caller, commandText, coordinator.AgentCount);
                    break;
            }
        }

        private static IEnumerable<NPC> EnumerateAgents()
        {
            var agentType = ModContent.NPCType<AIAgentNPC>();
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                var npc = Main.npc[i];
                if (npc != null && npc.active && npc.type == agentType)
                {
                    yield return npc;
                }
            }
        }

        private static string BuildUserPrompt(Player player, string text)
        {
            var agentCount = CountAgents();
            return $"Player: {player.name}\nCurrentAgents: {agentCount}\nRequest: {text}";
        }

        private static int CountAgents()
        {
            var count = 0;
            foreach (var _ in EnumerateAgents())
            {
                count++;
            }
            return count;
        }

        private static void SpawnAgents(int count, Player commander, CommandCaller caller, bool deleteExisting)
        {
            if (deleteExisting)
            {
                DeleteAllAgents(caller);
            }

            // Clamp count to reasonable range
            count = Math.Clamp(count, 0, 50);

            if (count == 0)
            {
                caller.Reply("All agents dismissed.", Color.Orange);
                return;
            }

            // Spawn agents at player position with spread
            Vector2 basePosition = commander.Bottom;
            for (int i = 0; i < count; i++)
            {
                float offsetX = (i - count / 2f) * 40f;  // Spread them out
                Vector2 spawnPos = basePosition + new Vector2(offsetX, -42f);
                string name = $"Agent {i + 1}";
                SpawnAgentAtPosition(spawnPos, name, new EntitySource_WorldGen(), 73);
            }

            caller.Reply($"Spawned {count} agent(s). Soul split into {count + 1} pieces.", Color.LightGreen);
        }

        private static void DeleteAllAgents(CommandCaller caller)
        {
            int deleted = 0;
            foreach (var npc in EnumerateAgents())
            {
                if (npc.ModNPC is AIAgentNPC)
                {
                    // Release all claims before deletion
                    MultiAgentCoordinator.ReleaseAllClaimsForAgent(npc.whoAmI);
                    MultiAgentCoordinator.ReleaseHellevator(npc.whoAmI);

                    npc.active = false;

                    if (Main.netMode == Terraria.ID.NetmodeID.Server)
                    {
                        Terraria.NetMessage.SendData(Terraria.ID.MessageID.SyncNPC, -1, -1, null, npc.whoAmI);
                    }

                    deleted++;
                }
            }

            if (deleted > 0)
            {
                caller.Reply($"Deleted {deleted} agent(s).", Color.Orange);
            }
        }

        private static void RouteToNearestAgents(CommandCaller caller, string commandText, int? targetCount)
        {
            Player commander = caller.Player;
            if (commander == null)
            {
                return;
            }

            // Get all active agents with their distances to the player
            var agentsWithDistance = new List<(NPC npc, AIAgentNPC agent, float distance)>();
            foreach (var npc in EnumerateAgents())
            {
                if (npc.ModNPC is AIAgentNPC agent)
                {
                    float distance = Vector2.Distance(npc.Center, commander.Center);
                    agentsWithDistance.Add((npc, agent, distance));
                }
            }

            if (agentsWithDistance.Count == 0)
            {
                caller.Reply("No agents available. Use '/ai spawn N agents' to create some.", Color.Red);
                return;
            }

            // Sort by distance (closest first)
            agentsWithDistance.Sort((a, b) => a.distance.CompareTo(b.distance));

            // Select N closest agents, or all if not specified
            int selectCount = targetCount.HasValue && targetCount > 0
                ? Math.Min(targetCount.Value, agentsWithDistance.Count)
                : agentsWithDistance.Count;

            int count = 0;
            float maxDistance = 0f;
            for (int i = 0; i < selectCount; i++)
            {
                var agentData = agentsWithDistance[i];
                agentData.agent.ReceiveCommand(commander, commandText);
                maxDistance = agentData.distance;
                count++;
            }

            if (targetCount.HasValue && targetCount > 0)
            {
                caller.Reply($"{count} closest agent(s) received command (within {maxDistance:F0}px)", Color.LightBlue);
            }
            else
            {
                caller.Reply($"All {count} agent(s) received command", Color.LightBlue);
            }
        }
    }

    internal sealed class CoordinatorResponse
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("command")]
        public string? Command { get; set; }

        [JsonPropertyName("action")]
        public string? Action { get; set; }

        [JsonPropertyName("count")]
        public int? Count { get; set; }

        [JsonPropertyName("deleteExisting")]
        public bool DeleteExisting { get; set; } = false;

        [JsonPropertyName("agentCount")]
        public int? AgentCount { get; set; }
    }

    internal sealed class AICommand : ModCommand
    {
        public override CommandType Type => CommandType.Chat;
        public override string Command => "ai";
        public override string Usage => "/ai <instruction>";
        public override string Description => "Routes chat requests for TerrarAI agents.";

        public override void Action(CommandCaller caller, string input, string[] args)
        {
            var commandText = args.Length == 0 ? string.Empty : string.Join(" ", args);
            if (string.IsNullOrWhiteSpace(commandText))
            {
                caller.Reply("Usage: /ai <instruction>", Color.LightGray);
                return;
            }

            // Handle recall command directly
            if (ChatCoordinator.IsRecallCommand(commandText))
            {
                var player = caller.Player;
                if (player != null)
                {
                    ChatCoordinator.RecallAllAgents(player, caller);
                }
                return;
            }

            ChatCoordinator.HandleInput(caller, commandText);
        }
    }
}
