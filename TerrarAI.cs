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
                // tModLoader expects default key names that match the XNA enum casing (e.g. "J").
                // Using lowercase "j" caused it to fall back to "Unbound", so keep the label lowercase but register with "J".
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
