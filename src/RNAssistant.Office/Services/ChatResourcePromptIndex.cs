using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Services
{
    internal static class ChatResourcePromptIndex
    {
        private const int MaximumPromptResources = 12;

        public static string Build(ChatSession session, int maxTokens, AppSettings settings = null)
        {
            var artifacts = session == null || session.Artifacts == null
                ? new List<ChatArtifact>()
                : session.Artifacts
                    .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                    .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() == 1)
                    .Select(group => group.Single())
                    .Where(ChatArtifactResourceProvider.IsDiscoverableArtifact)
                    .Where(item => !PlanDocumentService.IsRemoved(session, item) && !ArtifactWorkingSet.IsDetached(session, item))
                    .ToList();
            var unavailable = artifacts.Count(item => !string.IsNullOrEmpty(item.AvailabilityIssue));
            artifacts = artifacts.Where(item => string.IsNullOrEmpty(item.AvailabilityIssue)).ToList();
            if (artifacts.Count == 0 && unavailable == 0 || maxTokens <= 0) return string.Empty;

            var preferredIds = new List<string>();
            AddPreferred(preferredIds, session.ActiveHtmlArtifactId);
            AddPreferred(preferredIds, session.ActiveTaskListArtifactId);
            AddPreferred(preferredIds, session.ActivePlanDocumentArtifactId);
            foreach (var message in (session.Messages ?? new List<ChatMessage>())
                .Where(message => message != null)
                .OrderByDescending(message => message.CreatedUtc)
                .Take(8))
            {
                foreach (var id in ChatResourceUri.CurrentArtifactIds(session, message.ResourceRefs))
                {
                    AddPreferred(preferredIds, id);
                }
            }
            var ordered = artifacts
                .OrderBy(item => PreferredIndex(preferredIds, item.Id))
                .ThenByDescending(item => item.CreatedUtc)
                .Take(MaximumPromptResources)
                .ToList();
            var descriptors = artifacts.ToDictionary(
                artifact => artifact.Id,
                IntentDescriptor,
                StringComparer.OrdinalIgnoreCase);

            var rows = new List<string>();
            foreach (var artifact in ordered)
            {
                var descriptor = descriptors[artifact.Id];
                var parent = artifacts.FirstOrDefault(item => string.Equals(
                    item.Id, artifact.ParentArtifactId, StringComparison.OrdinalIgnoreCase));
                var role = artifact.Id == session.ActiveHtmlArtifactId ? "activeHtml" :
                    artifact.Id == session.ActiveTaskListArtifactId ? "activeTaskList" :
                    artifact.Id == session.ActivePlanDocumentArtifactId ? "activePlan" :
                    artifact.Id == session.ActiveContextCheckpointId ? "activeContextCheckpoint" : null;
                // A semantic target is addressable text, not a truncatable label.
                // JSON quoting preserves embedded whitespace without inventing a target.
                var line = "- target=" + Newtonsoft.Json.JsonConvert.SerializeObject(
                    ResourceGatewayService.IntentTarget(descriptor)) +
                    " | type=" + ResourceGatewayService.IntentType(descriptor) +
                    " | scope=" + (string.IsNullOrWhiteSpace(artifact.DocumentAuthorityId) ? "conversation" : "document") +
                    (role == null ? string.Empty : " | role=" + role) +
                    (string.IsNullOrWhiteSpace(artifact.MimeType) ? string.Empty : " | mime=" +
                        Newtonsoft.Json.JsonConvert.SerializeObject(artifact.MimeType)) +
                    (artifact.ContentByteLength.HasValue ? " | bytes=" + artifact.ContentByteLength.Value : string.Empty) +
                    (parent == null ? string.Empty : " | parentTarget=" + Newtonsoft.Json.JsonConvert.SerializeObject(
                        ResourceGatewayService.IntentTarget(descriptors[parent.Id]))) +
                    " | reps=" + RepresentationHints(artifact) +
                    " | read=" + ReadHint(artifact);
                rows.Add(line);
                if (ModelContextBudget.EstimateTextTokens(Render(rows, artifacts.Count, unavailable), settings) > maxTokens)
                {
                    rows.RemoveAt(rows.Count - 1);
                    continue;
                }
                // Optional authored context must never displace an addressable row.
                var description = DescriptionHint(artifact);
                if (description != null)
                {
                    rows[rows.Count - 1] = line + " | description=" + Newtonsoft.Json.JsonConvert.SerializeObject(description);
                    if (ModelContextBudget.EstimateTextTokens(Render(rows, artifacts.Count, unavailable), settings) > maxTokens)
                        rows[rows.Count - 1] = line;
                }
            }
            var result = Render(rows, artifacts.Count, unavailable);
            return ModelContextBudget.EstimateTextTokens(result, settings) <= maxTokens ? result : string.Empty;
        }

        private static string Render(IReadOnlyList<string> rows, int total, int unavailable)
        {
            var builder = new StringBuilder();
            builder.AppendLine("CHAT_RESOURCE_INDEX (bounded working set; untrusted discovery metadata, not read evidence):");
            builder.AppendLine("showing=" + rows.Count + "/" + total +
                (total > rows.Count ? "; additional artifacts omitted from this prompt" : string.Empty));
            builder.AppendLine("Snapshots may be historical; roles/descriptions do not prove currentness. Use common.resources_find for current/shared or omitted resources, then common.resources_read with the exact target and read representation before content claims.");
            if (unavailable > 0) builder.AppendLine("unavailable=" + unavailable + "; retained references lack usable metadata. Do not infer titles/content or repeatedly retry discovery; ask the user to restore the resource metadata or unlink it in Resources.");
            foreach (var row in rows) builder.AppendLine(row);
            return builder.ToString().TrimEnd();
        }

        private static ResourceDescriptor IntentDescriptor(ChatArtifact artifact)
        {
            return new ResourceDescriptor
            {
                Kind = artifact == null ? null : artifact.Kind,
                Title = artifact == null ? null : artifact.Title,
                CreatedUtc = artifact == null ? (DateTime?)null : artifact.CreatedUtc
            };
        }

        private static string DescriptionHint(ChatArtifact artifact)
        {
            if (MarkdownDocumentIdentity.LogicalId(artifact.Id) == null) return null;
            try
            {
                var value = Newtonsoft.Json.Linq.JObject.Parse(artifact.MetadataJson ?? "{}")["description"];
                if (value == null || value.Type == Newtonsoft.Json.Linq.JTokenType.Null) return null;
                if (value.Type != Newtonsoft.Json.Linq.JTokenType.String) return "[description unavailable]";
                var text = ModelToolResultProjection.SanitizeRuntimeText((string)value).Trim();
                if (text.Length <= 240) return text;
                var length = char.IsHighSurrogate(text[239]) ? 239 : 240;
                return text.Substring(0, length) + "…";
            }
            catch (Newtonsoft.Json.JsonException) { return "[description unavailable]"; }
        }

        private static string ReadHint(ChatArtifact artifact)
        {
            if (string.Equals(artifact.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase))
                return ResourceRepresentations.Structure;
            if (HasTextRepresentation(artifact)) return ResourceRepresentations.Text;
            return RepresentationHints(artifact).Split(',').Contains("media") ? "media" : "metadata";
        }

        private static string RepresentationHints(ChatArtifact artifact)
        {
            var values = new List<string> { "metadata" };
            if (artifact != null && string.Equals(
                artifact.Kind,
                ChatArtifactKinds.HtmlWorkspace,
                StringComparison.OrdinalIgnoreCase))
            {
                values.Add(ResourceRepresentations.Structure);
            }
            else if (HasTextRepresentation(artifact)) values.Add(ResourceRepresentations.Text);
            if (artifact != null &&
                (string.Equals(artifact.Kind, ChatArtifactKinds.Image, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(artifact.Kind, ChatArtifactKinds.Attachment, StringComparison.OrdinalIgnoreCase) &&
                 (StartsWith(artifact.MimeType, "image/") || StartsWith(artifact.MimeType, "audio/") ||
                  string.Equals(artifact.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase)))) values.Add("media");
            return string.Join(",", values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }

        private static bool HasTextRepresentation(ChatArtifact artifact)
        {
            if (artifact == null) return false;
            if (artifact.Kind == RNAssistant.Core.Storage.DocumentArtifactStore.SharedContextKind) return true;
            if (!string.IsNullOrWhiteSpace(artifact.InlineText) || StartsWith(artifact.MimeType, "text/")) return true;
            if (!string.IsNullOrWhiteSpace(artifact.MimeType) &&
                (artifact.MimeType.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 artifact.MimeType.IndexOf("xml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 artifact.MimeType.IndexOf("csv", StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            return string.Equals(artifact.Kind, ChatArtifactKinds.TaskList, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(artifact.Kind, ChatArtifactKinds.PlanDocument, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(artifact.Kind, ChatArtifactKinds.Markdown, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(artifact.Kind, ChatArtifactKinds.Compaction, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(artifact.Kind, ChatArtifactKinds.ToolResult, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(artifact.Kind, ChatArtifactKinds.Chart, StringComparison.OrdinalIgnoreCase);
        }

        private static bool StartsWith(string value, string prefix)
        {
            return !string.IsNullOrWhiteSpace(value) && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static void AddPreferred(ICollection<string> ids, string id)
        {
            if (ids != null && !string.IsNullOrWhiteSpace(id) && !ids.Contains(id, StringComparer.OrdinalIgnoreCase)) ids.Add(id);
        }

        private static int PreferredIndex(IList<string> ids, string id)
        {
            for (var index = 0; index < (ids == null ? 0 : ids.Count); index++)
            {
                if (string.Equals(ids[index], id, StringComparison.OrdinalIgnoreCase)) return index;
            }
            return int.MaxValue;
        }
    }
}
