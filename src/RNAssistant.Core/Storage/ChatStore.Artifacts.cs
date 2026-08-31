using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    public sealed partial class ChatStore
    {
        private void ExternalizeArtifacts(ChatSession session)
        {
            foreach (var artifact in session.Artifacts ?? new List<ChatArtifact>())
            {
                if (artifact == null || string.IsNullOrEmpty(artifact.InlineText)) continue;
                if (CanReuseArtifactBody(artifact)) continue;
                Interlocked.Increment(ref _artifactCasExternalizationCount);
                var reference = _blobs.StoreText(artifact.InlineText,
                    string.IsNullOrWhiteSpace(artifact.MimeType) ? "text/plain; charset=utf-8" : artifact.MimeType,
                    ArtifactBodyReference(artifact));
                artifact.ContentSha256 = reference.Sha256;
                artifact.ContentByteLength = reference.ByteLength;
                RememberArtifactBody(artifact);
            }
        }

        private bool CanReuseArtifactBody(ChatArtifact artifact)
        {
            return artifact != null && artifact.ContentByteLength.HasValue &&
                artifact.StorageContentByteLength.HasValue &&
                artifact.ContentByteLength.Value == artifact.StorageContentByteLength.Value &&
                artifact.StorageInlineTextTrusted &&
                string.Equals(artifact.ContentSha256, artifact.StorageContentSha256, StringComparison.OrdinalIgnoreCase) &&
                _blobs.HasStoredReference(ArtifactBodyReference(artifact));
        }

        private static ChatBlobReference ArtifactBodyReference(ChatArtifact artifact)
        {
            return artifact == null || !artifact.ContentByteLength.HasValue
                ? null
                : new ChatBlobReference
                {
                    Sha256 = artifact.ContentSha256,
                    ByteLength = artifact.ContentByteLength.Value,
                    ContentType = artifact.MimeType
                };
        }

        private static void RememberArtifactBody(ChatArtifact artifact)
        {
            if (artifact == null) return;
            artifact.StorageInlineTextTrusted = true;
            artifact.StorageContentSha256 = artifact.ContentSha256;
            artifact.StorageContentByteLength = artifact.ContentByteLength;
        }

        private static void EnsureChartArtifacts(ChatSession session)
        {
            if (session == null) return;
            session.Artifacts = session.Artifacts ?? new List<ChatArtifact>();
            foreach (var message in session.Messages ?? new List<ChatMessage>())
            {
                var activity = message == null ? null : message.Activity;
                JObject chart;
                if (activity == null || !ChartArtifactPayload.TryParse(activity.DataJson, out chart)) continue;
                message.ResourceRefs = message.ResourceRefs ?? new List<ResourceRef>();
                var referencedIds = new HashSet<string>(
                    ChatResourceUri.CurrentArtifactIds(session, message.ResourceRefs),
                    StringComparer.OrdinalIgnoreCase);
                var linked = session.Artifacts.LastOrDefault(item => item != null &&
                    referencedIds.Contains(item.Id) &&
                    string.Equals(item.Kind, ChatArtifactKinds.Chart, StringComparison.OrdinalIgnoreCase));
                var normalized = chart.ToString(Formatting.None);
                if (linked != null && string.Equals(linked.InlineText, normalized, StringComparison.Ordinal)) continue;
                var artifact = new ChatArtifact
                {
                    Kind = ChatArtifactKinds.Chart,
                    Title = (string)chart["title"] ?? (string)chart["Title"] ?? activity.Title ?? "Диаграмма",
                    MimeType = "application/vnd.rnassistant.chart+json",
                    SourceMessageId = message.Id,
                    RunId = message.RunId,
                    ParentArtifactId = linked == null ? null : linked.Id,
                    Revision = linked == null ? 1 : Math.Max(1, linked.Revision + 1),
                    InlineText = normalized
                };
                session.Artifacts.Add(artifact);
                if (linked != null) RemoveArtifactReference(message, linked.Id);
                message.ResourceRefs.Add(ChatResourceUri.CreateArtifactRevision(session, artifact));
            }
        }

        private void RebuildChartActivityProjection(ChatSession session)
        {
            if (session == null) return;
            foreach (var message in session.Messages ?? new List<ChatMessage>())
            {
                if (message == null || message.Activity == null) continue;
                var referencedIds = new HashSet<string>(
                    ChatResourceUri.CurrentArtifactIds(session, message.ResourceRefs),
                    StringComparer.OrdinalIgnoreCase);
                var artifact = (session.Artifacts ?? new List<ChatArtifact>()).LastOrDefault(item => item != null &&
                    referencedIds.Contains(item.Id) &&
                    string.Equals(item.Kind, ChatArtifactKinds.Chart, StringComparison.OrdinalIgnoreCase));
                if (artifact == null || !HydrateArtifact(artifact)) continue;
                message.Activity.DataJson = artifact.InlineText;
            }
        }

        private static void RemoveArtifactReference(ChatMessage message, string artifactId)
        {
            if (message == null || message.ResourceRefs == null || string.IsNullOrWhiteSpace(artifactId)) return;
            message.ResourceRefs.RemoveAll(reference =>
            {
                string ignoredSessionId;
                string referencedArtifactId;
                int ignoredRevision;
                return ChatResourceUri.TryParseArtifactRevision(
                    reference, out ignoredSessionId, out referencedArtifactId, out ignoredRevision) &&
                    string.Equals(referencedArtifactId, artifactId, StringComparison.OrdinalIgnoreCase);
            });
        }

        private void RebuildHtmlWorkspaceProjection(ChatSession session)
        {
            if (session == null) return;
            var activeId = session.ActiveHtmlArtifactId;
            if (string.IsNullOrWhiteSpace(activeId))
            {
                session.HtmlWorkspace = new HtmlWorkspace();
                session.HtmlWorkspaceRecovery = HtmlWorkspaceNavigationService.CreateRecoveryState(
                    session, HtmlWorkspaceRecoveryStatuses.Empty, null, null, null, null, true);
                return;
            }

            var active = FindHtmlArtifact(session, activeId);
            if (active == null)
            {
                session.HtmlWorkspace = new HtmlWorkspace();
                session.HtmlWorkspaceRecovery = HtmlWorkspaceNavigationService.CreateRecoveryState(
                    session,
                    HtmlWorkspaceRecoveryStatuses.Degraded,
                    HtmlWorkspaceRecoveryIssues.ActiveArtifactMissing,
                    "The active HTML workspace revision metadata is missing. Select another revision before editing.",
                    activeId,
                    activeId,
                    false);
                return;
            }
            if (!HydrateArtifact(active))
            {
                session.HtmlWorkspace = new HtmlWorkspace();
                session.HtmlWorkspaceRecovery = HtmlWorkspaceNavigationService.CreateRecoveryState(
                    session,
                    HtmlWorkspaceRecoveryStatuses.Degraded,
                    HtmlWorkspaceRecoveryIssues.ActiveBodyUnavailable,
                    "The active HTML workspace body is missing, corrupt, or cannot be decrypted. Select another revision before editing.",
                    activeId,
                    activeId,
                    false);
                return;
            }
            var activeSnapshot = ParseWorkspaceSnapshot(active);
            if (activeSnapshot == null)
            {
                session.HtmlWorkspace = new HtmlWorkspace();
                session.HtmlWorkspaceRecovery = HtmlWorkspaceNavigationService.CreateRecoveryState(
                    session,
                    HtmlWorkspaceRecoveryStatuses.Degraded,
                    HtmlWorkspaceRecoveryIssues.ActiveBodyInvalid,
                    "The active HTML workspace body is invalid. Select another revision before editing.",
                    activeId,
                    activeId,
                    false);
                return;
            }

            var workspace = HtmlWorkspaceCopyService.CreateWorkspaceFromSnapshot(activeSnapshot);
            workspace.UpdatedUtc = active.CreatedUtc;
            var current = active;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { active.Id };
            string issue = null;
            string message = null;
            string problemArtifactId = null;
            long historyCharacters = 0;
            while (!string.IsNullOrWhiteSpace(current.ParentArtifactId))
            {
                if (workspace.History.Count >= HtmlWorkspaceHistoryPolicy.MaxItems ||
                    historyCharacters >= HtmlWorkspaceHistoryPolicy.MaxContentCharacters)
                {
                    break;
                }
                problemArtifactId = current.ParentArtifactId;
                if (!visited.Add(problemArtifactId))
                {
                    issue = HtmlWorkspaceRecoveryIssues.LineageCycle;
                    message = "The HTML workspace revision lineage contains a cycle. The active revision is readable, but older undo history is incomplete.";
                    break;
                }
                current = FindHtmlArtifact(session, problemArtifactId);
                if (current == null)
                {
                    issue = HtmlWorkspaceRecoveryIssues.ParentArtifactMissing;
                    message = "An older HTML workspace revision is missing. The active revision is readable, but undo history is incomplete.";
                    break;
                }
                if (!HydrateArtifact(current))
                {
                    issue = HtmlWorkspaceRecoveryIssues.ParentBodyUnavailable;
                    message = "An older HTML workspace body is unavailable. The active revision is readable, but undo history is incomplete.";
                    break;
                }
                var snapshot = ParseWorkspaceSnapshot(current);
                if (snapshot == null)
                {
                    issue = HtmlWorkspaceRecoveryIssues.ParentBodyInvalid;
                    message = "An older HTML workspace body is invalid. The active revision is readable, but undo history is incomplete.";
                    break;
                }
                var snapshotCharacters = HtmlWorkspaceHistoryPolicy.EstimateContentCharacters(snapshot);
                if (snapshotCharacters > HtmlWorkspaceHistoryPolicy.MaxContentCharacters ||
                    historyCharacters + snapshotCharacters > HtmlWorkspaceHistoryPolicy.MaxContentCharacters)
                {
                    problemArtifactId = null;
                    break;
                }
                workspace.History.Add(snapshot);
                historyCharacters += snapshotCharacters;
            }

            workspace.RedoBranches = HtmlWorkspaceNavigationService.GetRedoBranches(session);
            session.HtmlWorkspace = workspace;
            session.HtmlWorkspaceRecovery = HtmlWorkspaceNavigationService.CreateRecoveryState(
                session,
                issue == null ? HtmlWorkspaceRecoveryStatuses.Healthy : HtmlWorkspaceRecoveryStatuses.Degraded,
                issue,
                message,
                active.Id,
                problemArtifactId,
                true);
        }

        private static HtmlWorkspaceSnapshot ParseWorkspaceSnapshot(ChatArtifact artifact)
        {
            if (artifact == null || string.IsNullOrWhiteSpace(artifact.InlineText)) return null;
            try
            {
                var snapshot = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(artifact.InlineText);
                if (snapshot == null) return null;
                snapshot.Id = artifact.Id;
                snapshot.Label = string.IsNullOrWhiteSpace(artifact.Title) ? "HTML workspace" : artifact.Title;
                snapshot.CreatedUtc = artifact.CreatedUtc;
                return snapshot;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private void RebuildContextCheckpointProjection(ChatSession session)
        {
            if (session == null) return;
            var checkpoints = new List<ContextCheckpoint>();
            foreach (var artifact in UniqueArtifacts(session)
                .Where(item => item != null &&
                    string.Equals(item.Kind, ChatArtifactKinds.Compaction, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.CreatedUtc))
            {
                if (!HydrateArtifact(artifact)) continue;
                try
                {
                    var checkpoint = JsonConvert.DeserializeObject<ContextCheckpoint>(artifact.InlineText);
                    if (checkpoint == null || string.IsNullOrWhiteSpace(checkpoint.ThroughMessageId)) continue;
                    checkpoint.Id = artifact.Id;
                    checkpoint.CreatedUtc = artifact.CreatedUtc;
                    checkpoints.Add(checkpoint);
                    var sourceMessage = (session.Messages ?? new List<ChatMessage>()).FirstOrDefault(item =>
                        item != null && string.Equals(item.Id, artifact.SourceMessageId, StringComparison.OrdinalIgnoreCase));
                    if (sourceMessage != null && sourceMessage.Activity != null &&
                        string.Equals(sourceMessage.Activity.Kind, "compaction", StringComparison.OrdinalIgnoreCase))
                    {
                        sourceMessage.Content = checkpoint.SummaryMarkdown;
                        sourceMessage.Activity.ResultMessage = checkpoint.SummaryMarkdown;
                        sourceMessage.Activity.DataJson = artifact.MetadataJson;
                    }
                }
                catch (JsonException)
                {
                }
            }
            session.ContextCheckpoints = checkpoints;
            if (!checkpoints.Any(item => string.Equals(item.Id, session.ActiveContextCheckpointId, StringComparison.OrdinalIgnoreCase)))
            {
                session.ActiveContextCheckpointId = null;
            }
        }

        private bool HydrateArtifact(ChatArtifact artifact)
        {
            if (artifact == null) return false;
            if (!string.IsNullOrEmpty(artifact.InlineText)) return true;
            if (string.IsNullOrWhiteSpace(artifact.ContentSha256) || !artifact.ContentByteLength.HasValue) return false;
            artifact.InlineText = _blobs.ReadText(ArtifactBodyReference(artifact));
            if (artifact.InlineText == null) return false;
            RememberArtifactBody(artifact);
            return true;
        }

        private static ChatArtifact FindArtifact(ChatSession session, string artifactId)
        {
            if (session == null || string.IsNullOrWhiteSpace(artifactId)) return null;
            var matches = (session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => item != null && string.Equals(
                    item.Id,
                    artifactId,
                    StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        private static ChatArtifact FindHtmlArtifact(ChatSession session, string artifactId)
        {
            var artifact = FindArtifact(session, artifactId);
            return artifact != null && string.Equals(artifact.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase)
                ? artifact
                : null;
        }

        private static bool ShouldHydrateForActiveSession(ChatArtifact artifact)
        {
            if (artifact == null || string.Equals(artifact.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var mimeType = artifact.MimeType ?? string.Empty;
            return mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                mimeType.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mimeType.IndexOf("xml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(artifact.Kind, ChatArtifactKinds.TaskList, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(artifact.Kind, ChatArtifactKinds.PlanDocument, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(artifact.Kind, ChatArtifactKinds.Markdown, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(artifact.Kind, ChatArtifactKinds.ToolResult, StringComparison.OrdinalIgnoreCase);
        }

        private static List<ChatArtifact> UniqueArtifacts(ChatSession session)
        {
            return (session == null ? new List<ChatArtifact>() : session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .Select(group => group.Single())
                .ToList();
        }

        private static void EnsureUniqueArtifactIdentities(ChatSession session)
        {
            var ambiguous = (session == null ? new List<ChatArtifact>() : session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() != 1);
            if (ambiguous != null)
            {
                throw new InvalidOperationException(
                    "Chat artifact identity is ambiguous and cannot be saved: " + ambiguous.Key);
            }
        }

    }
}
