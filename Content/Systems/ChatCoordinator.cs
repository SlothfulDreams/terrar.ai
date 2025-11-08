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
        private const string RouterSystemPrompt = "You route Terraria chat requests. Respond with JSON: {\"type\":\"create\",\"count\":N} to spawn that many agents (1-8), {\"type\":\"remove\",\"all\":true|false} to despawn, or {\"type\":\"command\",\"command\":\"text\"} when the player wants an agent to act. Default count is 1. Only choose remove when the player clearly wants to despawn agents.";
        private static int _nameSeed = 1;

        public override void OnWorldLoad()
        {
            _nameSeed = 1;
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

        internal static void CreateAgents(CommandCaller caller, int count)
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

            var created = new List<string>();
            count = Math.Clamp(count, 1, 8);

            for (int i = 0; i < count; i++)
            {
                var spawnOffset = new Vector2(48f * player.direction + (i * 16f), -16f);
                var spawnPos = player.Center + spawnOffset;
                var index = NPC.NewNPC(new EntitySource_DebugCommand("TerrarAI:ChatCoordinator"), (int)spawnPos.X, (int)spawnPos.Y, ModContent.NPCType<AIAgentNPC>());
                if (index < 0 || index >= Main.maxNPCs)
                {
                    continue;
                }

                var npc = Main.npc[index];
                npc.direction = player.direction;
                npc.GivenName = $"Agent {_nameSeed++}";
                if (npc.ModNPC is AIAgentNPC agentNpc)
                {
                    agentNpc.SetPlayerAppearance(player);
                }
                npc.netUpdate = true;
                created.Add(npc.GivenName ?? "Agent");
            }

            if (created.Count == 0)
            {
                caller.Reply("Failed to create agent.", Color.OrangeRed);
            }
            else if (created.Count == 1)
            {
                caller.Reply($"Created {created[0]}.", Color.LightGreen);
            }
            else
            {
                caller.Reply($"Created {created.Count} agents.", Color.LightGreen);
            }
        }

        internal static void RemoveAgent(CommandCaller caller, bool removeAll)
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

            if (removeAll)
            {
                var removed = 0;
                foreach (var agent in EnumerateAgents())
                {
                    removed++;
                    Despawn(agent);
                }

                caller.Reply(removed == 0 ? "No agents to remove." : $"Removed {removed} agent{(removed == 1 ? string.Empty : "s")}.", Color.LightSalmon);
                return;
            }

            var nearest = FindNearestAgent(player);
            if (nearest == null)
            {
                caller.Reply("No agents nearby.", Color.OrangeRed);
                return;
            }

            var name = nearest.GivenName ?? "Agent";
            Despawn(nearest);
            caller.Reply($"Removed {name}.", Color.LightSalmon);
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
                caller.Reply("Create an agent first by requesting one.", Color.OrangeRed);
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

            var type = coordinator.Type?.ToLowerInvariant();
            switch (type)
            {
                case "create":
                    CreateAgents(caller, coordinator.Count <= 0 ? 1 : coordinator.Count);
                    break;
                case "remove":
                    RemoveAgent(caller, coordinator.All);
                    break;
                default:
                    var commandText = string.IsNullOrWhiteSpace(coordinator.Command) ? originalText : coordinator.Command!;
                    RouteTool(caller, commandText);
                    break;
            }
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

        private static void Despawn(NPC npc)
        {
            npc.StrikeInstantKill();
            npc.netUpdate = true;
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

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("all")]
        public bool All { get; set; }

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

