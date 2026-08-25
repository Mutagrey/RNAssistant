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

        private void EnsureWorkspaceArtifact(ChatSession session)
        {
            if (session == null) return;
            var workspace = session.HtmlWorkspace ?? new HtmlWorkspace();
            var hasContent = (workspace.Files != null && workspace.Files.Any(item => item != null)) ||
                (workspace.DataSources != null && workspace.DataSources.Any(item => item != null));
            if (session.HtmlWorkspaceRecovery != null && !session.HtmlWorkspaceRecovery.CanMutate)
            {
                if (hasContent)
                {
                    throw new InvalidOperationException("HTML workspace mutation is blocked until a healthy revision is selected.");
                }
                return;
            }
            var current = FindArtifact(session, session.ActiveHtmlArtifactId);
            if (!hasContent && current == null) return;
            if (current != null) HydrateArtifact(current);

            var snapshot = HtmlWorkspaceCopyService.CaptureSnapshot(workspace, "HTML workspace");
            if (current != null && WorkspaceStateEquals(current.InlineText, snapshot)) return;
            var artifact = new ChatArtifact
            {
                Kind = ChatArtifactKinds.HtmlWorkspace,
                Title = "HTML workspace",
                MimeType = "application/vnd.rnassistant.html-workspace+json",
                ParentArtifactId = current == null ? null : current.Id,
                Revision = current == null ? 1 : Math.Max(1, current.Revision + 1),
                InlineText = SerializeWorkspaceState(snapshot),
                MetadataJson = JsonConvert.SerializeObject(new
                {
                    activeFileId = snapshot.ActiveFileId,
                    fileCount = snapshot.Files.Count,
                    dataSourceCount = snapshot.DataSources.Count
                })
            };
            session.Artifacts = session.Artifacts ?? new List<ChatArtifact>();
            session.Artifacts.Add(artifact);
            session.ActiveHtmlArtifactId = artifact.Id;
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
                message.ArtifactIds = message.ArtifactIds ?? new List<string>();
                var linked = session.Artifacts.LastOrDefault(item => item != null &&
                    message.ArtifactIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase) &&
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
                if (linked != null) message.ArtifactIds.RemoveAll(id =>
                    string.Equals(id, linked.Id, StringComparison.OrdinalIgnoreCase));
                message.ArtifactIds.Add(artifact.Id);
            }
        }

        private void RebuildChartActivityProjection(ChatSession session)
        {
            if (session == null) return;
            foreach (var message in session.Messages ?? new List<ChatMessage>())
            {
                if (message == null || message.Activity == null) continue;
                var artifact = (session.Artifacts ?? new List<ChatArtifact>()).LastOrDefault(item => item != null &&
                    (message.ArtifactIds ?? new List<string>()).Contains(item.Id, StringComparer.OrdinalIgnoreCase) &&
                    string.Equals(item.Kind, ChatArtifactKinds.Chart, StringComparison.OrdinalIgnoreCase));
                if (artifact == null || !HydrateArtifact(artifact)) continue;
                message.Activity.DataJson = artifact.InlineText;
            }
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

        private static bool WorkspaceStateEquals(string existingJson, HtmlWorkspaceSnapshot candidate)
        {
            if (string.IsNullOrWhiteSpace(existingJson) || candidate == null) return false;
            try
            {
                var existing = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(existingJson);
                return existing != null &&
                    string.Equals(existing.ActiveFileId, candidate.ActiveFileId, StringComparison.OrdinalIgnoreCase) &&
                    JToken.DeepEquals(JArray.FromObject(existing.Files ?? new List<HtmlWorkspaceFile>()),
                        JArray.FromObject(candidate.Files ?? new List<HtmlWorkspaceFile>())) &&
                    JToken.DeepEquals(JArray.FromObject(existing.DataSources ?? new List<HtmlWorkspaceDataSource>()),
                        JArray.FromObject(candidate.DataSources ?? new List<HtmlWorkspaceDataSource>()));
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string SerializeWorkspaceState(HtmlWorkspaceSnapshot snapshot)
        {
            snapshot = snapshot ?? new HtmlWorkspaceSnapshot();
            return JsonConvert.SerializeObject(new
            {
                snapshot.ActiveFileId,
                Files = snapshot.Files ?? new List<HtmlWorkspaceFile>(),
                DataSources = snapshot.DataSources ?? new List<HtmlWorkspaceDataSource>()
            }, Formatting.None);
        }

        private void RebuildContextCheckpointProjection(ChatSession session)
        {
            if (session == null) return;
            var checkpoints = new List<ContextCheckpoint>();
            foreach (var artifact in (session.Artifacts ?? new List<ChatArtifact>())
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
            return (session.Artifacts ?? new List<ChatArtifact>()).FirstOrDefault(item =>
                item != null && string.Equals(item.Id, artifactId, StringComparison.OrdinalIgnoreCase));
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
                string.Equals(artifact.Kind, ChatArtifactKinds.Plan, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(artifact.Kind, ChatArtifactKinds.Markdown, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(artifact.Kind, ChatArtifactKinds.ToolResult, StringComparison.OrdinalIgnoreCase);
        }

    }
}
