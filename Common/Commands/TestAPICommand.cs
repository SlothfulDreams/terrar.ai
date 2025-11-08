using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Systems;
using Terraria;
using Terraria.ModLoader;

namespace TerrarAI.Common.Commands
{
    public class TestAPICommand : ModCommand
    {
        public override CommandType Type => CommandType.Chat;

        public override string Command => "testxai";

        public override string Usage => "/testxai";

        public override string Description => "Checks whether the TerrarAI xAI connection works.";

        public override void Action(CommandCaller caller, string input, string[] args)
        {
            if (!ServerAuthority.EnsureServer(message => caller.Reply(message, Color.OrangeRed), "Only the server host can run /testxai."))
            {
                return;
            }

            var config = TerrarAI_Config.Get();
            var apiKey = config.GetEffectiveApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                caller.Reply("Set an xAI API key in Mod Configuration first.", Color.OrangeRed);
                return;
            }

            caller.Reply("Contacting xAI...", Color.LightGreen);

            _ = Task.Run(async () =>
            {
                try
                {
                    var systemPrompt = "You are TerrarAI, a cheerful helper for Terraria players.";
                    var response = await TerrarAI.RequireClient().SendChatCompletionAsync(systemPrompt, "Say hello!", CancellationToken.None).ConfigureAwait(false);

                    ServerAuthority.QueueMainThread(() =>
                    {
                        caller.Reply($"xAI replied: {response}", Color.LightBlue);
                    });
                }
                catch (Exception ex)
                {
                    ServerAuthority.QueueMainThread(() =>
                    {
                        caller.Reply($"xAI request failed: {ex.Message}", Color.Red);
                    });
                }
            });
        }
    }
}
