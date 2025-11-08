using TerrarAI.Content.Systems;
using Terraria;
using Terraria.ModLoader;

namespace TerrarAI
{
    public class TerrarAI : Mod
    {
        internal static ModKeybind? CommandKeybind { get; private set; }
        internal static XAIClient? Client { get; private set; }

        public override void Load()
        {
            if (!Main.dedServ)
            {
                CommandKeybind = KeybindLoader.RegisterKeybind(this, "Open Command Panel", "J");
            }

            Client = new XAIClient();
        }

        public override void Unload()
        {
            CommandKeybind = null;
            Client?.Dispose();
            Client = null;
        }

        internal static XAIClient RequireClient()
        {
            return Client ??= new XAIClient();
        }
    }
}
