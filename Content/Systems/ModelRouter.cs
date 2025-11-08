using System;
using System.Collections.Generic;
using System.Linq;

namespace TerrarAI.Content.Systems
{
    internal enum ModelRouteType
    {
        Fast,
        Reasoning
    }

    internal readonly record struct ModelRouteSelection(string Model, float Temperature, ModelRouteType RouteType, string Reason);

    internal static class ModelRouter
    {
        public static ModelRouteSelection SelectModel(string userPrompt, List<(string role, string content)>? history)
        {
            var config = TerrarAI_Config.Get();
            var fallback = new ModelRouteSelection(config.Model, config.Temperature, ModelRouteType.Fast, "Router disabled");

            if (!config.EnableModelRouter || string.IsNullOrWhiteSpace(config.ReasoningModel))
            {
                return fallback;
            }

            var text = userPrompt ?? string.Empty;
            int wordCount = CountWords(text);
            bool hasMultiStepIndicators = ContainsMultiStepSignals(text) || HistoryIndicatesFailure(history);
            bool keywordHit = ContainsKeyword(text, config.RouterComplexKeywords);

            if (keywordHit) wordCount += 50;
            if (hasMultiStepIndicators) wordCount += 30;

            if (wordCount >= config.RouterWordThreshold)
            {
                return new ModelRouteSelection(
                    config.ReasoningModel,
                    config.ReasoningTemperature,
                    ModelRouteType.Reasoning,
                    $"Complexity score {wordCount} exceeded threshold {config.RouterWordThreshold}");
            }

            return fallback with { Reason = $"Complexity score {wordCount} below threshold {config.RouterWordThreshold}" };
        }

        private static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            var span = text.AsSpan();
            int count = 0;
            bool inWord = false;

            foreach (char c in span)
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (!inWord)
                    {
                        count++;
                        inWord = true;
                    }
                }
                else
                {
                    inWord = false;
                }
            }

            return count;
        }

        private static bool ContainsKeyword(string text, string keywordList)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(keywordList))
            {
                return false;
            }

            var comparer = StringComparison.OrdinalIgnoreCase;
            var tokens = keywordList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return tokens.Any(token => text.Contains(token, comparer));
        }

        private static bool ContainsMultiStepSignals(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return text.Contains("step", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("plan", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("multiple", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("complex", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HistoryIndicatesFailure(List<(string role, string content)>? history)
        {
            if (history == null || history.Count == 0)
            {
                return false;
            }

            var comparer = StringComparison.OrdinalIgnoreCase;
            foreach (var (_, content) in history)
            {
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                if (content.Contains("failed", comparer) || content.Contains("error", comparer) || content.Contains("stuck", comparer))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
