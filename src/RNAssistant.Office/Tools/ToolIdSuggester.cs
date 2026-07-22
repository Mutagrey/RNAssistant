using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Tools
{
    internal static class ToolIdSuggester
    {
        public static List<string> Suggest(string requestedToolId, IReadOnlyList<ToolDefinition> knownTools, int limit)
        {
            var requestedTokens = Expand(Tokenize(requestedToolId));
            if (requestedTokens.Count == 0)
            {
                return new List<string>();
            }

            return (knownTools ?? new ToolDefinition[0])
                .Where(tool => tool != null && tool.Enabled && !string.IsNullOrWhiteSpace(tool.Id))
                .Select(tool => new { Tool = tool, Score = Score(requestedTokens, tool) })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Tool.Id.Length)
                .Take(Math.Max(1, limit))
                .Select(item => item.Tool.Id)
                .ToList();
        }

        private static int Score(ISet<string> requestedTokens, ToolDefinition tool)
        {
            var candidateTokens = Expand(Tokenize(
                (tool.Id ?? string.Empty) + " " +
                (tool.Name ?? string.Empty) + " " +
                (tool.Description ?? string.Empty)));
            return requestedTokens.Sum(token => candidateTokens.Contains(token) ? (token.Length <= 2 ? 1 : 3) : 0);
        }

        private static ISet<string> Expand(IEnumerable<string> tokens)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in tokens ?? new string[0])
            {
                var value = (token ?? string.Empty).Trim().ToLowerInvariant();
                if (value.Length == 0)
                {
                    continue;
                }
                result.Add(value);
                if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase) && value.Length > 3)
                {
                    result.Add(value.Substring(0, value.Length - 1));
                }
                if (value == "create" || value == "make" || value == "new") result.Add("add");
                if (value == "worksheet") result.Add("sheet");
                if (value == "diagram") result.Add("chart");
                if (value == "delete") result.Add("remove");
            }
            return result;
        }

        private static IEnumerable<string> Tokenize(string value)
        {
            var token = string.Empty;
            var previousWasLower = false;
            foreach (var ch in value ?? string.Empty)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    if (char.IsUpper(ch) && previousWasLower && token.Length > 0)
                    {
                        yield return token;
                        token = string.Empty;
                    }
                    token += char.ToLowerInvariant(ch);
                    previousWasLower = char.IsLower(ch);
                }
                else
                {
                    if (token.Length > 0) yield return token;
                    token = string.Empty;
                    previousWasLower = false;
                }
            }
            if (token.Length > 0) yield return token;
        }
    }
}
