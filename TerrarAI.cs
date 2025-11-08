using TerrarAI.Content.Systems;
using Terraria;
using Terraria.ModLoader;

namespace TerrarAI
{
    public class TerrarAI : Mod
    {
        internal static TerrarAI? Instance { get; private set; }
        internal static ModKeybind? CommandKeybind { get; private set; }
        internal static XAIClient? Client { get; private set; }

        public override void Load()
        {
            Instance = this;

            if (!Main.dedServ)
            {
                CommandKeybind = KeybindLoader.RegisterKeybind(this, "Open Command Panel", "J");
            }

            Client = new XAIClient();
        }

        public override void Unload()
        {
            Instance = null;
            CommandKeybind = null;
            Client?.Dispose();
            Client = null;
        }

        internal static XAIClient RequireClient()
        {
            return Client ??= new XAIClient();
        }

        internal static void LogInfo(string message)
        {
            Instance?.Logger.Info(message);
        }

        internal static void LogWarn(string message)
        {
            Instance?.Logger.Warn(message);
        }

        internal static void LogError(string message)
        {
            Instance?.Logger.Error(message);
        }
    }
}
