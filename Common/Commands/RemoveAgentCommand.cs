using System;
using TerrarAI.Content.Systems;
using Terraria.ModLoader;

namespace TerrarAI.Common.Commands
{
    public class RemoveAgentCommand : ModCommand
    {
        public override CommandType Type => CommandType.Chat;

        public override string Command => "remove";

        public override string Usage => "/remove [all]";

        public override string Description => "Removes TerrarAI agents near you.";

        public override void Action(CommandCaller caller, string input, string[] args)
        {
            var removeAll = args.Length > 0 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase);
            ChatCoordinator.RemoveAgent(caller, removeAll);
        }
    }
}

