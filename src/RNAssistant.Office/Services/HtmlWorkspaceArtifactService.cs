using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    internal static class HtmlWorkspaceArtifactService
    {
        public static string CaptureCurrent(ChatSession session, string title)
        {
            if (session == null) return string.Empty;
            if (session.HtmlWorkspaceRecovery != null && !session.HtmlWorkspaceRecovery.CanMutate)
            {
                if (HasContent(session.HtmlWorkspace))
                {
                    throw new InvalidOperationException("HTML workspace mutation is blocked until a healthy revision is selected.");
                }
                return session.ActiveHtmlArtifactId ?? session.HtmlWorkspaceRecovery.ActiveArtifactId ?? string.Empty;
            }
            session.HtmlWorkspace = HtmlArtifactToolExecutor.NormalizeWorkspace(session.HtmlWorkspace);
            session.Artifacts = session.Artifacts ?? new List<ChatArtifact>();
            var snapshot = HtmlWorkspaceCopyService.CaptureSnapshot(
                session.HtmlWorkspace,
                string.IsNullOrWhiteSpace(title) ? "HTML workspace" : title);
            var stateJson = SerializeState(snapshot);
            var current = session.Artifacts.FirstOrDefault(item => item != null &&
                string.Equals(item.Id, session.ActiveHtmlArtifactId, StringComparison.OrdinalIgnoreCase));
            if (current != null && SameState(current.InlineText, snapshot))
            {
                RebuildNavigation(session);
                return current.Id;
            }
            if (current == null && snapshot.Files.Count == 0 && snapshot.DataSources.Count == 0)
            {
                session.ActiveHtmlArtifactId = null;
                session.HtmlWorkspace.History = new List<HtmlWorkspaceSnapshot>();
                session.HtmlWorkspace.RedoBranches = new List<HtmlWorkspaceRedoBranch>();
                return string.Empty;
            }
            var artifact = new ChatArtifact
            {
                Kind = ChatArtifactKinds.HtmlWorkspace,
                Title = snapshot.Label,
                MimeType = "application/vnd.rnassistant.html-workspace+json",
                ParentArtifactId = current == null ? null : current.Id,
                Revision = NextRevision(session),
                InlineText = stateJson,
                MetadataJson = Metadata(snapshot, current)
            };
            session.Artifacts.Add(artifact);
            session.ActiveHtmlArtifactId = artifact.Id;
            RebuildNavigation(session);
            return artifact.Id;
        }

        public static bool Restore(ChatSession session, string artifactId)
        {
            if (session == null || string.IsNullOrWhiteSpace(artifactId) || session.Artifacts == null) return false;
            ValidateRevisionLineage(session);
            var artifact = session.Artifacts.FirstOrDefault(item => item != null &&
                string.Equals(item.Id, artifactId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase));
            if (artifact == null || string.IsNullOrWhiteSpace(artifact.InlineText)) return false;
            HtmlWorkspaceSnapshot snapshot;
            try
            {
                snapshot = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(artifact.InlineText);
            }
            catch (JsonException)
            {
                return false;
            }
            if (snapshot == null) return false;
            session.HtmlWorkspace = HtmlArtifactToolExecutor.NormalizeWorkspace(
                HtmlWorkspaceCopyService.CreateWorkspaceFromSnapshot(snapshot));
            session.ActiveHtmlArtifactId = artifact.Id;
            RebuildNavigation(session);
            return true;
        }

        public static void EnsureMutable(ChatSession session)
        {
            if (session == null)
            {
                throw new InvalidOperationException("Chat session is required.");
            }
            var recovery = session.HtmlWorkspaceRecovery;
            if (recovery != null && !recovery.CanMutate)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(recovery.Message)
                    ? "HTML workspace mutation is blocked until a healthy revision is selected."
                    : recovery.Message);
            }
            ValidateRevisionLineage(session);
        }

        public static string PrepareExport(ChatSession session, string expectedActiveArtifactId)
        {
            if (session == null) throw new InvalidOperationException("Chat session is required.");
            if (string.IsNullOrWhiteSpace(expectedActiveArtifactId) ||
                string.IsNullOrWhiteSpace(session.ActiveHtmlArtifactId) ||
                !string.Equals(
                expectedActiveArtifactId ?? string.Empty,
                session.ActiveHtmlArtifactId ?? string.Empty,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("HTML workspace changed; reload it before exporting.");
            }
            EnsureMutable(session);
            var artifactId = CaptureCurrent(session, "HTML export checkpoint");
            if (string.IsNullOrWhiteSpace(artifactId))
            {
                throw new InvalidOperationException("HTML workspace has no exportable revision.");
            }
            return artifactId;
        }

        public static void RebuildNavigation(ChatSession session)
        {
            if (session == null || session.HtmlWorkspace == null) return;
            session.HtmlWorkspace.History = new List<HtmlWorkspaceSnapshot>();
            session.HtmlWorkspace.RedoBranches = new List<HtmlWorkspaceRedoBranch>();
            var active = FindArtifact(session, session.ActiveHtmlArtifactId);
            if (string.IsNullOrWhiteSpace(session.ActiveHtmlArtifactId))
            {
                session.HtmlWorkspaceRecovery = HtmlWorkspaceNavigationService.CreateRecoveryState(
                    session, HtmlWorkspaceRecoveryStatuses.Empty, null, null, null, null, true);
                return;
            }
            if (active == null)
            {
                session.HtmlWorkspaceRecovery = HtmlWorkspaceNavigationService.CreateRecoveryState(
                    session,
                    HtmlWorkspaceRecoveryStatuses.Degraded,
                    HtmlWorkspaceRecoveryIssues.ActiveArtifactMissing,
                    "The active HTML workspace revision metadata is missing. Select another revision before editing.",
                    session.ActiveHtmlArtifactId,
                    session.ActiveHtmlArtifactId,
                    false);
                return;
            }
            var activeSnapshot = ParseSnapshot(active);
            if (activeSnapshot == null)
            {
                var missing = string.IsNullOrWhiteSpace(active.InlineText);
                session.HtmlWorkspaceRecovery = HtmlWorkspaceNavigationService.CreateRecoveryState(
                    session,
                    HtmlWorkspaceRecoveryStatuses.Degraded,
                    missing ? HtmlWorkspaceRecoveryIssues.ActiveBodyUnavailable : HtmlWorkspaceRecoveryIssues.ActiveBodyInvalid,
                    missing
                        ? "The active HTML workspace body is unavailable. Select another revision before editing."
                        : "The active HTML workspace body is invalid. Select another revision before editing.",
                    active.Id,
                    active.Id,
                    false);
                return;
            }
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { active.Id };
            var current = active;
            string issue = null;
            string message = null;
            string problemArtifactId = null;
            while (!string.IsNullOrWhiteSpace(current.ParentArtifactId))
            {
                problemArtifactId = current.ParentArtifactId;
                if (!visited.Add(problemArtifactId))
                {
                    issue = HtmlWorkspaceRecoveryIssues.LineageCycle;
                    message = "The HTML workspace revision lineage contains a cycle. The active revision is readable, but older undo history is incomplete.";
                    break;
                }
                current = FindArtifact(session, problemArtifactId);
                if (current == null)
                {
                    issue = HtmlWorkspaceRecoveryIssues.ParentArtifactMissing;
                    message = "An older HTML workspace revision is missing. The active revision is readable, but undo history is incomplete.";
                    break;
                }
                var snapshot = ParseSnapshot(current);
                if (snapshot == null)
                {
                    var missing = string.IsNullOrWhiteSpace(current.InlineText);
                    issue = missing ? HtmlWorkspaceRecoveryIssues.ParentBodyUnavailable : HtmlWorkspaceRecoveryIssues.ParentBodyInvalid;
                    message = missing
                        ? "An older HTML workspace body is unavailable. The active revision is readable, but undo history is incomplete."
                        : "An older HTML workspace body is invalid. The active revision is readable, but undo history is incomplete.";
                    break;
                }
                session.HtmlWorkspace.History.Add(snapshot);
            }
            session.HtmlWorkspace.History = HtmlWorkspaceHistoryPolicy.Trim(session.HtmlWorkspace.History);

            session.HtmlWorkspace.RedoBranches = HtmlWorkspaceNavigationService.GetRedoBranches(session);
            session.HtmlWorkspaceRecovery = HtmlWorkspaceNavigationService.CreateRecoveryState(
                session,
                issue == null ? HtmlWorkspaceRecoveryStatuses.Healthy : HtmlWorkspaceRecoveryStatuses.Degraded,
                issue,
                message,
                active.Id,
                problemArtifactId,
                true);
        }

        public static string CheckpointAtOrBefore(ChatSession session, IReadOnlyList<ChatMessage> messages, int index)
        {
            if (messages == null) return string.Empty;
            for (var current = Math.Min(index, messages.Count - 1); current >= 0; current--)
            {
                string id;
                var reference = messages[current] == null ? null : messages[current].HtmlWorkspaceCheckpoint;
                if (ChatResourceUri.TryGetArtifactId(session, reference, out id)) return id;
            }
            return string.Empty;
        }

        public static void StampUncheckpointed(ChatSession session, int startIndex, string checkpointId)
        {
            if (session == null || session.Messages == null || string.IsNullOrWhiteSpace(checkpointId)) return;
            var reference = ChatResourceUri.ResolveArtifactRevision(session, checkpointId);
            if (reference == null) return;
            for (var index = Math.Max(0, startIndex); index < session.Messages.Count; index++)
            {
                var message = session.Messages[index];
                if (message != null && message.HtmlWorkspaceCheckpoint == null)
                {
                    message.HtmlWorkspaceCheckpoint = new ResourceRef(reference.Uri, reference.Revision);
                }
            }
        }

        private static bool SameState(string existingJson, HtmlWorkspaceSnapshot candidate)
        {
            if (string.IsNullOrWhiteSpace(existingJson) || candidate == null) return false;
            try
            {
                var existing = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(existingJson);
                return existing != null &&
                    string.Equals(existing.ActiveFileId, candidate.ActiveFileId, StringComparison.OrdinalIgnoreCase) &&
                    JsonConvert.SerializeObject(existing.Files ?? new List<HtmlWorkspaceFile>()) == JsonConvert.SerializeObject(candidate.Files ?? new List<HtmlWorkspaceFile>()) &&
                    JsonConvert.SerializeObject(existing.DataSources ?? new List<HtmlWorkspaceDataSource>()) == JsonConvert.SerializeObject(candidate.DataSources ?? new List<HtmlWorkspaceDataSource>());
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static int NextRevision(ChatSession session)
        {
            var artifacts = ValidateRevisionLineage(session);
            if (artifacts.Count == 0) return 1;
            var maximum = artifacts.Max(item => item.Revision);
            if (maximum == int.MaxValue)
            {
                throw new InvalidOperationException("HTML workspace revision sequence is exhausted; start a new chat.");
            }
            return maximum + 1;
        }

        private static string Metadata(HtmlWorkspaceSnapshot snapshot, ChatArtifact parent)
        {
            var metadata = new JObject
            {
                ["activeFileId"] = snapshot == null ? string.Empty : snapshot.ActiveFileId,
                ["fileCount"] = snapshot == null ? 0 : snapshot.Files.Count,
                ["dataSourceCount"] = snapshot == null ? 0 : snapshot.DataSources.Count
            };
            if (parent != null && !string.IsNullOrWhiteSpace(parent.MetadataJson))
            {
                try
                {
                    var previous = JObject.Parse(parent.MetadataJson);
                    foreach (var name in new[]
                    {
                        "importedFromUri",
                        "importedFromArtifactId",
                        "importedSourceContentSha256",
                        "importedPath"
                    })
                    {
                        var value = previous.GetValue(name, StringComparison.OrdinalIgnoreCase);
                        if (value != null) metadata[name] = value.DeepClone();
                    }
                }
                catch (JsonException)
                {
                }
            }
            return metadata.ToString(Formatting.None);
        }

        private static List<ChatArtifact> ValidateRevisionLineage(ChatSession session)
        {
            var artifacts = (session == null || session.Artifacts == null
                    ? new List<ChatArtifact>()
                    : session.Artifacts)
                .Where(item => item != null && string.Equals(
                    item.Kind,
                    ChatArtifactKinds.HtmlWorkspace,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (artifacts.GroupBy(item => item.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Any(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() != 1) ||
                artifacts.Any(item => item.Revision < 1) ||
                artifacts.GroupBy(item => item.Revision).Any(group => group.Count() != 1))
            {
                throw new InvalidOperationException(
                    "HTML workspace revision lineage is ambiguous; start a new chat or reset the incompatible workspace.");
            }
            var byId = artifacts.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var artifact in artifacts)
            {
                ChatArtifact parent;
                if (!string.IsNullOrWhiteSpace(artifact.ParentArtifactId) &&
                    byId.TryGetValue(artifact.ParentArtifactId, out parent) &&
                    parent.Revision >= artifact.Revision)
                {
                    throw new InvalidOperationException(
                        "HTML workspace parent revision is not older than its child; start a new chat or reset the incompatible workspace.");
                }
            }
            return artifacts;
        }

        private static ChatArtifact FindArtifact(ChatSession session, string artifactId)
        {
            if (session == null || string.IsNullOrWhiteSpace(artifactId)) return null;
            return (session.Artifacts ?? new List<ChatArtifact>()).FirstOrDefault(item => item != null &&
                string.Equals(item.Id, artifactId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase));
        }

        private static HtmlWorkspaceSnapshot ParseSnapshot(ChatArtifact artifact)
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

        private static string SerializeState(HtmlWorkspaceSnapshot snapshot)
        {
            snapshot = snapshot ?? new HtmlWorkspaceSnapshot();
            return JsonConvert.SerializeObject(new
            {
                snapshot.ActiveFileId,
                Files = snapshot.Files ?? new List<HtmlWorkspaceFile>(),
                DataSources = snapshot.DataSources ?? new List<HtmlWorkspaceDataSource>()
            }, Formatting.None);
        }

        private static bool HasContent(HtmlWorkspace workspace)
        {
            return workspace != null &&
                ((workspace.Files != null && workspace.Files.Any(item => item != null)) ||
                 (workspace.DataSources != null && workspace.DataSources.Any(item => item != null)));
        }

    }
}
