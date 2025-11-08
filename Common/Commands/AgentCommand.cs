using System.Linq;
using Microsoft.Xna.Framework;
using TerrarAI.Content.NPCs;
using TerrarAI.Content.Systems;
using Terraria;
using Terraria.ModLoader;
using Terraria.Localization;

namespace TerrarAI.Common.Commands
{
    public class AgentCommand : ModCommand
    {
        public override CommandType Type => CommandType.Chat;

        public override string Command => "action";

        public override string Usage => "/action <instruction text>";

        public override string Description => "Sends a natural-language instruction to the nearest TerrarAI agent.";

        public override void Action(CommandCaller caller, string input, string[] args)
        {
            if (args.Length == 0)
            {
                caller.Reply("Usage: /action <instruction text>", Color.OrangeRed);
                return;
            }

            if (!ServerAuthority.EnsureServer(message => caller.Reply(message, Color.OrangeRed), "Only the server host can issue commands right now."))
            {
                return;
            }

            var player = caller.Player;
            var agent = FindNearestAgent(player.Center);

            if (agent == null)
            {
                caller.Reply("No TerrarAI agents nearby. Create one with /create.", Color.OrangeRed);
                return;
            }

            var command = string.Join(' ', args);
            agent.ReceiveCommand(player, command);
            var name = GetAgentName(agent);
            caller.Reply($"Sent command to {name}: \"{command}\"", Color.LightGreen);
        }

        private static string GetAgentName(AIAgentNPC agent)
        {
            var npc = agent.NPC;
            return string.IsNullOrWhiteSpace(npc.GivenName) ? Lang.GetNPCNameValue(npc.type) : npc.GivenName;
        }

        private static AIAgentNPC? FindNearestAgent(Vector2 position)
        {
            var npcType = ModContent.NPCType<AIAgentNPC>();
            AIAgentNPC? best = null;
            float bestDistance = float.MaxValue;

            foreach (var npc in Main.npc.Where(n => n.active && n.type == npcType))
            {
                var distance = Vector2.Distance(position, npc.Center);
                if (distance < bestDistance && npc.ModNPC is AIAgentNPC agent)
                {
                    bestDistance = distance;
                    best = agent;
                }
            }

            return best;
        }
    }
}
