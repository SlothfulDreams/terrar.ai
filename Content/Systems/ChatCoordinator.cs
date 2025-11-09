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
        private const string RouterSystemPrompt = "You route Terraria chat requests. Respond with JSON: {\"type\":\"command\",\"command\":\"text\"} when the player wants an agent to act. Agents are always available - just route commands to them.";

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

                    ApplyCoordinatorResponse(caller, text, response);
                });
            });
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

            var agent = FindNearestAgent(player);
            if (agent?.ModNPC is not AIAgentNPC aiAgent)
            {
                caller.Reply("No agents available.", Color.OrangeRed);
                return;
            }

            aiAgent.ReceiveCommand(player, commandText);
            caller.Reply($"Sent command to {agent.GivenName ?? "agent"}.", Color.LightBlue);
        }

        private static void ApplyCoordinatorResponse(CommandCaller caller, string originalText, string responseText)
        {
            var coordinator = JsonSerializer.Deserialize<CoordinatorResponse>(responseText);
            if (coordinator == null)
            {
                RouteTool(caller, originalText);
                return;
            }

            var commandText = string.IsNullOrWhiteSpace(coordinator.Command) ? originalText : coordinator.Command!;
            RouteTool(caller, commandText);
        }

        private static NPC? FindNearestAgent(Player player)
        {
            NPC? best = null;
            var bestDistance = float.MaxValue;

            foreach (var npc in EnumerateAgents())
            {
                var distance = Vector2.DistanceSquared(player.Center, npc.Center);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = npc;
                }
            }

            return best;
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
    }

    internal sealed class CoordinatorResponse
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("command")]
        public string? Command { get; set; }
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

            ChatCoordinator.HandleInput(caller, commandText);
        }
    }
}

