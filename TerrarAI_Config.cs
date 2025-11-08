using System;
using System.ComponentModel;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

namespace TerrarAI
{
    public class TerrarAI_Config : ModConfig
    {
        public override ConfigScope Mode => ConfigScope.ServerSide;

        [Header("xAI")]
        [DefaultValue("")]
        public string ApiKey { get; set; } = string.Empty;

        [DefaultValue("grok-4-fast-reasoning")]
        public string Model { get; set; } = "grok-4-fast-reasoning";

        [Range(0f, 2f)]
        [DefaultValue(0.7f)]
        public float Temperature { get; set; } = 0.7f;

        [DefaultValue("https://api.x.ai/v1/chat/completions")]
        public string BaseEndpoint { get; set; } = "https://api.x.ai/v1/chat/completions";

        internal static TerrarAI_Config Get() => ModContent.GetInstance<TerrarAI_Config>();

        internal string GetEffectiveApiKey()
        {
            if (!string.IsNullOrWhiteSpace(ApiKey))
            {
                return ApiKey;
            }

            return Environment.GetEnvironmentVariable("XAI_TOKEN") ?? string.Empty;
        }
    }
}
