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

        [DefaultValue("grok-4-fast-non-reasoning")]
        public string Model { get; set; } = "grok-4-fast-non-reasoning";

        [Range(0f, 2f)]
        [DefaultValue(0.7f)]
        public float Temperature { get; set; } = 0.7f;

        [DefaultValue("https://api.x.ai/v1/chat/completions")]
        public string BaseEndpoint { get; set; } = "https://api.x.ai/v1/chat/completions";

        [Header("Debugging")]
        [DefaultValue(false)]
        [Tooltip("Enable verbose logging of API requests and responses to the log file")]
        public bool EnableVerboseLogging { get; set; } = false;

        [DefaultValue(false)]
        [Tooltip("Show AI agent thought process in real-time as chat messages during planning")]
        public bool ShowAgentThoughts { get; set; } = false;

        [Header("Timeouts")]
        [Range(10, 300)]
        [DefaultValue(60)]
        [Tooltip("HTTP request timeout in seconds for xAI API calls")]
        public int RequestTimeoutSeconds { get; set; } = 60;

        [Range(30, 300)]
        [DefaultValue(90)]
        [Tooltip("Maximum time in seconds an agent can stay in Planning state before auto-failing")]
        public int MaxPlanningSeconds { get; set; } = 90;

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
