using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
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

            // Configure timeout from config
            var config = TerrarAI_Config.Get();
            _httpClient.Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds);
        }

        public async Task<string> SendChatCompletionAsync(string systemPrompt, string userPrompt, List<(string role, string content)> conversationHistory = null, CancellationToken cancellationToken = default)
        {
            var config = TerrarAI_Config.Get();
            var apiKey = config.GetEffectiveApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("Set an xAI API key in the TerrarAI config before making requests.");
            }

            if (config.EnableVerboseLogging)
            {
                ModContent.GetInstance<TerrarAI>().Logger.Info("[XAIClient] === API Request ===");
                ModContent.GetInstance<TerrarAI>().Logger.Info($"[XAIClient] Model: {config.Model}");
                ModContent.GetInstance<TerrarAI>().Logger.Info($"[XAIClient] Temperature: {config.Temperature}");
                ModContent.GetInstance<TerrarAI>().Logger.Info($"[XAIClient] System Prompt: {systemPrompt}");
                ModContent.GetInstance<TerrarAI>().Logger.Info($"[XAIClient] User Prompt: {userPrompt}");
            }

            var request = new HttpRequestMessage(HttpMethod.Post, config.BaseEndpoint)
            {
                Content = new StringContent(BuildPayload(config.Model, config.Temperature, systemPrompt, userPrompt, conversationHistory), Encoding.UTF8, "application/json")
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            catch (TaskCanceledException ex)
            {
                if (config.EnableVerboseLogging)
                {
                    ModContent.GetInstance<TerrarAI>().Logger.Warn($"[XAIClient] Request timed out after {config.RequestTimeoutSeconds} seconds");
                }
                throw new TimeoutException($"xAI API request timed out after {config.RequestTimeoutSeconds} seconds. Check your network connection and API endpoint.", ex);
            }
            catch (HttpRequestException ex)
            {
                if (config.EnableVerboseLogging)
                {
                    ModContent.GetInstance<TerrarAI>().Logger.Error($"[XAIClient] HTTP error: {ex.Message}");
                }
                throw new InvalidOperationException($"xAI API request failed: {ex.Message}. Check your API key and endpoint configuration.", ex);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (config.EnableVerboseLogging)
            {
                ModContent.GetInstance<TerrarAI>().Logger.Info($"[XAIClient] === API Response ===");
                ModContent.GetInstance<TerrarAI>().Logger.Info($"[XAIClient] Raw JSON: {document.RootElement.GetRawText()}");
            }

            if (document.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
                    message.TryGetProperty("content", out var contentElement))
                {
                    var content = contentElement.GetString() ?? string.Empty;

                    if (config.EnableVerboseLogging)
                    {
                        ModContent.GetInstance<TerrarAI>().Logger.Info($"[XAIClient] Parsed Content: {content}");
                    }

                    return content;
                }
            }

            var fallback = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (config.EnableVerboseLogging)
            {
                ModContent.GetInstance<TerrarAI>().Logger.Warn($"[XAIClient] No content found in standard response format, returning raw: {fallback}");
            }

            return fallback;
        }

        public async IAsyncEnumerable<string> SendChatCompletionStreamAsync(string systemPrompt, string userPrompt, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var config = TerrarAI_Config.Get();
            var apiKey = config.GetEffectiveApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("Set an xAI API key in the TerrarAI config before making requests.");
            }

            if (config.EnableVerboseLogging)
            {
                ModContent.GetInstance<TerrarAI>().Logger.Info("[XAIClient] === API Streaming Request ===");
                ModContent.GetInstance<TerrarAI>().Logger.Info($"[XAIClient] Model: {config.Model}");
                ModContent.GetInstance<TerrarAI>().Logger.Info($"[XAIClient] Temperature: {config.Temperature}");
                ModContent.GetInstance<TerrarAI>().Logger.Info($"[XAIClient] System Prompt: {systemPrompt}");
                ModContent.GetInstance<TerrarAI>().Logger.Info($"[XAIClient] User Prompt: {userPrompt}");
            }

            var request = new HttpRequestMessage(HttpMethod.Post, config.BaseEndpoint)
            {
                Content = new StringContent(BuildPayload(config.Model, config.Temperature, systemPrompt, userPrompt, stream: true), Encoding.UTF8, "application/json")
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            catch (TaskCanceledException ex)
            {
                if (config.EnableVerboseLogging)
                {
                    ModContent.GetInstance<TerrarAI>().Logger.Warn($"[XAIClient] Streaming request timed out after {config.RequestTimeoutSeconds} seconds");
                }
                throw new TimeoutException($"xAI API streaming request timed out after {config.RequestTimeoutSeconds} seconds. Check your network connection and API endpoint.", ex);
            }
            catch (HttpRequestException ex)
            {
                if (config.EnableVerboseLogging)
                {
                    ModContent.GetInstance<TerrarAI>().Logger.Error($"[XAIClient] HTTP error: {ex.Message}");
                }
                throw new InvalidOperationException($"xAI API streaming request failed: {ex.Message}. Check your API key and endpoint configuration.", ex);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Server-Sent Events format: "data: {json}"
                if (!line.StartsWith("data: ")) continue;

                var data = line.Substring(6); // Remove "data: " prefix
                if (data == "[DONE]") break;

                // Parse the JSON chunk
                using var document = JsonDocument.Parse(data);
                if (document.RootElement.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array &&
                    choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("content", out var contentElement))
                    {
                        var content = contentElement.GetString();
                        if (!string.IsNullOrEmpty(content))
                        {
                            if (config.EnableVerboseLogging)
                            {
                                ModContent.GetInstance<TerrarAI>().Logger.Info($"[XAIClient] Stream chunk: {content}");
                            }
                            yield return content;
                        }
                    }
                }
            }

            if (config.EnableVerboseLogging)
            {
                ModContent.GetInstance<TerrarAI>().Logger.Info("[XAIClient] === Streaming Complete ===");
            }
        }

        private static string BuildPayload(string model, float temperature, string systemPrompt, string userPrompt, List<(string role, string content)> conversationHistory = null, bool stream = false)
        {
            // Build messages array with conversation history
            var messagesList = new List<object>
            {
                new { role = "system", content = systemPrompt }
            };

            // Add conversation history if provided
            if (conversationHistory != null && conversationHistory.Count > 0)
            {
                foreach (var (role, content) in conversationHistory)
                {
                    messagesList.Add(new { role, content });
                }
            }

            // Add current user prompt last
            messagesList.Add(new { role = "user", content = userPrompt });

            var payload = new
            {
                model,
                temperature,
                stream,
                messages = messagesList.ToArray()
            };

            return JsonSerializer.Serialize(payload, SerializerOptions);
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
