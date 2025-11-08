using Microsoft.Xna.Framework;
using TerrarAI.Content.NPCs;
using TerrarAI.Content.Systems;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;

namespace TerrarAI.Common.Commands
{
    public class SpawnAgentCommand : ModCommand
    {
        public override CommandType Type => CommandType.Chat;

        public override string Command => "spawnagent";

        public override string Usage => "/spawnagent [optional name]";

        public override string Description => "Spawns a TerrarAI agent near you.";

        public override void Action(CommandCaller caller, string input, string[] args)
        {
            if (!ServerAuthority.EnsureServer(message => caller.Reply(message, Color.OrangeRed), "Only the server host can spawn agents."))
            {
                return;
            }

            var player = caller.Player;
            // Spawn at player's feet, offset to the side
            var spawnPosition = player.Bottom + new Vector2(40f, -42f);

            var source = new EntitySource_Parent(player);
            var npcType = ModContent.NPCType<AIAgentNPC>();
            var index = NPC.NewNPC(source, (int)spawnPosition.X, (int)spawnPosition.Y, npcType);

            if (index < 0)
            {
                caller.Reply("Failed to spawn TerrarAI agent.", Color.Red);
                return;
            }

            var npc = Main.npc[index];
            if (args.Length > 0)
            {
                npc.GivenName = string.Join(' ', args);
            }

            caller.Reply("TerrarAI agent spawned!", Color.LightGreen);
        }
    }
}
