using Terraria;
using Terraria.ModLoader;
using TerrarAI.Content.NPCs;

namespace TerrarAI.Content.Players
{
    public class SharedLifePlayer : ModPlayer
    {
        public override void PostUpdateMiscEffects()
        {
            int agentCount = CountActiveAgents();
            if (agentCount == 0)
            {
                return;  // No HP division when no agents exist
            }

            // Divide HP by number of agents only (not including player)
            int maxAllowedLife = Player.statLifeMax2 / agentCount;

            if (Player.statLife > maxAllowedLife)
            {
                Player.statLife = maxAllowedLife;
            }
        }

        private int CountActiveAgents()
        {
            int count = 0;
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC npc = Main.npc[i];
                if (npc.active && npc.ModNPC is AIAgentNPC)
                {
                    count++;
                }
            }
            return count;
        }
    }
}
