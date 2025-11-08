using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Terraria.ModLoader;

namespace TerrarAI.Content.Systems
{
	public sealed class XAIClient : IDisposable
	{
		private readonly HttpClient _httpClient;
		private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

		public XAIClient(HttpClient? httpClient = null)
		{
			_httpClient = httpClient ?? new HttpClient();
		}

		public async Task<string> SendChatCompletionAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
		{
			var config = TerrarAI_Config.Get();
			if (string.IsNullOrWhiteSpace(config.ApiKey))
			{
				throw new InvalidOperationException("Set an xAI API key in the TerrarAI config before making requests.");
			}

			var request = new HttpRequestMessage(HttpMethod.Post, config.BaseEndpoint)
			{
				Content = new StringContent(BuildPayload(config.Model, config.Temperature, systemPrompt, userPrompt), Encoding.UTF8, "application/json")
			};

			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey.Trim());

			var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
			response.EnsureSuccessStatusCode();

			await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
			using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

			if (document.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
			{
				var choice = choices[0];
				if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
				    message.TryGetProperty("content", out var contentElement))
				{
					return contentElement.GetString() ?? string.Empty;
				}
			}

			return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		}

		private static string BuildPayload(string model, float temperature, string systemPrompt, string userPrompt)
		{
			var payload = new
			{
				model,
				temperature,
				messages = new[]
				{
					new { role = "system", content = systemPrompt },
					new { role = "user", content = userPrompt }
				}
			};

			return JsonSerializer.Serialize(payload, SerializerOptions);
		}

		public void Dispose()
		{
			_httpClient.Dispose();
		}
	}
}
