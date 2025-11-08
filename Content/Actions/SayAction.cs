using Microsoft.Xna.Framework;
using TerrarAI.Content.Systems;
using Terraria;
using Terraria.Chat;
using Terraria.ID;
using Terraria.Localization;

namespace TerrarAI.Content.Actions
{
    public sealed class SayAction : AgentAction
    {
        private readonly string _text;
        private bool _completed;

        public SayAction(string text)
        {
            _text = text.Trim();
        }

        public override string Name => "say";

        public override AgentActionResult Execute(AgentActionContext context)
        {
            if (_completed)
            {
                return AgentActionResult.Success();
            }

            if (!ServerAuthority.IsServer)
            {
                return AgentActionResult.Failure("SayAction must run on the server.");
            }

            var message = string.IsNullOrWhiteSpace(_text) ? "..." : _text;
            var networkText = NetworkText.FromLiteral(message);

            if (Main.netMode == NetmodeID.Server)
            {
                ChatHelper.BroadcastChatMessage(networkText, Color.LightGoldenrodYellow);
            }
            else
            {
                Main.NewText(message, Color.LightGoldenrodYellow);
            }

            _completed = true;
            return AgentActionResult.Success($"Said \"{message}\"");
        }

        public override void Reset()
        {
            _completed = false;
        }
    }
}
