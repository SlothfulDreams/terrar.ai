using Microsoft.Xna.Framework;
using TerrarAI.Content.NPCs;
using TerrarAI.Content.Systems;
using Terraria;
using Terraria.GameInput;
using Terraria.Localization;
using Terraria.ModLoader;
using TerrarAIMod = TerrarAI.TerrarAI;

namespace TerrarAI.Common.Players
{
    public class CommandUIPlayer : ModPlayer
    {
        private const float MaxCommandDistance = 960f;

		public override void ProcessTriggers(TriggersSet triggersSet)
		{
			if (Main.dedServ || TerrarAIMod.CommandKeybind is not { } keybind)
			{
				return;
			}

			if (keybind.JustPressed)
			{
				TerrarAIMod.LogInfo($"[CommandUIPlayer] Command key pressed by {Player.name} (netMode={Main.netMode}).");
				CommandUISystem.Instance?.ToggleUI();
			}
		}

		internal CommandDispatchResult TrySendCommand(string commandText)
		{
			TerrarAIMod.LogInfo($"[CommandUIPlayer] Player {Player.name} submitted raw text: \"{commandText}\"");

			if (string.IsNullOrWhiteSpace(commandText))
			{
				TerrarAIMod.LogInfo("[CommandUIPlayer] Submission rejected: empty text.");
				return CommandDispatchResult.CreateFailure("Enter a command first.");
			}

			if (!ServerAuthority.IsServer)
			{
				TerrarAIMod.LogWarn("[CommandUIPlayer] Submission rejected: not running on server instance.");
				return CommandDispatchResult.CreateFailure("Only the host can issue commands right now.");
			}

			var agent = FindNearestAgent(MaxCommandDistance);
			if (agent == null)
			{
				TerrarAIMod.LogWarn("[CommandUIPlayer] Submission rejected: no agent nearby.");
				return CommandDispatchResult.CreateFailure("No agent nearby.");
			}

			var sanitized = commandText.Trim();
			TerrarAIMod.LogInfo($"[CommandUIPlayer] Dispatching command to agent {agent.NPC.whoAmI}: \"{sanitized}\"");
			agent.ReceiveCommand(Player, sanitized);
			var agentName = GetAgentName(agent);
			return CommandDispatchResult.CreateSuccess($"Sent command to {agentName}: \"{sanitized}\"");
		}

        internal AIAgentNPC? FindNearestAgent(float maxDistance)
        {
            var npcType = ModContent.NPCType<AIAgentNPC>();
            AIAgentNPC? best = null;
            var bestDistance = maxDistance;

            foreach (var npc in Main.npc)
            {
                if (!npc.active || npc.type != npcType || npc.ModNPC is not AIAgentNPC agent)
                {
                    continue;
                }

                var distance = Vector2.Distance(Player.Center, npc.Center);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = agent;
                }
            }

            return best;
        }

        private static string GetAgentName(AIAgentNPC agent)
        {
            var npc = agent.NPC;
            if (!string.IsNullOrWhiteSpace(npc.GivenName))
            {
                return npc.GivenName;
            }

            return Lang.GetNPCNameValue(npc.type);
        }

        internal readonly struct CommandDispatchResult
        {
            public bool Success { get; }
            public string Message { get; }
            public Color MessageColor { get; }

            private CommandDispatchResult(bool success, string message, Color color)
            {
                Success = success;
                Message = message;
                MessageColor = color;
            }

            public static CommandDispatchResult CreateSuccess(string message)
            {
                return new CommandDispatchResult(true, message, Color.LightGreen);
            }

            public static CommandDispatchResult CreateFailure(string message)
            {
                return new CommandDispatchResult(false, message, Color.OrangeRed);
            }
        }
    }
}
