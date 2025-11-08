using System.ComponentModel;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

namespace TerrarAI
{
	public class TerrarAI_Config : ModConfig
	{
		public override ConfigScope Mode => ConfigScope.ServerSide;

		[Header("xAI")]
		[Label("xAI API Key")]
		[DefaultValue("")]
		[Tooltip("Set this on the server or single-player host. Leave blank on clients.")]
		public string ApiKey { get; set; } = string.Empty;

		[Label("Model")]
		[DefaultValue("grok-beta")]
		public string Model { get; set; } = "grok-beta";

		[Label("Temperature")]
		[Range(0f, 2f)]
		[DefaultValue(0.7f)]
		public float Temperature { get; set; } = 0.7f;

		[Label("xAI Endpoint")]
		[DefaultValue("https://api.x.ai/v1/chat/completions")]
		public string BaseEndpoint { get; set; } = "https://api.x.ai/v1/chat/completions";

		internal static TerrarAI_Config Get() => ModContent.GetInstance<TerrarAI_Config>();
	}
}
