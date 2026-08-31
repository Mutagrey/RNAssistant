using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Services
{
    internal static class ToolResultResourceService
    {
        public static ChatArtifact ExternalizeIfNeeded(
            ChatSession session,
            ToolCommand command,
            ToolResultMaterialization result,
            int inlineTokenBudget,
            AppSettings settings)
        {
            var data = result == null ? null : result.Result.DataJson;
            if (session == null || string.IsNullOrWhiteSpace(data) ||
                data.Length > ChatArtifactLimits.MaximumTextCharacters)
            {
                return null;
            }
            if (IsExactReadEvidence(command)) return null;

            JObject chart;
            if (TryParseChart(data, out chart))
            {
                return AddArtifact(
                    session,
                    command,
                    result,
                    ChatArtifactKinds.Chart,
                    (string)chart["title"] ?? (string)chart["Title"] ?? "Диаграмма",
                    "application/vnd.rnassistant.chart+json",
                    chart.ToString(Formatting.None));
            }

            if (EstimateProtocolDataTokens(data, settings) <= Math.Max(0, inlineTokenBudget))
            {
                return null;
            }

            var toolId = command == null ? string.Empty : command.ToolId ?? string.Empty;
            return AddArtifact(
                session,
                command,
                result,
                ChatArtifactKinds.ToolResult,
                string.IsNullOrWhiteSpace(toolId) ? "Tool result" : "Tool result · " + toolId,
                IsJson(data) ? "application/json" : "text/plain; charset=utf-8",
                data);
        }

        private static ChatArtifact AddArtifact(
            ChatSession session,
            ToolCommand command,
            ToolResultMaterialization result,
            string kind,
            string title,
            string mimeType,
            string content)
        {
            session.Artifacts = session.Artifacts ?? new List<ChatArtifact>();
            var existing = ReferencedArtifact(session, result, kind, content);
            if (existing != null)
            {
                result.IncludeResultResource(ChatResourceUri.CreateArtifactRevision(session, existing), existing.Kind);
                return existing;
            }

            var toolId = command == null ? string.Empty : command.ToolId ?? string.Empty;
            var artifact = new ChatArtifact
            {
                Kind = kind,
                Title = title,
                MimeType = mimeType,
                RunId = session.LastRun == null ? null : session.LastRun.RunId,
                InlineText = content,
                MetadataJson = JsonConvert.SerializeObject(new
                {
                    type = string.Equals(kind, ChatArtifactKinds.Chart, StringComparison.OrdinalIgnoreCase)
                        ? "rnassistant.chart"
                        : "rnassistant.toolResult",
                    toolId = toolId,
                    toolCallId = command == null ? null : command.ToolCallId,
                    status = result.Result.Status.ToString().ToLowerInvariant(),
                    originalCharacters = (content ?? string.Empty).Length
                })
            };
            session.Artifacts.Add(artifact);

            var reference = ChatResourceUri.CreateArtifactRevision(session, artifact);
            result.IncludeResultResource(reference, artifact.Kind);
            return artifact;
        }

        private static ChatArtifact ReferencedArtifact(
            ChatSession session,
            ToolResultMaterialization result,
            string kind,
            string content)
        {
            foreach (var reference in result == null
                ? new ResourceRef[0]
                : result.Result.Resources)
            {
                string artifactId;
                int revision;
                if (!ChatResourceUri.TryParseArtifactRevision(
                    session == null ? null : session.Id,
                    reference,
                    out artifactId,
                    out revision)) continue;
                var matches = (session.Artifacts ?? new List<ChatArtifact>())
                    .Where(item => item != null && string.Equals(
                        item.Id,
                        artifactId,
                        StringComparison.OrdinalIgnoreCase))
                    .Take(2)
                    .ToList();
                if (matches.Count > 1)
                {
                    throw new InvalidOperationException(
                        "Tool result artifact identity is ambiguous: " + artifactId);
                }
                var artifact = matches.Count == 1 ? matches[0] : null;
                if (artifact != null &&
                    Math.Max(1, artifact.Revision) == revision &&
                    string.Equals(artifact.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(artifact.InlineText ?? string.Empty, content ?? string.Empty, StringComparison.Ordinal))
                {
                    return artifact;
                }
            }
            return null;
        }

        internal static bool IsExactReadEvidence(ToolCommand command)
        {
            var id = command == null ? string.Empty : command.ToolId ?? string.Empty;
            return id.StartsWith("common.resources_", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("common.capabilities_", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsResourceEvidence(ToolCommand command)
        {
            var id = command == null ? string.Empty : command.ToolId ?? string.Empty;
            return id.StartsWith("common.resources_", StringComparison.OrdinalIgnoreCase);
        }

        private static int EstimateProtocolDataTokens(string data, AppSettings settings)
        {
            JToken parsed;
            try
            {
                parsed = JsonConvert.DeserializeObject<JToken>(data,
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None }) ?? JValue.CreateNull();
            }
            catch (JsonException)
            {
                parsed = new JValue(data);
            }
            return ModelContextBudget.EstimateTextTokens(parsed.ToString(Formatting.None), settings);
        }

        private static bool IsJson(string data)
        {
            try
            {
                JsonConvert.DeserializeObject<JToken>(data,
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryParseChart(string data, out JObject chart)
        {
            chart = null;
            try
            {
                chart = JsonConvert.DeserializeObject<JObject>(data ?? string.Empty,
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
                if (chart == null) return false;
                var type = (string)chart["type"] ?? (string)chart["Type"];
                if (string.Equals(type, "rnassistant.chart", StringComparison.OrdinalIgnoreCase)) return true;
                chart = null;
                return false;
            }
            catch (JsonException)
            {
                chart = null;
                return false;
            }
        }
    }
}
